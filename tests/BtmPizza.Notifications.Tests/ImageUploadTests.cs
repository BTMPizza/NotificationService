using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using BtmPizza.Notifications.Configuration;
using BtmPizza.Notifications.Images;

namespace BtmPizza.Notifications.Tests;

/// <summary>POST /api/admin/notifications/images, as the admin app calls it through the gateway.</summary>
public sealed class ImageUploadTests(ServiceFixture fx) : IClassFixture<ServiceFixture>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 1, 2, 3, 4];

    private async Task<(HttpStatusCode Status, JsonNode? Json)> Upload(byte[] body, string contentType = "image/png",
        string? customerId = "owner-1", string? role = "OWNER")
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/notifications/images") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (customerId is not null) req.Headers.Add("customer-id", customerId);
        if (role is not null) req.Headers.Add("X-User-Role", role);
        var res = await fx.Client.SendAsync(req);
        return (res.StatusCode, JsonNode.Parse(await res.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Owner_upload_is_stored_and_its_public_url_returned()
    {
        var (status, json) = await Upload(Png);

        Assert.Equal(HttpStatusCode.OK, status);
        var url = json!["image_url"]!.GetValue<string>();
        Assert.StartsWith("https://s3.test/notificationimages/admin/", url);
        Assert.EndsWith(".png", url);
        var key = json["key"]!.GetValue<string>();
        Assert.Equal(Png, fx.Images.Stored[key].Data);
        Assert.Equal("image/png", fx.Images.Stored[key].ContentType);
    }

    [Fact]
    public async Task The_type_comes_from_the_bytes_not_the_header()
    {
        var (status, json) = await Upload(Jpeg, contentType: "image/png");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("image/jpeg", json!["content_type"]!.GetValue<string>());

        var (bad, err) = await Upload("<svg/>"u8.ToArray(), contentType: "image/png");
        Assert.Equal((HttpStatusCode)422, bad);
        Assert.Contains("JPEG, PNG or GIF", err!.ToJsonString());
    }

    [Fact]
    public async Task Only_owners_can_upload()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(Png, customerId: null)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(Png, role: "CUSTOMER")).Status);
    }

    [Fact]
    public async Task Empty_and_oversized_uploads_are_refused()
    {
        Assert.Equal((HttpStatusCode)422, (await Upload([])).Status);
        var big = new byte[ImageFormat.MaxBytes + 1];
        Png.CopyTo(big, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(big)).Status);
    }

    [Fact]
    public async Task Without_storage_configured_the_upload_says_so()
    {
        fx.Images.IsConfigured = false;
        try
        {
            var (status, json) = await Upload(Png);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.Contains("aren't configured", json!["error"]!.GetValue<string>());
        }
        finally
        {
            fx.Images.IsConfigured = true;
        }
    }

    /// <summary>
    /// Against a real S3-compatible server. Skipped unless NOTIFICATIONS_TEST_S3 is "endpoint|accessKey|secretKey|bucket",
    /// e.g. a throwaway MinIO: docker run -p 9000:9000 minio/minio server /data.
    /// </summary>
    [Fact]
    public async Task S3_store_puts_the_object_publicly_readable_by_key()
    {
        if (Environment.GetEnvironmentVariable("NOTIFICATIONS_TEST_S3") is not { Length: > 0 } spec) return;
        var p = spec.Split('|');
        using var store = new S3ImageStore(new ImageStorageCredentials(p[0], p[1], p[2], p[3], $"https://public.test/{p[3]}"));

        var url = await store.PutAsync("admin/test-upload.png", Png, "image/png", CancellationToken.None);

        Assert.Equal($"https://public.test/{p[3]}/admin/test-upload.png", url);
        using var http = new HttpClient();
        var back = await http.GetAsync($"{p[0].TrimEnd('/')}/{p[3]}/admin/test-upload.png");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(Png, await back.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", back.Content.Headers.ContentType?.MediaType);
    }
}
