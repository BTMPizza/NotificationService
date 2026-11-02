using BtmPizza.Notifications.Data;
using Microsoft.Data.Sqlite;

namespace BtmPizza.Notifications.Tests;

public class DeviceStoreMigrationTests
{
    [Fact]
    public void Existing_database_gets_the_new_columns_and_keeps_its_devices()
    {
        var path = Path.Combine(Path.GetTempPath(), "btm-notifications-old-" + Guid.NewGuid() + ".db");
        var token = new string('e', 64);
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // The devices table as it was before bundle_id / app_version existed.
            cmd.CommandText = $"""
                CREATE TABLE devices (
                  id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT UNIQUE, device_token TEXT NOT NULL UNIQUE,
                  platform TEXT NOT NULL, user_id TEXT NOT NULL, environment TEXT NOT NULL,
                  created_at TEXT NOT NULL DEFAULT (datetime('now')), updated_at TEXT NOT NULL DEFAULT (datetime('now')));
                INSERT INTO devices (device_token, platform, user_id, environment) VALUES ('{token}', 'ios', 'old-user', 'production');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new DeviceStore(path);
        _ = new DeviceStore(path); // opening twice must not try to add the columns again

        var targets = store.FindTargets(new Payload.Target(false, ["old-user"]));
        Assert.Single(targets);
        var row = store.Upsert(new DeviceRegistration(token, "ios", "old-user", "production", null, "com.nayakkarthik.BTMPizza", "1.0"));
        Assert.Equal("old-user", row.UserId);
    }
}
