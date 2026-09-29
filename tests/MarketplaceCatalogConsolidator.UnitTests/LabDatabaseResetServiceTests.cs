using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Application.Workflow;

namespace MarketplaceCatalogConsolidator.UnitTests;

public sealed class LabDatabaseResetServiceTests
{
    [Fact]
    public async Task HoldsMaintenanceGateAndFinishesMutationDespiteClientCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new Gate();
        var storage = new Storage(async token =>
        {
            Assert.True(gate.Held);
            cancellation.Cancel();
            Assert.False(token.CanBeCanceled);
            await Task.Yield();
            return new LabDatabaseResetResult(DateTimeOffset.UtcNow, 975, 0);
        });

        var result = await new LabDatabaseResetService(gate, storage).ResetAsync(cancellation.Token);

        Assert.Equal(975, result.ProductCount);
        Assert.False(gate.Held);
    }

    [Fact]
    public async Task FailureReleasesGateAndHidesInfrastructureException()
    {
        var gate = new Gate();
        var storage = new Storage(_ => throw new IOException("sensitive filesystem path"));

        var error = await Assert.ThrowsAsync<LabDatabaseResetException>(() => new LabDatabaseResetService(gate, storage).ResetAsync());

        Assert.Null(error.InnerException);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.False(gate.Held);
    }

    [Fact]
    public async Task CancellationBeforeGateAcquisitionNeverMutatesStorage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;
        var storage = new Storage(_ => { called = true; throw new InvalidOperationException(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LabDatabaseResetService(new Gate(), storage).ResetAsync(cancellation.Token));

        Assert.False(called);
    }

    private sealed class Gate : IWorkflowLock
    {
        public bool Held { get; private set; }
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Maintenance must use the maintenance entry point.");
        public Task<IAsyncDisposable> AcquireMaintenanceAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Held = true;
            return Task.FromResult<IAsyncDisposable>(new Lease(this));
        }
        private sealed class Lease(Gate gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { gate.Held = false; return ValueTask.CompletedTask; }
        }
    }
    private sealed class Storage(Func<CancellationToken, Task<LabDatabaseResetResult>> reset) : ILabDatabaseResetStorage
    {
        public bool HasPendingReset => false;
        public Task<LabDatabaseResetResult> ResetAsync(CancellationToken cancellationToken = default) => reset(cancellationToken);
    }
}
