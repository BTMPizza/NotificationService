using System.Globalization;
using System.Text.Json.Nodes;
using BtmPizza.Notifications.Payload;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BtmPizza.Notifications.Data;

/// <summary>Append-only record of every delivered notification, keyed by the time it was sent.</summary>
public interface INotificationLog
{
    Task WriteAsync(DateTime sentAtUtc, JsonObject payload, CancellationToken ct = default);
}

/// <summary>Used when mongodb.url is not set in credentials.json.</summary>
public sealed class NoNotificationLog : INotificationLog
{
    public Task WriteAsync(DateTime sentAtUtc, JsonObject payload, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Writes one document per notification: <c>{ _id: "&lt;UTC time&gt;", value: &lt;APNs payload&gt; }</c>.
/// This service is the only writer and never updates or deletes, so no locking is needed.
/// </summary>
public sealed class MongoNotificationLog : INotificationLog
{
    public const string DefaultDatabase = "btm_notifications";
    public const string Collection = "notification_log";

    private const int DuplicateKeyAttempts = 5;
    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoNotificationLog(string url)
    {
        var mongoUrl = new MongoUrl(url);
        var settings = MongoClientSettings.FromUrl(mongoUrl);
        // Fail fast when MongoDB is down instead of holding up the send response for the 30 s default.
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ConnectTimeout = TimeSpan.FromSeconds(3);
        _collection = new MongoClient(settings)
            .GetDatabase(mongoUrl.DatabaseName ?? DefaultDatabase)
            .GetCollection<BsonDocument>(Collection);
    }

    /// <summary>Fixed-width UTC timestamp with 100 ns precision, so keys sort chronologically as strings.</summary>
    public static string Key(DateTime sentAtUtc) => sentAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    public async Task WriteAsync(DateTime sentAtUtc, JsonObject payload, CancellationToken ct = default)
    {
        var value = BsonDocument.Parse(PayloadJson.Serialize(payload));
        // Two sends can land on the same tick; move the later one forward rather than lose it.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _collection.InsertOneAsync(new BsonDocument { ["_id"] = Key(sentAtUtc), ["value"] = value }, cancellationToken: ct);
                return;
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey && attempt < DuplicateKeyAttempts)
            {
                sentAtUtc = sentAtUtc.AddTicks(1);
            }
        }
    }
}
