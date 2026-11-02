using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace BtmPizza.Notifications.Tests;

/// <summary>The BTM Pizza Admin app's endpoints, called through the gateway with an OWNER token.</summary>
public sealed class AdminEndpointsTests(ServiceFixture fx) : IClassFixture<ServiceFixture>
{
    private static string Token(string prefix) => string.Concat(Enumerable.Repeat(prefix, 32));

    private async Task<(HttpStatusCode Status, JsonNode? Json)> Call(HttpMethod method, string path, object? body = null,
        string? customerId = "owner-1", string? role = "OWNER")
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (customerId is not null) req.Headers.Add("customer-id", customerId);
        if (role is not null) req.Headers.Add("X-User-Role", role);
        var res = await fx.Client.SendAsync(req);
        return (res.StatusCode, JsonNode.Parse(await res.Content.ReadAsStringAsync()));
    }

    private Task Register(string customerId, string token) =>
        fx.Client.PostAsJsonAsync("/devices", new { device_token = token, platform = "ios", user_id = customerId });

    [Fact]
    public async Task Only_owners_signed_in_through_the_gateway_get_in()
    {
        var send = new { type = "OFFER", title = "Hi", body = "There", target = new { all = true }, dry_run = true };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Post, "/api/admin/notifications/send", send, customerId: null)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/admin/notifications/send", send, role: "CUSTOMER")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/admin/notifications", role: null)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/admin/notifications/audience", role: "CAPTAIN")).Status);
    }

    [Fact]
    public async Task Sends_are_logged_with_their_content_and_target_and_filterable_by_customer()
    {
        await Register("adm-a", Token("a1"));
        await Register("adm-b", Token("a2"));

        var (status, json) = await Call(HttpMethod.Post, "/api/admin/notifications/send", new
        {
            type = "OFFER", title = "Margherita Monday", body = "20% off today",
            target = new { user_ids = new[] { "adm-a" } },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!["target_count"]!.GetValue<int>());

        await Call(HttpMethod.Post, "/api/admin/notifications/send", new
        {
            type = "PIZZA_NEWS", title = "New oven", body = "Wood-fired from today", target = new { all = true },
        });
        // Dry runs aren't logged.
        await Call(HttpMethod.Post, "/api/admin/notifications/send", new
        {
            type = "OFFER", title = "Preview", body = "Only a preview", target = new { all = true }, dry_run = true,
        });

        var (_, forB) = await Call(HttpMethod.Get, "/api/admin/notifications?user_id=adm-b");
        var items = forB!["items"]!.AsArray();
        Assert.Contains(items, i => i!["title"]!.GetValue<string>() == "New oven" && i["target"]!["all"]!.GetValue<bool>());
        Assert.DoesNotContain(items, i => i!["title"]!.GetValue<string>() == "Margherita Monday");
        Assert.DoesNotContain(items, i => i!["title"]!.GetValue<string>() == "Preview");

        var (_, forA) = await Call(HttpMethod.Get, "/api/admin/notifications?user_id=adm-a&page_size=100");
        var offer = forA!["items"]!.AsArray().Single(i => i!["title"]!.GetValue<string>() == "Margherita Monday")!;
        Assert.Equal("20% off today", offer["body"]!.GetValue<string>());
        Assert.False(offer["target"]!["all"]!.GetValue<bool>());
        Assert.Equal("adm-a", offer["target"]!["user_ids"]![0]!.GetValue<string>());
        Assert.EndsWith("Z", offer["sent_at"]!.GetValue<string>());

        var (_, all) = await Call(HttpMethod.Get, "/api/admin/notifications?page_size=1");
        Assert.Single(all!["items"]!.AsArray());
        Assert.True(all!["has_more"]!.GetValue<bool>());
        Assert.Equal("New oven", all["items"]![0]!["title"]!.GetValue<string>()); // newest first

        var (_, audience) = await Call(HttpMethod.Get, "/api/admin/notifications/audience");
        Assert.True(audience!["customers"]!.GetValue<int>() >= 2);
    }

    [Fact]
    public async Task Bad_paging_is_rejected()
    {
        Assert.Equal((HttpStatusCode)422, (await Call(HttpMethod.Get, "/api/admin/notifications?page_size=500")).Status);
    }
}
