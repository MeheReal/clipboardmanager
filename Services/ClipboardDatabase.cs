using ClipboardManager.Models;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Services;

public sealed class ClipboardDatabase : IDisposable
{
    public const int HistoryLimit = 1_000;
    public const int MaxTextLength = 1_000_000;

    private readonly string _directoryPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public ClipboardDatabase()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipboardManager");
        _directoryPath = directory;
        Directory.CreateDirectory(_directoryPath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directoryPath, "clipboard-history.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 3
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directoryPath);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS ClipboardItems (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    TextContent     TEXT NOT NULL,
                    Timestamp       INTEGER NOT NULL,
                    ItemType        TEXT NOT NULL CHECK (ItemType IN ('Text', 'Link', 'Code'))
                );
                CREATE INDEX IF NOT EXISTS IX_ClipboardItems_Timestamp
                    ON ClipboardItems (Timestamp DESC, Id DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ClipboardItem>> GetRecentAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<ClipboardItem>(HistoryLimit);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TextContent, Timestamp, ItemType
            FROM ClipboardItems
            ORDER BY Timestamp DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", HistoryLimit);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Enum.TryParse<ClipboardItemType>(reader.GetString(3), ignoreCase: false, out var itemType))
                itemType = ClipboardItemType.Text;

            items.Add(new ClipboardItem(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                itemType));
        }

        return items;
    }

    public async Task<ClipboardItem?> AddAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            return null;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            using (var latest = connection.CreateCommand())
            {
                latest.Transaction = (SqliteTransaction)transaction;
                latest.CommandText = "SELECT TextContent FROM ClipboardItems ORDER BY Id DESC LIMIT 1;";
                var previous = await latest.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                if (string.Equals(previous, text, StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }
            }

            var timestamp = DateTimeOffset.UtcNow;
            var itemType = ClipboardClassifier.Classify(text);
            long id;
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO ClipboardItems (TextContent, Timestamp, ItemType)
                    VALUES ($text, $timestamp, $type);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$text", text);
                insert.Parameters.AddWithValue("$timestamp", timestamp.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$type", itemType.ToString());
                id = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("SQLite did not return the inserted clipboard item id."));
            }

            using (var trim = connection.CreateCommand())
            {
                trim.Transaction = (SqliteTransaction)transaction;
                trim.CommandText = """
                    DELETE FROM ClipboardItems
                    WHERE Id NOT IN (
                        SELECT Id FROM ClipboardItems ORDER BY Id DESC LIMIT $limit
                    );
                    """;
                trim.Parameters.AddWithValue("$limit", HistoryLimit);
                await trim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ClipboardItem(id, text, timestamp, itemType);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ClipboardItems WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Avoid disposing the semaphore while an in-flight clipboard save may release it.
    }
}

