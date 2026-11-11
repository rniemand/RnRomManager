namespace RnRomManager.Core.Media;

/// <summary>
/// Spec T8: the HTTP client for titledb's image CDN, kept apart from the libretro thumbnail client. The Api registers it
/// as a typed named client with the thumbnail retry policy and redirects disabled.
/// </summary>
public sealed class TitleDbImageClient(HttpClient http)
{
    public HttpClient Http { get; } = http;
}
