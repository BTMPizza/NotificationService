namespace BtmPizza.Notifications.Payload;

public interface IImageUrlVerifier
{
    /// <summary>Throws ValidationException if the image isn't live; returns non-fatal warnings.</summary>
    Task<IReadOnlyList<string>> VerifyAsync(string imageUrl, CancellationToken ct = default);
}

/// <summary>
/// Checks that image_url is live: a HEAD request must return 200 with a JPEG or PNG content type.
/// </summary>
public sealed class ImageUrlVerifier(HttpClient http) : IImageUrlVerifier
{
    private static readonly string[] ImageContentTypes = ["image/jpeg", "image/png"];
    private const long RecommendedImageBytes = 500 * 1024;

    public async Task<IReadOnlyList<string>> VerifyAsync(string imageUrl, CancellationToken ct = default)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, imageUrl), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ValidationException($"image_url HEAD request failed: {ex.Message}");
        }

        using (res)
        {
            if ((int)res.StatusCode != 200)
                throw new ValidationException($"image_url HEAD request returned {(int)res.StatusCode}, expected 200");

            var contentType = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (!ImageContentTypes.Contains(contentType))
                throw new ValidationException(
                    $"image_url content type is \"{(contentType == "" ? "missing" : contentType)}\", expected {string.Join(" or ", ImageContentTypes)}");

            var warnings = new List<string>();
            if (res.Content.Headers.ContentLength is > RecommendedImageBytes and var size)
                warnings.Add($"image is {Math.Round(size / 1024.0)} KB; keep it under 500 KB so the popup loads quickly");
            return warnings;
        }
    }
}
