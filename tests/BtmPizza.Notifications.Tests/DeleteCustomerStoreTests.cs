using BtmPizza.Notifications.Data;
using BtmPizza.Notifications.Payload;
using Microsoft.Data.Sqlite;

namespace BtmPizza.Notifications.Tests;

public sealed class DeleteCustomerStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"btm-delete-{Guid.NewGuid()}.db");
    private DeviceStore Store => new(_db);

    private void Log(string id, params string[] users) =>
        Store.InsertSendLog(new SendLogEntry(id, "OFFER", users.Length, 0, 0, 0, "{}", "Hi", "There", null, new Target(false, users)));

    private string? Users(string id)
    {
        using var conn = new SqliteConnection($"Data Source={_db}");
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT target_user_ids FROM notification_log WHERE notification_id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    [Fact]
    public void Removes_sends_only_they_received_and_their_id_from_shared_ones_and_can_be_repeated()
    {
        Log("alone", "gone");
        Log("shared", "gone", "stays");
        Log("other", "stays");
        Log("similar", "gone-too");

        Store.DeleteCustomer("gone");
        Store.DeleteCustomer("gone");

        Assert.Null(Users("alone"));
        Assert.Equal("""["stays"]""", Users("shared"));
        Assert.Equal("""["stays"]""", Users("other"));
        Assert.Equal("""["gone-too"]""", Users("similar"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }
}
