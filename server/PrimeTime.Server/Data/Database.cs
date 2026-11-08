using Microsoft.Data.Sqlite;

namespace PrimeTime.Server.Data;

/// <summary>
/// Opens connections to the SQLite database and applies schema migrations.
/// Migrations are numbered SQL scripts tracked with PRAGMA user_version.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;
    private readonly ILogger<Database> _logger;

    private static readonly string[] Migrations =
    [
        // 1: admin accounts, key/value settings, the Jellyfin connection.
        """
        CREATE TABLE admin_users (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
            password_hash TEXT NOT NULL,
            created_at    TEXT NOT NULL
        );

        CREATE TABLE settings (
            key        TEXT PRIMARY KEY,
            value      TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE jellyfin_connection (
            id              INTEGER PRIMARY KEY CHECK (id = 1),
            base_url        TEXT NOT NULL,
            auth_method     TEXT NOT NULL,
            username        TEXT,
            user_id         TEXT,
            token_protected TEXT NOT NULL,
            server_id       TEXT,
            server_name     TEXT,
            server_version  TEXT,
            updated_at      TEXT NOT NULL
        );
        """,

        // 2: selected libraries, the media index, and scheduled tasks.
        """
        CREATE TABLE libraries (
            id              TEXT PRIMARY KEY,
            name            TEXT NOT NULL,
            collection_type TEXT NOT NULL,
            locations       TEXT NOT NULL DEFAULT '[]',
            selected        INTEGER NOT NULL DEFAULT 0,
            last_scanned_at TEXT,
            last_scan_error TEXT
        );

        CREATE TABLE movies (
            id                TEXT PRIMARY KEY,
            library_id        TEXT NOT NULL REFERENCES libraries (id) ON DELETE CASCADE,
            title             TEXT NOT NULL,
            sort_title        TEXT,
            year              INTEGER,
            overview          TEXT,
            runtime_seconds   INTEGER,
            genres            TEXT NOT NULL DEFAULT '[]',
            studios           TEXT NOT NULL DEFAULT '[]',
            tags              TEXT NOT NULL DEFAULT '[]',
            official_rating   TEXT,
            community_rating  REAL,
            premiere_date     TEXT,
            date_added        TEXT,
            provider_ids      TEXT NOT NULL DEFAULT '{}',
            first_seen_run_id INTEGER NOT NULL,
            last_seen_run_id  INTEGER NOT NULL
        );
        CREATE INDEX ix_movies_library ON movies (library_id);

        CREATE TABLE shows (
            id                TEXT PRIMARY KEY,
            library_id        TEXT NOT NULL REFERENCES libraries (id) ON DELETE CASCADE,
            title             TEXT NOT NULL,
            sort_title        TEXT,
            year              INTEGER,
            overview          TEXT,
            status            TEXT,
            genres            TEXT NOT NULL DEFAULT '[]',
            studios           TEXT NOT NULL DEFAULT '[]',
            tags              TEXT NOT NULL DEFAULT '[]',
            official_rating   TEXT,
            community_rating  REAL,
            premiere_date     TEXT,
            date_added        TEXT,
            provider_ids      TEXT NOT NULL DEFAULT '{}',
            first_seen_run_id INTEGER NOT NULL,
            last_seen_run_id  INTEGER NOT NULL
        );
        CREATE INDEX ix_shows_library ON shows (library_id);

        CREATE TABLE episodes (
            id                TEXT PRIMARY KEY,
            show_id           TEXT NOT NULL REFERENCES shows (id) ON DELETE CASCADE,
            library_id        TEXT NOT NULL REFERENCES libraries (id) ON DELETE CASCADE,
            season_number     INTEGER,
            episode_number    INTEGER,
            title             TEXT NOT NULL,
            overview          TEXT,
            runtime_seconds   INTEGER,
            premiere_date     TEXT,
            date_added        TEXT,
            provider_ids      TEXT NOT NULL DEFAULT '{}',
            first_seen_run_id INTEGER NOT NULL,
            last_seen_run_id  INTEGER NOT NULL
        );
        CREATE INDEX ix_episodes_library ON episodes (library_id);
        CREATE INDEX ix_episodes_order ON episodes (show_id, season_number, episode_number);

        CREATE TABLE scheduled_tasks (
            key              TEXT PRIMARY KEY,
            enabled          INTEGER NOT NULL DEFAULT 1,
            interval_minutes INTEGER NOT NULL,
            next_run_at      TEXT
        );

        CREATE TABLE task_runs (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            task_key    TEXT NOT NULL,
            trigger     TEXT NOT NULL,
            status      TEXT NOT NULL,
            started_at  TEXT NOT NULL,
            finished_at TEXT,
            summary     TEXT,
            error       TEXT
        );
        CREATE INDEX ix_task_runs_key ON task_runs (task_key, id);
        """,

        // 3: installs that already finished a scan have completed first-run setup.
        """
        INSERT OR IGNORE INTO settings (key, value, updated_at)
        SELECT 'setup_complete', 'true', strftime('%Y-%m-%dT%H:%M:%SZ', 'now')
        WHERE EXISTS (SELECT 1 FROM libraries WHERE selected = 1 AND last_scanned_at IS NOT NULL);
        """,

        // 4: channels and their stored schedules.
        """
        CREATE TABLE channels (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            number       INTEGER NOT NULL UNIQUE,
            name         TEXT NOT NULL,
            content_type TEXT NOT NULL,
            mode         TEXT NOT NULL,
            year_from    INTEGER,
            year_before  INTEGER,
            genres       TEXT NOT NULL DEFAULT '[]',
            studios      TEXT NOT NULL DEFAULT '[]',
            created_at   TEXT NOT NULL,
            updated_at   TEXT NOT NULL
        );

        CREATE TABLE channel_items (
            channel_id INTEGER NOT NULL REFERENCES channels (id) ON DELETE CASCADE,
            item_id    TEXT NOT NULL,
            PRIMARY KEY (channel_id, item_id)
        );

        CREATE TABLE schedule_entries (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id     INTEGER NOT NULL REFERENCES channels (id) ON DELETE CASCADE,
            item_type      TEXT NOT NULL,
            item_id        TEXT NOT NULL,
            show_id        TEXT,
            season_number  INTEGER,
            episode_number INTEGER,
            title          TEXT NOT NULL,
            subtitle       TEXT,
            cycle          INTEGER NOT NULL DEFAULT 1,
            start_ts       INTEGER NOT NULL,
            end_ts         INTEGER NOT NULL,
            UNIQUE (channel_id, start_ts)
        );
        CREATE INDEX ix_schedule_window ON schedule_entries (start_ts, end_ts);
        """,

        // 5: Jellyfin collections.
        """
        CREATE TABLE collections (
            id        TEXT PRIMARY KEY,
            name      TEXT NOT NULL,
            sort_name TEXT
        );

        CREATE TABLE collection_items (
            collection_id TEXT NOT NULL REFERENCES collections (id) ON DELETE CASCADE,
            item_id       TEXT NOT NULL,
            PRIMARY KEY (collection_id, item_id)
        );
        """,

        // 6: The order a channel airs its content in. Keeps the closest thing to how each type played before.
        """
        ALTER TABLE channels ADD COLUMN air_order TEXT NOT NULL DEFAULT 'random';
        UPDATE channels SET air_order = 'oldest' WHERE content_type = 'shows';
        """,

        // 7: Channel logos.
        """
        CREATE TABLE channel_logos (
            channel_id   INTEGER PRIMARY KEY REFERENCES channels (id) ON DELETE CASCADE,
            content_type TEXT NOT NULL,
            data         BLOB NOT NULL,
            updated_at   INTEGER NOT NULL
        );
        """,

        // 8: Guide colors. A channel without a custom color gets one chosen from its content.
        """
        ALTER TABLE channels ADD COLUMN color TEXT;
        ALTER TABLE channels ADD COLUMN color_custom INTEGER NOT NULL DEFAULT 0;
        """,

        // 9: Server-side sessions, so signing out really ends one.
        """
        CREATE TABLE sessions (
            id           TEXT PRIMARY KEY,
            admin_id     INTEGER NOT NULL REFERENCES admin_users (id) ON DELETE CASCADE,
            last_seen_ts INTEGER NOT NULL
        );
        """,
    ];

    public Database(PrimeTimeOptions options, ILogger<Database> logger)
    {
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Migrate()
    {
        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");

        var current = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        for (var version = current; version < Migrations.Length; version++)
        {
            using var tx = connection.BeginTransaction();
            Execute(connection, Migrations[version], tx);
            Execute(connection, $"PRAGMA user_version = {version + 1};", tx);
            tx.Commit();
            _logger.LogInformation("Applied database migration {Version}", version + 1);
        }
    }

    public bool IsHealthy()
    {
        try
        {
            using var connection = Open();
            return Convert.ToInt32(Scalar(connection, "SELECT 1;")) == 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database health check failed");
            return false;
        }
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
