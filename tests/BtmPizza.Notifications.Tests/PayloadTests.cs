using System.Net;
using System.Text.Json.Nodes;
using BtmPizza.Notifications.Payload;

namespace BtmPizza.Notifications.Tests;

public class PayloadTests
{
    private static JsonObject Valid() => new()
    {
        ["type"] = "OFFER",
        ["title"] = "Two mediums for ₹599",
        ["body"] = "Today only at BTM Layout · earns double Dough Coins.",
        ["kicker"] = "Weekend offer",
        ["image_url"] = "https://s3.flavourslane.com/notificationimages/banner.jpeg",
        ["article"] = new JsonArray("First paragraph shown in the in-app popup.", "Second paragraph."),
        ["target"] = new JsonObject { ["user_ids"] = new JsonArray("u1") },
    };

    private static JsonObject With(string key, JsonNode? value)
    {
        var obj = Valid();
        obj[key] = value;
        return obj;
    }

    private static IReadOnlyList<string> ErrorsFor(JsonObject input)
    {
        try
        {
            SendRequestValidator.Validate(input);
            return [];
        }
        catch (ValidationException ex)
        {
            return ex.Errors;
        }
    }

    private const string SpecExample = """
        {"aps":{"alert":{"title":"Two mediums for ₹599","body":"Today only at BTM Layout · earns double Dough Coins."},"sound":"notification_chime.caf","thread-id":"OFFER"},"type":"OFFER","notification_id":"6f1c2a9e-1b7d-4c1e-9a55-2f3d8c0e7b41","kicker":"Weekend offer","image_url":"https://s3.flavourslane.com/notificationimages/banner.jpeg","article":["First paragraph shown in the in-app popup.","Second paragraph."]}
        """;

    [Fact]
    public void Builds_the_exact_payload_shape_from_the_spec()
    {
        var payload = PayloadBuilder.Build(SendRequestValidator.Validate(Valid()), "6f1c2a9e-1b7d-4c1e-9a55-2f3d8c0e7b41");
        Assert.Equal(SpecExample.Trim(), PayloadJson.Serialize(payload));
    }

    [Fact]
    public void Omits_optional_fields_when_absent()
    {
        var input = Valid();
        input.Remove("kicker");
        input.Remove("image_url");
        input.Remove("article");
        input["type"] = "PIZZA_NEWS";
        var payload = PayloadBuilder.Build(SendRequestValidator.Validate(input), Guid.NewGuid().ToString());
        Assert.Equal(["aps", "type", "notification_id"], payload.Select(kv => kv.Key));
        Assert.Equal("PIZZA_NEWS", payload["aps"]!["thread-id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("offer")]
    [InlineData("PROMO")]
    public void Rejects_unknown_or_lowercase_types(string type) => Assert.Single(ErrorsFor(With("type", type)));

    [Fact]
    public void Rejects_missing_type() => Assert.Single(ErrorsFor(With("type", null)));

    [Fact]
    public void Enforces_length_limits_counting_user_visible_characters()
    {
        Assert.Empty(ErrorsFor(With("title", new string('₹', 50))));
        Assert.Empty(ErrorsFor(With("title", string.Concat(Enumerable.Repeat("🍕", 50)))));
        Assert.Contains("title must be at most 50", ErrorsFor(With("title", new string('x', 51)))[0]);
        Assert.Contains("body must be at most 150", ErrorsFor(With("body", new string('x', 151)))[0]);
        Assert.Contains("kicker must be at most 25", ErrorsFor(With("kicker", new string('x', 26)))[0]);
        Assert.Contains("title must be a non-empty string", ErrorsFor(With("title", "  "))[0]);
        Assert.Contains("body is required", ErrorsFor(With("body", null))[0]);
    }

    [Fact]
    public void Validates_article()
    {
        Assert.Single(ErrorsFor(With("article", new JsonArray())));
        Assert.Single(ErrorsFor(With("article", "one string")));
        Assert.Single(ErrorsFor(With("article", new JsonArray("ok", ""))));
    }

    [Fact]
    public void Validates_image_url_syntax()
    {
        Assert.Contains("https", ErrorsFor(With("image_url", "http://example.com/a.jpg"))[0]);
        Assert.Contains("percent-encode", ErrorsFor(With("image_url", "https://example.com/my banner.jpg"))[0]);
        Assert.Contains("absolute", ErrorsFor(With("image_url", "/banner.jpg"))[0]);
        Assert.Empty(ErrorsFor(With("image_url", "https://example.com/my%20banner.jpg")));
    }

    [Fact]
    public void Validates_target()
    {
        Assert.Empty(ErrorsFor(With("target", new JsonObject { ["all"] = true })));
        Assert.Single(ErrorsFor(With("target", new JsonObject { ["all"] = false })));
        Assert.Single(ErrorsFor(With("target", new JsonObject { ["user_ids"] = new JsonArray() })));
        Assert.Single(ErrorsFor(With("target", new JsonObject { ["all"] = true, ["user_ids"] = new JsonArray("u1") })));
        Assert.Single(ErrorsFor(With("target", null)));
    }

    [Fact]
    public void Rejects_unknown_fields_such_as_category() =>
        Assert.Contains("unknown field \"category\"", ErrorsFor(With("category", "X"))[0]);

    [Fact]
    public void Rejects_payloads_over_4_KB()
    {
        var req = SendRequestValidator.Validate(With("article", new JsonArray(new string('x', 5000))));
        var ex = Assert.Throws<ValidationException>(() => PayloadBuilder.Build(req, Guid.NewGuid().ToString()));
        Assert.Contains("over the APNs limit of 4096", ex.Message);
    }

    [Fact]
    public void Ios_contract_accepts_the_spec_example_and_flags_deviations()
    {
        var spec = (JsonObject)JsonNode.Parse(SpecExample)!;
        Assert.Empty(IosContract.Check(spec));

        var broken = (JsonObject)spec.DeepClone();
        broken["aps"]!["category"] = "X";
        broken["aps"]!["sound"] = "notification_chime";
        broken["type"] = "offer";
        Assert.Equal(4, IosContract.Check(broken).Count); // category, sound, type, thread-id != type

        var wrongThread = (JsonObject)spec.DeepClone();
        wrongThread["aps"]!["thread-id"] = "PIZZA_NEWS";
        Assert.Contains("thread-id must equal type", IosContract.Check(wrongThread)[0]);
    }

    private sealed class FakeHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond());
    }

    private static ImageUrlVerifier Verifier(HttpStatusCode status, string? contentType, long? length = null) =>
        new(new HttpClient(new FakeHandler(() =>
        {
            var res = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
            if (contentType is not null) res.Content.Headers.ContentType = new(contentType);
            res.Content.Headers.ContentLength = length;
            return res;
        })));

    [Fact]
    public async Task Image_HEAD_check_requires_200_and_a_JPEG_or_PNG_content_type()
    {
        Assert.Empty(await Verifier(HttpStatusCode.OK, "image/jpeg").VerifyAsync("https://x/a.jpg"));
        Assert.Empty(await Verifier(HttpStatusCode.OK, "image/png").VerifyAsync("https://x/a.png"));
        var notFound = await Assert.ThrowsAsync<ValidationException>(() => Verifier(HttpStatusCode.NotFound, "image/jpeg").VerifyAsync("https://x/a"));
        Assert.Contains("returned 404", notFound.Message);
        var html = await Assert.ThrowsAsync<ValidationException>(() => Verifier(HttpStatusCode.OK, "text/html").VerifyAsync("https://x/a"));
        Assert.Contains("content type", html.Message);
        var warnings = await Verifier(HttpStatusCode.OK, "image/jpeg", 900 * 1024).VerifyAsync("https://x/a.jpg");
        Assert.Contains("900 KB", warnings[0]);
    }
}
