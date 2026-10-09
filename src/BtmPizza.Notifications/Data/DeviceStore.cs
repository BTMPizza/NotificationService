using BtmPizza.Notifications.Payload;
using Microsoft.Data.Sqlite;

namespace BtmPizza.Notifications.Data;

public sealed record DeviceRegistration(string DeviceToken, string Platform, string UserId, string Environment, string? DeviceId);

public sealed record Device(string DeviceToken, string UserId, string Environment, string UpdatedAt);

public sealed record TargetDevice(string DeviceToken, string Environment);

public sealed record SendLogEntry(
    string NotificationId, string Type, int TargetCount, int SuccessCount, int FailureCount, int RemovedTokens, string Payload);

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
            };
            if (d.DeviceId is not null)
            {
                Execute(conn, tx, "DELETE FROM devices WHERE device_token = $token AND (device_id IS NULL OR device_id != $deviceId)", args);
                Execute(conn, tx, """
                    INSERT INTO devices (device_id, device_token, platform, user_id, environment)
                    VALUES ($deviceId, $token, $platform, $userId, $env)
                    ON CONFLICT(device_id) DO UPDATE SET
                      device_token = excluded.device_token,
                      platform     = excluded.platform,
                      user_id      = excluded.user_id,
                      environment  = excluded.environment,
                      updated_at   = datetime('now')
                    """, args);
            }
            else
            {
                Execute(conn, tx, """
                    INSERT INTO devices (device_token, platform, user_id, environment)
                    VALUES ($token, $platform, $userId, $env)
                    ON CONFLICT(device_token) DO UPDATE SET
                      platform    = excluded.platform,
                      user_id     = excluded.user_id,
                      environment = excluded.environment,
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

    public int DeleteToken(string deviceToken)
    {
        using var conn = Open();
        return Execute(conn, "DELETE FROM devices WHERE device_token = $token", ("$token", deviceToken));
    }

    public void InsertSendLog(SendLogEntry e)
    {
        using var conn = Open();
        Execute(conn, """
            INSERT INTO notification_log
              (notification_id, type, target_count, success_count, failure_count, removed_tokens, payload)
            VALUES ($id, $type, $target, $success, $failure, $removed, $payload)
            """,
            ("$id", e.NotificationId), ("$type", e.Type), ("$target", e.TargetCount), ("$success", e.SuccessCount),
            ("$failure", e.FailureCount), ("$removed", e.RemovedTokens), ("$payload", e.Payload));
    }
}
