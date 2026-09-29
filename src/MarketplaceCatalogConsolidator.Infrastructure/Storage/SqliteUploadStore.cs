using System.Globalization;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Domain;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class SqliteUploadStore(SqliteConnectionFactory connectionFactory) : IUploadStore
{
    private const string SelectColumns = "Id, IdempotencyKey, FileName, FileHash, StagedFilePath, ReportFilePath, Status, StartedAtUtc, CompletedAtUtc, ReceivedCount, ApprovedCount, CleanedCount, RejectedCount, TraceId, FailureCode, FailureMessage, ReportSha256, IntendedTerminalStatus, ConsolidationFinishedAtUtc, ReportGeneratedAtUtc, ReportGenerationFailureCode, ReportGenerationFailureMessage";
    private readonly SqliteConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public Task<UploadRecord?> FindByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        FindOneAsync("IdempotencyKey", idempotencyKey.ToString("D"), cancellationToken);

    public Task<UploadRecord?> FindByIdAsync(Guid uploadId, CancellationToken cancellationToken = default) =>
        FindOneAsync("Id", uploadId.ToString("D"), cancellationToken);

    public async Task<UploadRecord> CreateAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Upload (Id, IdempotencyKey, FileName, FileHash, StagedFilePath, ReportFilePath, Status, StartedAtUtc, CompletedAtUtc, ReceivedCount, ApprovedCount, CleanedCount, RejectedCount, TraceId, FailureCode, FailureMessage, ReportSha256, IntendedTerminalStatus, ConsolidationFinishedAtUtc, ReportGeneratedAtUtc, ReportGenerationFailureCode, ReportGenerationFailureMessage) VALUES ($id, $idempotencyKey, $fileName, $fileHash, $stagedFilePath, $reportFilePath, $status, $startedAtUtc, $completedAtUtc, $receivedCount, $approvedCount, $cleanedCount, $rejectedCount, $traceId, $failureCode, $failureMessage, $reportSha256, $intendedTerminalStatus, $consolidationFinishedAtUtc, $reportGeneratedAtUtc, $reportGenerationFailureCode, $reportGenerationFailureMessage);";
        AddUploadParameters(command, upload);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return upload;
    }

    public async Task<UploadCreateResult> CreateOrGetByIdempotencyKeyAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindByIdempotencyKeyAsync(connection, transaction, upload.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new UploadCreateResult(existing, Created: false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Upload (Id, IdempotencyKey, FileName, FileHash, StagedFilePath, ReportFilePath, Status, StartedAtUtc, CompletedAtUtc, ReceivedCount, ApprovedCount, CleanedCount, RejectedCount, TraceId, FailureCode, FailureMessage, ReportSha256, IntendedTerminalStatus, ConsolidationFinishedAtUtc, ReportGeneratedAtUtc, ReportGenerationFailureCode, ReportGenerationFailureMessage) VALUES ($id, $idempotencyKey, $fileName, $fileHash, $stagedFilePath, $reportFilePath, $status, $startedAtUtc, $completedAtUtc, $receivedCount, $approvedCount, $cleanedCount, $rejectedCount, $traceId, $failureCode, $failureMessage, $reportSha256, $intendedTerminalStatus, $consolidationFinishedAtUtc, $reportGeneratedAtUtc, $reportGenerationFailureCode, $reportGenerationFailureMessage);";
        AddUploadParameters(command, upload);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UploadCreateResult(upload, Created: true);
    }

    public async Task<Page<UploadRecord>> ListAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        => await ListFilteredAsync(null, pageNumber, pageSize, cancellationToken).ConfigureAwait(false);

    public async Task<Page<UploadRecord>> ListFilteredAsync(UploadStatus? status, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        connection.CreateFunction<string, long>("upload_start_ticks", timestamp => DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcTicks);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = "SELECT COUNT(*) FROM Upload WHERE ($status IS NULL OR Status = $status);";
        countCommand.Parameters.AddWithValue("$status", (object?)status?.ToString() ?? DBNull.Value);
        var totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Upload WHERE ($status IS NULL OR Status = $status) ORDER BY upload_start_ticks(StartedAtUtc) DESC, Id DESC LIMIT $pageSize OFFSET $offset;";
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$status", (object?)status?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$pageSize", pageSize);
        command.Parameters.AddWithValue("$offset", checked((long)(pageNumber - 1) * pageSize));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<UploadRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadUpload(reader));
        }

        return new Page<UploadRecord>(records, pageNumber, pageSize, totalCount);
    }

    public async Task UpdateAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Upload SET Status = $status, ReportFilePath = $reportFilePath, CompletedAtUtc = $completedAtUtc, ReceivedCount = $receivedCount, ApprovedCount = $approvedCount, CleanedCount = $cleanedCount, RejectedCount = $rejectedCount, FailureCode = $failureCode, FailureMessage = $failureMessage, ReportSha256 = $reportSha256, IntendedTerminalStatus = $intendedTerminalStatus, ConsolidationFinishedAtUtc = $consolidationFinishedAtUtc, ReportGeneratedAtUtc = $reportGeneratedAtUtc, ReportGenerationFailureCode = $reportGenerationFailureCode, ReportGenerationFailureMessage = $reportGenerationFailureMessage WHERE Id = $id;";
        command.Parameters.AddWithValue("$status", upload.Status.ToString());
        command.Parameters.AddWithValue("$reportFilePath", upload.ReportFilePath);
        command.Parameters.AddWithValue("$completedAtUtc", ToDatabaseValue(upload.CompletedAtUtc));
        command.Parameters.AddWithValue("$receivedCount", upload.ReceivedCount);
        command.Parameters.AddWithValue("$approvedCount", upload.ApprovedCount);
        command.Parameters.AddWithValue("$cleanedCount", upload.CleanedCount);
        command.Parameters.AddWithValue("$rejectedCount", upload.RejectedCount);
        command.Parameters.AddWithValue("$failureCode", (object?)upload.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureMessage", (object?)upload.FailureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$reportSha256", (object?)upload.ReportSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$intendedTerminalStatus", ToDatabaseValue(upload.IntendedTerminalStatus));
        command.Parameters.AddWithValue("$consolidationFinishedAtUtc", ToDatabaseValue(upload.ConsolidationFinishedAtUtc));
        command.Parameters.AddWithValue("$reportGeneratedAtUtc", ToDatabaseValue(upload.ReportGeneratedAtUtc));
        command.Parameters.AddWithValue("$reportGenerationFailureCode", (object?)upload.ReportGenerationFailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$reportGenerationFailureMessage", (object?)upload.ReportGenerationFailureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", upload.Id.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Upload '{upload.Id:D}' does not exist.");
        }
    }

    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Upload SET Status = $queued WHERE Status = $processing;";
        command.Parameters.AddWithValue("$queued", UploadStatus.Queued.ToString());
        command.Parameters.AddWithValue("$processing", UploadStatus.Processing.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UploadRecord?> ClaimNextQueuedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE Upload SET Status = $processing WHERE Id = (SELECT Id FROM Upload WHERE Status = $queued ORDER BY StartedAtUtc ASC, Id ASC LIMIT 1) AND Status = $queued RETURNING {SelectColumns};";
        command.Parameters.AddWithValue("$processing", UploadStatus.Processing.ToString());
        command.Parameters.AddWithValue("$queued", UploadStatus.Queued.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUpload(reader) : null;
    }

    public async Task<IReadOnlyList<UploadRecord>> GetReportPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Upload WHERE Status = $status ORDER BY StartedAtUtc, Id;";
        command.Parameters.AddWithValue("$status", UploadStatus.ReportPending.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var uploads = new List<UploadRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uploads.Add(ReadUpload(reader));
        }

        return uploads;
    }

    public async Task<bool> BeginReportFinalizationAsync(
        Guid uploadId,
        UploadStatus intendedTerminalStatus,
        int approvedCount,
        int cleanedCount,
        int rejectedCount,
        DateTimeOffset consolidationFinishedAtUtc,
        string? failureCode,
        string? failureMessage,
        CancellationToken cancellationToken = default)
    {
        if (intendedTerminalStatus is not (UploadStatus.Completed or UploadStatus.CompletedWithRejections or UploadStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(intendedTerminalStatus), "A terminal intent is required.");
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Upload SET Status = $pending, IntendedTerminalStatus = $intendedStatus, ConsolidationFinishedAtUtc = $finishedAtUtc, CompletedAtUtc = NULL, ApprovedCount = $approvedCount, CleanedCount = $cleanedCount, RejectedCount = $rejectedCount, FailureCode = $failureCode, FailureMessage = $failureMessage, ReportGenerationFailureCode = NULL, ReportGenerationFailureMessage = NULL WHERE Id = $id AND Status = $processing;";
        command.Parameters.AddWithValue("$pending", UploadStatus.ReportPending.ToString());
        command.Parameters.AddWithValue("$intendedStatus", intendedTerminalStatus.ToString());
        command.Parameters.AddWithValue("$finishedAtUtc", consolidationFinishedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$approvedCount", approvedCount);
        command.Parameters.AddWithValue("$cleanedCount", cleanedCount);
        command.Parameters.AddWithValue("$rejectedCount", rejectedCount);
        command.Parameters.AddWithValue("$failureCode", (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureMessage", (object?)failureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", uploadId.ToString("D"));
        command.Parameters.AddWithValue("$processing", UploadStatus.Processing.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> CompleteReportFinalizationAsync(
        Guid uploadId,
        string reportFilePath,
        string reportSha256,
        DateTimeOffset reportGeneratedAtUtc,
        DateTimeOffset terminalAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Upload SET Status = IntendedTerminalStatus, ReportFilePath = $reportFilePath, ReportSha256 = $reportSha256, ReportGeneratedAtUtc = $reportGeneratedAtUtc, CompletedAtUtc = $terminalAtUtc, ReportGenerationFailureCode = NULL, ReportGenerationFailureMessage = NULL WHERE Id = $id AND Status = $pending AND IntendedTerminalStatus IN ($completed, $completedWithRejections, $failed);";
        command.Parameters.AddWithValue("$reportFilePath", reportFilePath);
        command.Parameters.AddWithValue("$reportSha256", reportSha256);
        command.Parameters.AddWithValue("$reportGeneratedAtUtc", reportGeneratedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$terminalAtUtc", terminalAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", uploadId.ToString("D"));
        command.Parameters.AddWithValue("$pending", UploadStatus.ReportPending.ToString());
        command.Parameters.AddWithValue("$completed", UploadStatus.Completed.ToString());
        command.Parameters.AddWithValue("$completedWithRejections", UploadStatus.CompletedWithRejections.ToString());
        command.Parameters.AddWithValue("$failed", UploadStatus.Failed.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> RecordReportGenerationFailureAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Upload SET ReportGenerationFailureCode = $code, ReportGenerationFailureMessage = $message WHERE Id = $id AND Status = $pending;";
        command.Parameters.AddWithValue("$code", "report_generation_failed");
        command.Parameters.AddWithValue("$message", "The terminal report could not be generated; finalization will retry.");
        command.Parameters.AddWithValue("$id", uploadId.ToString("D"));
        command.Parameters.AddWithValue("$pending", UploadStatus.ReportPending.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> RequeueProcessingAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Upload SET Status = $queued WHERE Id = $id AND Status = $processing;";
        command.Parameters.AddWithValue("$queued", UploadStatus.Queued.ToString());
        command.Parameters.AddWithValue("$id", uploadId.ToString("D"));
        command.Parameters.AddWithValue("$processing", UploadStatus.Processing.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task<UploadRecord?> FindOneAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Upload WHERE {columnName} = $value LIMIT 1;";
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUpload(reader) : null;
    }

    private async Task<UploadRecord?> FindByIdempotencyKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM Upload WHERE IdempotencyKey = $value LIMIT 1;";
        command.Parameters.AddWithValue("$value", idempotencyKey.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUpload(reader) : null;
    }

    private static UploadRecord ReadUpload(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        Enum.Parse<UploadStatus>(reader.GetString(6), ignoreCase: false),
        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetInt32(9),
        reader.GetInt32(10),
        reader.GetInt32(11),
        reader.GetInt32(12),
        Guid.Parse(reader.GetString(13)),
        ReadNullableText(reader, 14),
        ReadNullableText(reader, 15),
        ReadNullableText(reader, 16),
        reader.IsDBNull(17) ? null : Enum.Parse<UploadStatus>(reader.GetString(17), ignoreCase: false),
        reader.IsDBNull(18) ? null : DateTimeOffset.Parse(reader.GetString(18), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ReadNullableText(reader, 20),
        ReadNullableText(reader, 21));

    private static void AddUploadParameters(SqliteCommand command, UploadRecord upload)
    {
        command.Parameters.AddWithValue("$id", upload.Id.ToString("D"));
        command.Parameters.AddWithValue("$idempotencyKey", upload.IdempotencyKey.ToString("D"));
        command.Parameters.AddWithValue("$fileName", upload.FileName);
        command.Parameters.AddWithValue("$fileHash", upload.FileHash);
        command.Parameters.AddWithValue("$stagedFilePath", upload.StagedFilePath);
        command.Parameters.AddWithValue("$reportFilePath", upload.ReportFilePath);
        command.Parameters.AddWithValue("$status", upload.Status.ToString());
        command.Parameters.AddWithValue("$startedAtUtc", upload.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$completedAtUtc", ToDatabaseValue(upload.CompletedAtUtc));
        command.Parameters.AddWithValue("$receivedCount", upload.ReceivedCount);
        command.Parameters.AddWithValue("$approvedCount", upload.ApprovedCount);
        command.Parameters.AddWithValue("$cleanedCount", upload.CleanedCount);
        command.Parameters.AddWithValue("$rejectedCount", upload.RejectedCount);
        command.Parameters.AddWithValue("$traceId", upload.TraceId.ToString("D"));
        command.Parameters.AddWithValue("$failureCode", (object?)upload.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureMessage", (object?)upload.FailureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$reportSha256", (object?)upload.ReportSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$intendedTerminalStatus", ToDatabaseValue(upload.IntendedTerminalStatus));
        command.Parameters.AddWithValue("$consolidationFinishedAtUtc", ToDatabaseValue(upload.ConsolidationFinishedAtUtc));
        command.Parameters.AddWithValue("$reportGeneratedAtUtc", ToDatabaseValue(upload.ReportGeneratedAtUtc));
        command.Parameters.AddWithValue("$reportGenerationFailureCode", (object?)upload.ReportGenerationFailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$reportGenerationFailureMessage", (object?)upload.ReportGenerationFailureMessage ?? DBNull.Value);
    }

    private static object ToDatabaseValue(DateTimeOffset? value) => value is null
        ? DBNull.Value
        : value.Value.ToString("O", CultureInfo.InvariantCulture);

    private static object ToDatabaseValue(UploadStatus? value) => value is null
        ? DBNull.Value
        : value.Value.ToString();

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
