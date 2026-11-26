using System.Net;
using RnRomManager.Core.Media;

namespace RnRomManager.Api.Http;

/// <summary>U3/U4: which addresses an image-URL download may reach, and how long it may take (tests shorten both).</summary>
public sealed record ImageUrlPolicy(Func<IPAddress, bool> IsAllowed, TimeSpan Timeout)
{
    public static ImageUrlPolicy Default { get; } = new(PublicAddress.IsPublic, TimeSpan.FromSeconds(15));
}
