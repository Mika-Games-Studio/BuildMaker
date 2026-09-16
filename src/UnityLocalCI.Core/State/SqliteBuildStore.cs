using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace UnityLocalCI.Core.State;

/// <summary>Estado em SQLite. Ver secao 4 da especificacao.</summary>
public sealed class SqliteBuildStore : IBuildStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteBuildStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteBuildStore(string databasePath, ILogger<SqliteBuildStore> logger)
    {
        _logger = logger;

        var folder = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(connection, """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS builds (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                commit_sha           TEXT NOT NULL,
                commit_message       TEXT,
                commit_author        TEXT,
                project              TEXT NOT NULL,
                branch               TEXT NOT NULL,
                status               TEXT NOT NULL,
                queued_at            TEXT NOT NULL,
                started_at           TEXT,
                finished_at          TEXT,
                duration_seconds     INTEGER,
                artifact_path        TEXT,
                artifact_size_bytes  INTEGER,
                artifact_sha256      TEXT,
                published_path       TEXT,
                publish_status       TEXT,
                log_path             TEXT,
                error_summary        TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_builds_project_status ON builds (project, status);
            CREATE INDEX IF NOT EXISTS ix_builds_project_queued ON builds (project, queued_at DESC);

            CREATE TABLE IF NOT EXISTS watcher_state (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            """, ct).ConfigureAwait(false);

        _logger.LogInformation("Estado inicializado.");
    }

    public async Task<long> CreateAsync(BuildRecord record, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO builds
                    (commit_sha, commit_message, commit_author, project, branch, status,
                     queued_at, started_at, finished_at, duration_seconds,
                     artifact_path, artifact_size_bytes, artifact_sha256,
                     published_path, publish_status, log_path, error_summary)
                VALUES
                    ($sha, $message, $author, $project, $branch, $status,
                     $queuedAt, $startedAt, $finishedAt, $duration,
                     $artifactPath, $artifactSize, $artifactSha,
                     $publishedPath, $publishStatus, $logPath, $errorSummary);
                SELECT last_insert_rowid();
                """;
            Bind(command, record);

            return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }
        finally { _writeLock.Release(); }
    }

    public async Task UpdateAsync(BuildRecord record, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE builds SET
                    commit_sha = $sha, commit_message = $message, commit_author = $author,
                    project = $project, branch = $branch, status = $status,
                    queued_at = $queuedAt, started_at = $startedAt, finished_at = $finishedAt,
                    duration_seconds = $duration, artifact_path = $artifactPath,
                    artifact_size_bytes = $artifactSize, artifact_sha256 = $artifactSha,
                    published_path = $publishedPath, publish_status = $publishStatus,
                    log_path = $logPath, error_summary = $errorSummary
                WHERE id = $id;
                """;
            Bind(command, record);
            command.Parameters.AddWithValue("$id", record.Id);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<BuildRecord?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM builds WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<BuildRecord>> GetByStatusAsync(BuildStatus status, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM builds WHERE status = $status ORDER BY id;";
        command.Parameters.AddWithValue("$status", status.ToString());
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BuildRecord>> GetRecentAsync(string project, int count, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM builds WHERE project = $project ORDER BY id DESC LIMIT $count;";
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$count", count);
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<BuildRecord?> GetLastFinishedAsync(string project, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM builds
            WHERE project = $project AND finished_at IS NOT NULL
            ORDER BY id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$project", project);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<string?> GetWatcherValueAsync(string project, string field, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM watcher_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", WatcherKey(project, field));
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is DBNull or null ? null : (string)value;
    }

    public async Task SetWatcherValueAsync(string project, string field, string? value, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO watcher_state (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", WatcherKey(project, field));
            command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>A chave e "{projeto}:{campo}", conforme a secao 4.</summary>
    private static string WatcherKey(string project, string field) => $"{project}:{field}";

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<BuildRecord>> ReadAllAsync(SqliteCommand command, CancellationToken ct)
    {
        var records = new List<BuildRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) records.Add(Map(reader));
        return records;
    }

    private static void Bind(SqliteCommand command, BuildRecord r)
    {
        command.Parameters.AddWithValue("$sha", r.CommitSha);
        command.Parameters.AddWithValue("$message", (object?)r.CommitMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$author", (object?)r.CommitAuthor ?? DBNull.Value);
        command.Parameters.AddWithValue("$project", r.Project);
        command.Parameters.AddWithValue("$branch", r.Branch);
        command.Parameters.AddWithValue("$status", r.Status.ToString());
        command.Parameters.AddWithValue("$queuedAt", Iso(r.QueuedAt));
        command.Parameters.AddWithValue("$startedAt", (object?)Iso(r.StartedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$finishedAt", (object?)Iso(r.FinishedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)r.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$artifactPath", (object?)r.ArtifactPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$artifactSize", (object?)r.ArtifactSizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$artifactSha", (object?)r.ArtifactSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$publishedPath", (object?)r.PublishedPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$publishStatus", r.PublishStatus.ToString());
        command.Parameters.AddWithValue("$logPath", (object?)r.LogPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorSummary", (object?)r.ErrorSummary ?? DBNull.Value);
    }

    private static string Iso(DateTimeOffset value) => value.ToString("O");
    private static string? Iso(DateTimeOffset? value) => value?.ToString("O");

    private static BuildRecord Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        CommitSha = reader.GetString(reader.GetOrdinal("commit_sha")),
        CommitMessage = GetNullableString(reader, "commit_message"),
        CommitAuthor = GetNullableString(reader, "commit_author"),
        Project = reader.GetString(reader.GetOrdinal("project")),
        Branch = reader.GetString(reader.GetOrdinal("branch")),
        Status = Enum.Parse<BuildStatus>(reader.GetString(reader.GetOrdinal("status"))),
        QueuedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("queued_at"))),
        StartedAt = ParseNullableDate(reader, "started_at"),
        FinishedAt = ParseNullableDate(reader, "finished_at"),
        DurationSeconds = GetNullableInt(reader, "duration_seconds"),
        ArtifactPath = GetNullableString(reader, "artifact_path"),
        ArtifactSizeBytes = GetNullableLong(reader, "artifact_size_bytes"),
        ArtifactSha256 = GetNullableString(reader, "artifact_sha256"),
        PublishedPath = GetNullableString(reader, "published_path"),
        PublishStatus = ParsePublishStatus(GetNullableString(reader, "publish_status")),
        LogPath = GetNullableString(reader, "log_path"),
        ErrorSummary = GetNullableString(reader, "error_summary"),
    };

    private static PublishStatus ParsePublishStatus(string? value)
        => Enum.TryParse<PublishStatus>(value, out var parsed) ? parsed : PublishStatus.None;

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? GetNullableInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? GetNullableLong(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset? ParseNullableDate(SqliteDataReader reader, string column)
    {
        var value = GetNullableString(reader, column);
        return value is null ? null : DateTimeOffset.Parse(value);
    }
}
