using System.Collections.Concurrent;
using System.Text.Json;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Application.Workflow;
using MarketplaceCatalogConsolidator.Domain;
using MarketplaceCatalogConsolidator.Infrastructure.Storage;

namespace MarketplaceCatalogConsolidator.IntegrationTests;

public sealed class ConsolidationWorkflowTests
{
    [Fact]
    public async Task StartupRecoveryRequeuesProcessingAndPreservesOutcomesIdempotently()
    {
        using var fixture = new WorkflowFixture();
        var upload = await fixture.AddUploadAsync(UploadStatus.Processing, receivedCount: 2);
        var priorOutcome = CreateItem(upload.Id, 0, UploadItemStatus.Cleaned, "Already committed.");
        await fixture.ItemStore.SaveProgressAsync(priorOutcome);
        var recovery = fixture.CreateRecoveryService();

        Assert.Equal(1, await recovery.RecoverAsync());
        Assert.Equal(0, await recovery.RecoverAsync());

        var recoveredUpload = await fixture.UploadStore.FindByIdAsync(upload.Id);
        Assert.NotNull(recoveredUpload);
        Assert.Equal(UploadStatus.Queued, recoveredUpload.Status);
        Assert.Null(recoveredUpload.CompletedAtUtc);
        Assert.Equal(new[] { priorOutcome }, await fixture.ItemStore.GetByUploadIdAsync(upload.Id));
    }

    [Fact]
    public async Task QueuedUploadIsClaimedOnceAndCompletesWithCalculatedSummary()
    {
        using var fixture = new WorkflowFixture();
        var upload = await fixture.AddUploadAsync(UploadStatus.Queued, receivedCount: 3);
        var processor = new DeterministicProcessor((_, index, _) => Task.FromResult(index switch
        {
            0 => new UploadItemProcessingResult(UploadItemStatus.Approved, "Approved."),
            1 => new UploadItemProcessingResult(UploadItemStatus.Cleaned, "Cleaned."),
            _ => new UploadItemProcessingResult(UploadItemStatus.Rejected, "Rejected.")
        }));
        var workflow = fixture.CreateWorkflow(processor);

        Assert.True(await workflow.ProcessNextAsync());
        Assert.False(await workflow.ProcessNextAsync());

        var completed = await fixture.UploadStore.FindByIdAsync(upload.Id);
        Assert.NotNull(completed);
        Assert.Equal(UploadStatus.CompletedWithRejections, completed.Status);
        Assert.Equal(1, completed.ApprovedCount);
        Assert.Equal(1, completed.CleanedCount);
        Assert.Equal(1, completed.RejectedCount);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Equal(new[] { 0, 1, 2 }, processor.ProcessedIndices);
    }

    [Fact]
    public async Task UploadWithoutRejectedItemsCompletesNormally()
    {
        using var fixture = new WorkflowFixture();
        var upload = await fixture.AddUploadAsync(UploadStatus.Queued, receivedCount: 2);
        var processor = new DeterministicProcessor((_, index, _) => Task.FromResult(new UploadItemProcessingResult(
            index == 0 ? UploadItemStatus.Approved : UploadItemStatus.Cleaned,
            "Accepted.")));

        Assert.True(await fixture.CreateWorkflow(processor).ProcessNextAsync());

        var completed = await fixture.UploadStore.FindByIdAsync(upload.Id);
        Assert.NotNull(completed);
        Assert.Equal(UploadStatus.Completed, completed.Status);
        Assert.Equal((1, 1, 0), (completed.ApprovedCount, completed.CleanedCount, completed.RejectedCount));
    }

    [Fact]
    public async Task UnavailableProcessorLeavesQueuedWorkEligible()
    {
        using var fixture = new WorkflowFixture();
        var upload = await fixture.AddUploadAsync(UploadStatus.Queued, receivedCount: 1);

        Assert.False(await fixture.CreateWorkflow(new UnavailableProcessor()).ProcessNextAsync());

        var stillQueued = await fixture.UploadStore.FindByIdAsync(upload.Id);
        Assert.NotNull(stillQueued);
        Assert.Equal(UploadStatus.Queued, stillQueued.Status);
        Assert.Empty(await fixture.ItemStore.GetByUploadIdAsync(upload.Id));
    }

    [Fact]
    public async Task WorkflowSerializesAcrossConcurrentCallersAndProcessesUploadsInOrder()
    {
        using var fixture = new WorkflowFixture();
        var first = await fixture.AddUploadAsync(UploadStatus.Queued, receivedCount: 2);
        var second = await fixture.AddUploadAsync(UploadStatus.Queued, receivedCount: 1, startedAtUtc: first.StartedAtUtc.AddSeconds(1));
        var processor = new GatedProcessor(first.Id);
        var observedLock = new SignalingWorkflowLock(fixture.WorkflowLock);
        var firstWorkflow = fixture.CreateWorkflow(processor, observedLock);
        var secondWorkflow = fixture.CreateWorkflow(processor, observedLock);

        var firstTask = firstWorkflow.ProcessNextAsync();
        await processor.FirstItemEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = secondWorkflow.ProcessNextAsync();
        await observedLock.SecondAcquireAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, processor.CallsFor(first.Id));
        Assert.Equal(0, processor.CallsFor(second.Id));

        processor.ReleaseFirstItem();
        Assert.True(await firstTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await secondTask.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, processor.MaximumConcurrentCalls);
        Assert.Equal(2, processor.CallsFor(first.Id));
        Assert.Equal(1, processor.CallsFor(second.Id));
        Assert.Equal(UploadStatus.Completed, (await fixture.UploadStore.FindByIdAsync(first.Id))!.Status);
        Assert.Equal(UploadStatus.Completed, (await fixture.UploadStore.FindByIdAsync(second.Id))!.Status);
    }

    [Fact]
    public async Task ProcessorFailureRejectsOnlyThatItemAndLaterItemsStillComplete()
    {
        using var fixture = new WorkflowFixture();
        var upload = await fixture.AddUploadAsync(UploadStatus.Queued, receivedCount: 3);
        var processor = new DeterministicProcessor((_, index, _) => index switch
        {
            0 => Task.FromResult(new UploadItemProcessingResult(UploadItemStatus.Approved, "Approved before failure.")),
            1 => FailAfterVerifyingPriorOutcomeAsync(fixture.ItemStore, upload.Id),
            _ => Task.FromResult(new UploadItemProcessingResult(UploadItemStatus.Cleaned, "Completed after failure."))
        });

        Assert.True(await fixture.CreateWorkflow(processor).ProcessNextAsync());

        var persistedItems = await fixture.ItemStore.GetByUploadIdAsync(upload.Id);
        Assert.Equal(new[] { UploadItemStatus.Approved, UploadItemStatus.Rejected, UploadItemStatus.Cleaned }, persistedItems.Select(item => item.Status));
        Assert.Equal("Item rejected during processing.", persistedItems[1].ActionTaken);
        Assert.DoesNotContain("private", persistedItems[1].ActionTaken, StringComparison.OrdinalIgnoreCase);
        var completed = await fixture.UploadStore.FindByIdAsync(upload.Id);
        Assert.NotNull(completed);
        Assert.Equal(UploadStatus.CompletedWithRejections, completed.Status);
        Assert.Equal((1, 1, 1), (completed.ApprovedCount, completed.CleanedCount, completed.RejectedCount));
    }

    [Fact]
    public async Task UnexpectedWorkflowFailurePersistsFailedStateWithoutSensitiveDetails()
    {
        using var fixture = new WorkflowFixture();
        var upload = await fixture.AddUploadAsync(UploadStatus.Queued, receivedCount: 2);
        var processor = new DeterministicProcessor((_, _, _) =>
            Task.FromException<UploadItemProcessingResult>(new InvalidOperationException("private infrastructure details")));

        Assert.True(await fixture.CreateWorkflow(processor).ProcessNextAsync());

        var failed = await fixture.UploadStore.FindByIdAsync(upload.Id);
        Assert.NotNull(failed);
        Assert.Equal(UploadStatus.Failed, failed.Status);
        Assert.Equal("workflow_failed", failed.FailureCode);
        Assert.Equal("Upload processing could not be completed.", failed.FailureMessage);
        Assert.NotNull(failed.CompletedAtUtc);
        Assert.Empty(await fixture.ItemStore.GetByUploadIdAsync(upload.Id));
        Assert.True(File.Exists(failed.ReportFilePath));
        using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(failed.ReportFilePath));
        Assert.Equal("Failed", report.RootElement.GetProperty("status").GetString());
        Assert.Equal("Upload processing could not be completed.", report.RootElement.GetProperty("failureMessage").GetString());
    }

    [Fact]
    public async Task RecoverySkipsPriorResultsAndCancellationRequeuesWithoutLosingProgress()
    {
        using var fixture = new WorkflowFixture();
        var upload = await fixture.AddUploadAsync(UploadStatus.Processing, receivedCount: 3);
        await fixture.ItemStore.SaveProgressAsync(CreateItem(upload.Id, 0, UploadItemStatus.Approved, "Persisted before restart."));
        await fixture.CreateRecoveryService().RecoverAsync();

        var waitingProcessor = new CancellationGateProcessor();
        using var cancellation = new CancellationTokenSource();
        var workflow = fixture.CreateWorkflow(waitingProcessor);
        var running = workflow.ProcessNextAsync(cancellation.Token);
        await waitingProcessor.SecondItemEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        var interrupted = await fixture.UploadStore.FindByIdAsync(upload.Id);
        Assert.NotNull(interrupted);
        Assert.Equal(UploadStatus.Queued, interrupted.Status);
        Assert.Null(interrupted.CompletedAtUtc);
        Assert.Equal(new[] { 0 }, (await fixture.ItemStore.GetByUploadIdAsync(upload.Id)).Select(item => item.SourceIndex));
        Assert.Equal(new[] { 1 }, waitingProcessor.ProcessedIndices);

        var resumedProcessor = new DeterministicProcessor((_, index, _) => Task.FromResult(new UploadItemProcessingResult(
            index == 2 ? UploadItemStatus.Rejected : UploadItemStatus.Cleaned,
            "Resumed.")));
        Assert.True(await fixture.CreateWorkflow(resumedProcessor).ProcessNextAsync());

        var final = await fixture.UploadStore.FindByIdAsync(upload.Id);
        Assert.NotNull(final);
        Assert.Equal(UploadStatus.CompletedWithRejections, final.Status);
        Assert.Equal((1, 1, 1), (final.ApprovedCount, final.CleanedCount, final.RejectedCount));
        Assert.Equal(new[] { 1, 2 }, resumedProcessor.ProcessedIndices);
    }

    private static UploadItemRecord CreateItem(Guid uploadId, int index, UploadItemStatus status, string action) => new(
        uploadId,
        index,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        status,
        action,
        null);

    private static async Task<UploadItemProcessingResult> FailAfterVerifyingPriorOutcomeAsync(IUploadItemStore itemStore, Guid uploadId)
    {
        var outcomes = await itemStore.GetByUploadIdAsync(uploadId);
        Assert.Single(outcomes);
        Assert.Equal(0, outcomes[0].SourceIndex);
        Assert.Equal(UploadItemStatus.Approved, outcomes[0].Status);
        throw new ItemProcessingException(new InvalidOperationException("private processor details"));
    }

    private sealed class WorkflowFixture : IDisposable
    {
        private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"marketplace-workflow-tests-{Guid.NewGuid():N}");
        private readonly SqliteConnectionFactory _connectionFactory;

        public WorkflowFixture()
        {
            Directory.CreateDirectory(_temporaryDirectory);
            var starterPath = Path.Combine(AppContext.BaseDirectory, "artifacts", "catalog.db");
            Paths = new FileSystemStoragePaths(new CatalogStorageOptions(Path.Combine(_temporaryDirectory, "data"), starterPath));
            var workingPath = new SqliteWorkingDatabaseBootstrapper(Paths).EnsureWorkingDatabaseAsync().GetAwaiter().GetResult();
            new SqliteDatabaseMigrator().MigrateAsync(workingPath).GetAwaiter().GetResult();
            _connectionFactory = new SqliteConnectionFactory(workingPath);
            UploadStore = new SqliteUploadStore(_connectionFactory);
            ItemStore = new SqliteUploadItemStore(_connectionFactory);
            WorkflowLock = new FileSystemWorkflowLock(Paths);
        }

        public FileSystemStoragePaths Paths { get; }
        public SqliteUploadStore UploadStore { get; }
        public SqliteUploadItemStore ItemStore { get; }
        public FileSystemWorkflowLock WorkflowLock { get; }

        public Task<UploadRecord> AddUploadAsync(UploadStatus status, int receivedCount, DateTimeOffset? startedAtUtc = null)
        {
            var uploadId = Guid.NewGuid();
            var timestamp = startedAtUtc ?? DateTimeOffset.UtcNow;
            var upload = new UploadRecord(
                uploadId,
                Guid.NewGuid(),
                $"{uploadId:N}.json",
                "test-hash",
                Path.Combine(Paths.UploadDirectory, $"{uploadId:D}.json"),
                Path.Combine(Paths.ReportDirectory, $"{uploadId:D}.json"),
                status,
                timestamp,
                null,
                receivedCount,
                0,
                0,
                0,
                Guid.NewGuid(),
                null,
                null,
                null);
            return UploadStore.CreateAsync(upload);
        }

        public ConsolidationWorkflow CreateWorkflow(IUploadItemProcessor processor, IWorkflowLock? workflowLock = null) =>
            new(UploadStore, ItemStore, processor, workflowLock ?? WorkflowLock, new UploadReportFinalizationService(UploadStore, ItemStore, new FileSystemReportFileStore(Paths)));

        public StartupRecoveryService CreateRecoveryService()
        {
            var reportStore = new FileSystemReportFileStore(Paths);
            return new StartupRecoveryService(UploadStore, WorkflowLock, reportStore, new UploadReportFinalizationService(UploadStore, ItemStore, reportStore));
        }

        public void Dispose()
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
    }

    private sealed class DeterministicProcessor(Func<UploadRecord, int, CancellationToken, Task<UploadItemProcessingResult>> callback) : IUploadItemProcessor
    {
        private readonly ConcurrentQueue<int> _processed = new();

        public bool IsAvailable => true;
        public IReadOnlyList<int> ProcessedIndices => _processed.Order().ToArray();

        public Task<UploadItemProcessingResult> ProcessAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken = default)
        {
            _processed.Enqueue(sourceIndex);
            return callback(upload, sourceIndex, cancellationToken);
        }
    }

    private sealed class UnavailableProcessor : IUploadItemProcessor
    {
        public bool IsAvailable => false;

        public Task<UploadItemProcessingResult> ProcessAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This processor must not be called.");
    }

    private sealed class GatedProcessor(Guid firstUploadId) : IUploadItemProcessor
    {
        private readonly ConcurrentDictionary<Guid, int> _callsByUpload = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls;
        private int _maximumConcurrentCalls;

        public TaskCompletionSource FirstItemEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsAvailable => true;
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximumConcurrentCalls);
        public int CallsFor(Guid uploadId) => _callsByUpload.GetValueOrDefault(uploadId);
        public void ReleaseFirstItem() => _release.TrySetResult();

        public async Task<UploadItemProcessingResult> ProcessAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken = default)
        {
            _callsByUpload.AddOrUpdate(upload.Id, 1, static (_, count) => count + 1);
            var active = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(active);
            try
            {
                if (upload.Id == firstUploadId && sourceIndex == 0)
                {
                    FirstItemEntered.TrySetResult();
                    await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return new UploadItemProcessingResult(UploadItemStatus.Approved, "Approved.");
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentCalls);
                if (value <= current || Interlocked.CompareExchange(ref _maximumConcurrentCalls, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class CancellationGateProcessor : IUploadItemProcessor
    {
        private readonly ConcurrentQueue<int> _processed = new();

        public TaskCompletionSource SecondItemEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsAvailable => true;
        public IReadOnlyList<int> ProcessedIndices => _processed.Order().ToArray();

        public async Task<UploadItemProcessingResult> ProcessAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken = default)
        {
            _processed.Enqueue(sourceIndex);
            if (sourceIndex == 1)
            {
                SecondItemEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return new UploadItemProcessingResult(UploadItemStatus.Approved, "Approved.");
        }
    }

    private sealed class SignalingWorkflowLock(IWorkflowLock inner) : IWorkflowLock
    {
        private int _attempts;

        public TaskCompletionSource SecondAcquireAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempts) == 2)
            {
                SecondAcquireAttempted.TrySetResult();
            }

            return inner.AcquireAsync(cancellationToken);
        }
    }
}
