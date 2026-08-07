using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Data;

/// <summary>
/// Owns the sidecar's SQLite file: connection settings, schema creation and migration.
/// </summary>
/// <remarks>
/// <para>
/// The op log leans on one SQLite guarantee, so it is worth stating plainly: SQLite
/// permits only a single write transaction at a time, so commits are totally ordered.
/// Because <c>seq</c> is assigned by AUTOINCREMENT inside the writing transaction, a
/// reader that observes sequence N is guaranteed to already see every sequence below it.
/// That is what makes a monotonic cursor safe — a puller can never skip an op that was
/// still in flight. A store that allowed concurrent writers would need an explicit
/// commit-order sequence instead.
/// </para>
/// <para>
/// Rolled-back transactions burn a sequence number, so the log may contain gaps.
/// That is harmless: the cursor means "everything up to here", not "the next number".
/// </para>
/// </remarks>
public sealed class SyncDatabase
{
    private const int CurrentSchemaVersion = 2;

    private static readonly string[] Migrations =
    {
        // v1 — the op log.
        """
        CREATE TABLE IF NOT EXISTS ops (
            seq         INTEGER PRIMARY KEY AUTOINCREMENT,
            op_id       TEXT NOT NULL,
            user_id     TEXT NOT NULL,
            device_id   TEXT NOT NULL,
            entity      TEXT NOT NULL,
            entity_id   TEXT NOT NULL,
            operation   TEXT NOT NULL,
            payload     TEXT NOT NULL,
            created_at  INTEGER NOT NULL,
            received_at INTEGER NOT NULL
        );

        -- Scoped by user, not global: op ids are client-generated, and a shared
        -- namespace would let one account squat another's id and silently void its op.
        CREATE UNIQUE INDEX IF NOT EXISTS idx_ops_user_opid ON ops (user_id, op_id);

        -- The pull query, exactly: everything for one user past a cursor, in seq order.
        CREATE INDEX IF NOT EXISTS idx_ops_user_seq ON ops (user_id, seq);
        """,

        // v2 — playlist export bookkeeping, and artwork.
        """
        CREATE TABLE IF NOT EXISTS exported_playlists (
            user_id           TEXT NOT NULL,
            aoide_playlist_id TEXT NOT NULL,
            jellyfin_item_id  TEXT NOT NULL,
            content_hash      TEXT NOT NULL,
            exported_at       INTEGER NOT NULL,
            PRIMARY KEY (user_id, aoide_playlist_id)
        );

        -- Artwork is keyed by the SHA-256 of its own bytes, never by playlist id.
        -- Content addressing makes an upload idempotent, lets identical artwork on two
        -- playlists share one row, and means a client can cache a hash forever because
        -- the bytes behind it can never change. Keeping the bytes out of the op log is
        -- what stops a full history sync from carrying every image ever set.
        CREATE TABLE IF NOT EXISTS playlist_images (
            user_id    TEXT NOT NULL,
            image_hash TEXT NOT NULL,
            mime_type  TEXT NOT NULL,
            bytes      BLOB NOT NULL,
            size       INTEGER NOT NULL,
            created_at INTEGER NOT NULL,
            PRIMARY KEY (user_id, image_hash)
        );
        """
    };

    private readonly string _connectionString;
    private readonly ILogger<SyncDatabase> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncDatabase"/> class.
    /// </summary>
    /// <param name="databasePath">Full path to the SQLite file; its directory is created if absent.</param>
    /// <param name="logger">Logger.</param>
    public SyncDatabase(string databasePath, ILogger<SyncDatabase> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        DatabasePath = databasePath;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    /// <summary>
    /// Gets the full path to the SQLite file.
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Opens a connection, creating and migrating the schema on first use.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection the caller owns and must dispose.</returns>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        return await OpenRawAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // busy_timeout lets a reader wait out a concurrent writer instead of failing.
        // synchronous=FULL costs an fsync per commit, which is the price of push
        // meaning "durable": a client marks its ops synced on the strength of that
        // response and will not send them again, so a lost commit is lost user data.
        // Pushes are batched into one transaction each, so the cost is per batch.
        await ExecuteAsync(connection, "PRAGMA busy_timeout=10000; PRAGMA synchronous=FULL;", cancellationToken)
            .ConfigureAwait(false);

        return connection;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);

            // WAL is a property of the file and survives reconnection, but it cannot be
            // set inside a transaction — hence before the migration block below.
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);

            var version = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The Aoide sync database at {DatabasePath} is at schema version {version}, "
                    + $"newer than this plugin understands ({CurrentSchemaVersion}). Update the plugin.");
            }

            for (var next = version; next < Migrations.Length; next++)
            {
                _logger.LogInformation("Applying Aoide sync schema migration {Version}", next + 1);

                await using var transaction = connection.BeginTransaction(deferred: false);
                await ExecuteAsync(connection, Migrations[next], cancellationToken, transaction).ConfigureAwait(false);
                await ExecuteAsync(connection, $"PRAGMA user_version={next + 1};", cancellationToken, transaction)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            _logger.LogInformation("Aoide sync database ready at {Path}", DatabasePath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
