using BtmPizza.Notifications.Payload;
using Microsoft.Data.Sqlite;

namespace BtmPizza.Notifications.Data;

public sealed record DeviceRegistration(
    string DeviceToken, string Platform, string UserId, string Environment, string? DeviceId,
    string? BundleId = null, string? AppVersion = null);

public sealed record Device(string DeviceToken, string UserId, string Environment, string UpdatedAt);

public sealed record TargetDevice(string DeviceToken, string Environment);

public sealed record SendLogEntry(
    string NotificationId, string Type, int TargetCount, int SuccessCount, int FailureCount, int RemovedTokens, string Payload,
    string? Title = null, string? Body = null, string? ImageUrl = null, Target? Target = null);

/// <summary>A row of the send log for the admin app. Title, body, image and target are null for sends logged before
/// they were recorded.</summary>
public sealed record SendLogItem(
    string NotificationId, string Type, string? Title, string? Body, string? ImageUrl, bool? TargetAll,
    IReadOnlyList<string>? TargetUserIds, int TargetCount, int SuccessCount, int FailureCount, string SentAt);

/// <summary>SQLite storage for device tokens and the send log.</summary>
public sealed class DeviceStore
{
    private readonly string _connectionString;

    public DeviceStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, DefaultTimeout = 5 }.ToString();
        using var conn = Open();
        Execute(conn, """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS devices (
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              device_id    TEXT UNIQUE,
              device_token TEXT NOT NULL UNIQUE,
              platform     TEXT NOT NULL,
              user_id      TEXT NOT NULL,
              environment  TEXT NOT NULL,
              created_at   TEXT NOT NULL DEFAULT (datetime('now')),
              updated_at   TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_devices_user_id ON devices(user_id);

            CREATE TABLE IF NOT EXISTS notification_log (
              notification_id TEXT PRIMARY KEY,
              type            TEXT NOT NULL,
              target_count    INTEGER NOT NULL,
              success_count   INTEGER NOT NULL,
              failure_count   INTEGER NOT NULL,
              removed_tokens  INTEGER NOT NULL,
              payload         TEXT NOT NULL,
              sent_at         TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        // Added with the app's /api/devices/register call; older databases get the columns here.
        foreach (var column in new[] { "bundle_id", "app_version" })
            AddColumnIfMissing(conn, "devices", column, "TEXT");

        // Added for the admin app's send log: what was sent and to whom. Older rows keep NULLs.
        foreach (var (column, type) in new[] { ("title", "TEXT"), ("body", "TEXT"), ("image_url", "TEXT"), ("target_all", "INTEGER"), ("target_user_ids", "TEXT") })
            AddColumnIfMissing(conn, "notification_log", column, type);
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_notification_log_sent_at ON notification_log(sent_at)");
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string type)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name";
        check.Parameters.AddWithValue("$name", column);
        if ((long)check.ExecuteScalar()! == 0) Execute(conn, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static int Execute(SqliteConnection conn, string sql, params (string Name, object? Value)[] args) =>
        Execute(conn, null, sql, args);

    private static int Execute(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Stores one token per device. If the app sends a stable device_id (e.g. identifierForVendor),
    /// a new token for that device replaces the old one. Without it, the token itself identifies
    /// the device. Either way a token is only stored once, so a token that moves to another user
    /// is reassigned.
    /// </summary>
    public Device Upsert(DeviceRegistration d)
    {
        using var conn = Open();
        using (var tx = conn.BeginTransaction())
        {
            var args = new (string, object?)[]
            {
                ("$deviceId", d.DeviceId), ("$token", d.DeviceToken), ("$platform", d.Platform),
                ("$userId", d.UserId), ("$env", d.Environment),
                ("$bundleId", d.BundleId), ("$appVersion", d.AppVersion),
            };
            if (d.DeviceId is not null)
            {
                Execute(conn, tx, "DELETE FROM devices WHERE device_token = $token AND (device_id IS NULL OR device_id != $deviceId)", args);
                Execute(conn, tx, """
                    INSERT INTO devices (device_id, device_token, platform, user_id, environment, bundle_id, app_version)
                    VALUES ($deviceId, $token, $platform, $userId, $env, $bundleId, $appVersion)
                    ON CONFLICT(device_id) DO UPDATE SET
                      device_token = excluded.device_token,
                      platform     = excluded.platform,
                      user_id      = excluded.user_id,
                      environment  = excluded.environment,
                      bundle_id    = excluded.bundle_id,
                      app_version  = excluded.app_version,
                      updated_at   = datetime('now')
                    """, args);
            }
            else
            {
                Execute(conn, tx, """
                    INSERT INTO devices (device_token, platform, user_id, environment, bundle_id, app_version)
                    VALUES ($token, $platform, $userId, $env, $bundleId, $appVersion)
                    ON CONFLICT(device_token) DO UPDATE SET
                      platform    = excluded.platform,
                      user_id     = excluded.user_id,
                      environment = excluded.environment,
                      bundle_id   = excluded.bundle_id,
                      app_version = excluded.app_version,
                      updated_at  = datetime('now')
                    """, args);
            }
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_token, user_id, environment, updated_at FROM devices WHERE device_token = $token";
        cmd.Parameters.AddWithValue("$token", d.DeviceToken);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new Device(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    public List<TargetDevice> FindTargets(Target target)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (target.All)
        {
            cmd.CommandText = "SELECT device_token, environment FROM devices WHERE platform = 'ios'";
        }
        else
        {
            var names = target.UserIds.Select((id, i) =>
            {
                cmd.Parameters.AddWithValue($"$u{i}", id);
                return $"$u{i}";
            }).ToList();
            cmd.CommandText = $"SELECT device_token, environment FROM devices WHERE platform = 'ios' AND user_id IN ({string.Join(",", names)})";
        }

        var devices = new List<TargetDevice>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) devices.Add(new TargetDevice(reader.GetString(0), reader.GetString(1)));
        return devices;
    }

    /// <summary>Removes a token only if it belongs to <paramref name="userId"/> (logout).</summary>
    public int DeleteToken(string deviceToken, string userId)
    {
        using var conn = Open();
        return Execute(conn, "DELETE FROM devices WHERE device_token = $token AND user_id = $userId",
            ("$token", deviceToken), ("$userId", userId));
    }

    public int DeleteToken(string deviceToken)
    {
        using var conn = Open();
        return Execute(conn, "DELETE FROM devices WHERE device_token = $token", ("$token", deviceToken));
    }

    /// <summary>
    /// Removes a customer who deleted their account: their devices, the sends only they received, and their id from
    /// sends to several people. Safe to repeat.
    /// </summary>
    public int DeleteCustomer(string userId)
    {
        using var conn = Open();
        var removed = Execute(conn, "DELETE FROM devices WHERE user_id = $userId", ("$userId", userId));

        var logs = new List<(string Id, string Users)>();
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT notification_id, target_user_ids FROM notification_log WHERE target_user_ids LIKE $match";
            find.Parameters.AddWithValue("$match", $"%\"{userId}\"%");
            using var reader = find.ExecuteReader();
            while (reader.Read()) logs.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (id, users) in logs)
        {
            var others = (System.Text.Json.JsonSerializer.Deserialize<List<string>>(users) ?? []).Where(u => u != userId).ToList();
            if (others.Count == 0)
                Execute(conn, "DELETE FROM notification_log WHERE notification_id = $id", ("$id", id));
            else
                Execute(conn, "UPDATE notification_log SET target_user_ids = $users WHERE notification_id = $id",
                    ("$users", System.Text.Json.JsonSerializer.Serialize(others)), ("$id", id));
        }

        return removed;
    }

    public void InsertSendLog(SendLogEntry e)
    {
        using var conn = Open();
        Execute(conn, """
            INSERT INTO notification_log
              (notification_id, type, target_count, success_count, failure_count, removed_tokens, payload,
               title, body, image_url, target_all, target_user_ids)
            VALUES ($id, $type, $target, $success, $failure, $removed, $payload,
                    $title, $body, $image, $targetAll, $targetUsers)
            """,
            ("$id", e.NotificationId), ("$type", e.Type), ("$target", e.TargetCount), ("$success", e.SuccessCount),
            ("$failure", e.FailureCount), ("$removed", e.RemovedTokens), ("$payload", e.Payload),
            ("$title", e.Title), ("$body", e.Body), ("$image", e.ImageUrl),
            ("$targetAll", e.Target is null ? null : e.Target.All ? 1 : 0),
            ("$targetUsers", e.Target is null || e.Target.All ? null : System.Text.Json.JsonSerializer.Serialize(e.Target.UserIds)));
    }

    /// <summary>
    /// One page of the send log, newest first. With <paramref name="userId"/>, only sends that targeted that customer
    /// (sends to everyone, or to a list naming them).
    /// </summary>
    public (IReadOnlyList<SendLogItem> Items, int TotalCount) ListSendLog(int page, int pageSize, string? userId)
    {
        using var conn = Open();
        const string forUser = """
            WHERE target_all = 1
               OR EXISTS (SELECT 1 FROM json_each(notification_log.target_user_ids) WHERE json_each.value = $user)
            """;
        var where = userId is null ? "" : forUser;

        using var count = conn.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM notification_log {where}";
        if (userId is not null) count.Parameters.AddWithValue("$user", userId);
        var total = (int)(long)count.ExecuteScalar()!;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT notification_id, type, title, body, image_url, target_all, target_user_ids,
                   target_count, success_count, failure_count, sent_at
            FROM notification_log {where}
            ORDER BY sent_at DESC, rowid DESC
            LIMIT $limit OFFSET $offset
            """;
        if (userId is not null) cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", (long)(page - 1) * pageSize);

        var items = new List<SendLogItem>();
        using var r = cmd.ExecuteReader();
        string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
        {
            var users = Str(6) is { } json ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) : null;
            items.Add(new SendLogItem(r.GetString(0), r.GetString(1), Str(2), Str(3), Str(4),
                r.IsDBNull(5) ? null : r.GetInt64(5) == 1, users,
                r.GetInt32(7), r.GetInt32(8), r.GetInt32(9),
                // SQLite's datetime('now') is UTC without a zone: make it ISO 8601 for clients.
                r.GetString(10).Replace(' ', 'T') + "Z"));
        }
        return (items, total);
    }

    /// <summary>How many customers, and devices, can currently receive pushes.</summary>
    public (int Customers, int Devices) CountAudience()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT user_id), COUNT(*) FROM devices WHERE platform = 'ios'";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt32(1));
    }
}
