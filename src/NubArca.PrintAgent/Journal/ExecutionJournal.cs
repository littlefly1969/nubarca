using Microsoft.Data.Sqlite;

namespace NubArca.PrintAgent.Journal;

public static class LocalExecutionStates
{
    public const string Claimed = "claimed";
    public const string Submitting = "submitting";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string DeliveryUnknown = "delivery-unknown";
    public const string Acknowledged = "acknowledged";
}

public sealed record JournalEntry(Guid JobId, string ClaimToken, string ArtifactPath,
    string DeviceKey, string ContentType, string Format, string State,
    string? FailureCode, string? SpoolReference);

public sealed class ExecutionJournal
{
    private readonly string _connectionString;
    public ExecutionJournal(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS executions (
              job_id TEXT PRIMARY KEY,
              claim_token TEXT NOT NULL,
              artifact_path TEXT NOT NULL,
              device_key TEXT NOT NULL,
              content_type TEXT NOT NULL,
              format TEXT NOT NULL,
              state TEXT NOT NULL,
              failure_code TEXT NULL,
              spool_reference TEXT NULL,
              updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task UpsertClaimedAsync(JournalEntry entry, CancellationToken cancellationToken) =>
        UpsertAsync(entry with { State = LocalExecutionStates.Claimed }, cancellationToken);
    public Task MarkSubmittingAsync(JournalEntry entry, CancellationToken cancellationToken) =>
        UpsertAsync(entry with { State = LocalExecutionStates.Submitting }, cancellationToken);
    public Task MarkResultAsync(JournalEntry entry, string state, string? failureCode,
        string? spoolReference, CancellationToken cancellationToken) =>
        UpsertAsync(entry with { State = state, FailureCode = failureCode, SpoolReference = spoolReference }, cancellationToken);

    public async Task MarkAcknowledgedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE executions SET state = $state, updated_at = $now WHERE job_id = $id";
        command.Parameters.AddWithValue("$state", LocalExecutionStates.Acknowledged);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JournalEntry>> LoadPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT job_id, claim_token, artifact_path, device_key, content_type, format, state, failure_code, spool_reference FROM executions WHERE state <> $acked ORDER BY updated_at";
        command.Parameters.AddWithValue("$acked", LocalExecutionStates.Acknowledged);
        var rows = new List<JournalEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        return rows;
    }

    private async Task UpsertAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO executions(job_id, claim_token, artifact_path, device_key, content_type, format, state, failure_code, spool_reference, updated_at)
            VALUES($id,$claim,$path,$device,$content,$format,$state,$failure,$spool,$now)
            ON CONFLICT(job_id) DO UPDATE SET claim_token=$claim, artifact_path=$path,
              device_key=$device, content_type=$content, format=$format, state=$state,
              failure_code=$failure, spool_reference=$spool, updated_at=$now;
            """;
        command.Parameters.AddWithValue("$id", entry.JobId.ToString("D"));
        command.Parameters.AddWithValue("$claim", entry.ClaimToken);
        command.Parameters.AddWithValue("$path", entry.ArtifactPath);
        command.Parameters.AddWithValue("$device", entry.DeviceKey);
        command.Parameters.AddWithValue("$content", entry.ContentType);
        command.Parameters.AddWithValue("$format", entry.Format);
        command.Parameters.AddWithValue("$state", entry.State);
        command.Parameters.AddWithValue("$failure", (object?)entry.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$spool", (object?)entry.SpoolReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
