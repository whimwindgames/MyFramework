using Microsoft.Data.Sqlite;
using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Core;

public sealed record BuildHistoryItem(string JobId, string ProjectId, string ProfileId,
    string Action, string Target, string Environment, string Status, bool Ok, long DurationMs,
    string StartedAtUtc, string OutputRoot, string ReceiptJson);

public sealed class BuildHistoryRepository
{
    readonly string _databasePath;

    public BuildHistoryRepository(string? databasePath = null)
    {
        _databasePath = databasePath ?? BuildStudioPaths.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!);
        initialize();
    }

    public void Add(MfBuildReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using SqliteConnection connection = open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO builds
            (job_id, project_id, profile_id, action, target, environment, status, ok,
             duration_ms, started_at_utc, output_root, receipt_json)
            VALUES ($job, $project, $profile, $action, $target, $env, $status, $ok,
                    $duration, $started, $output, $json)
            """;
        command.Parameters.AddWithValue("$job", receipt.jobId ?? string.Empty);
        command.Parameters.AddWithValue("$project", receipt.projectId ?? string.Empty);
        command.Parameters.AddWithValue("$profile", receipt.profileId ?? string.Empty);
        command.Parameters.AddWithValue("$action", receipt.action ?? string.Empty);
        command.Parameters.AddWithValue("$target", receipt.target ?? string.Empty);
        command.Parameters.AddWithValue("$env", receipt.environment ?? string.Empty);
        command.Parameters.AddWithValue("$status", receipt.status ?? string.Empty);
        command.Parameters.AddWithValue("$ok", receipt.ok ? 1 : 0);
        command.Parameters.AddWithValue("$duration", receipt.durationMs);
        command.Parameters.AddWithValue("$started", receipt.startedAtUtc ?? string.Empty);
        command.Parameters.AddWithValue("$output", receipt.outputRoot ?? string.Empty);
        command.Parameters.AddWithValue("$json", BuildStudioJson.Serialize(receipt, false));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BuildHistoryItem> List(int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using SqliteConnection connection = open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, project_id, profile_id, action, target, environment, status, ok,
                   duration_ms, started_at_utc, output_root, receipt_json
            FROM builds ORDER BY started_at_utc DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        List<BuildHistoryItem> result = [];
        while (reader.Read()) result.Add(new BuildHistoryItem(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7) != 0,
            reader.GetInt64(8), reader.GetString(9), reader.GetString(10), reader.GetString(11)));
        return result;
    }

    void initialize()
    {
        using SqliteConnection connection = open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS builds (
              job_id TEXT PRIMARY KEY,
              project_id TEXT NOT NULL,
              profile_id TEXT NOT NULL,
              action TEXT NOT NULL,
              target TEXT NOT NULL,
              environment TEXT NOT NULL,
              status TEXT NOT NULL,
              ok INTEGER NOT NULL,
              duration_ms INTEGER NOT NULL,
              started_at_utc TEXT NOT NULL,
              output_root TEXT NOT NULL,
              receipt_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS builds_started ON builds(started_at_utc DESC);
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection open()
    {
        SqliteConnection connection = new($"Data Source={_databasePath};Mode=ReadWriteCreate");
        connection.Open();
        return connection;
    }
}
