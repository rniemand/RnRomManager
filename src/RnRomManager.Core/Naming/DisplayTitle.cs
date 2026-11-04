using System.Text.RegularExpressions;

namespace RnRomManager.Core.Naming;

/// <summary>Spec §6.1 / D24: a trailing article moves to the front (<c>Legend of Zelda, The - …</c> → <c>The Legend of Zelda - …</c>).</summary>
public static partial class DisplayTitle
{
    public static string From(string title) => Article().Replace(title, "$2 $1$3");

    [GeneratedRegex(@"^(.*?), (The|A|An|Die|Der|Das|Le|La|Les|El|Il)( - .*)?\z", RegexOptions.Singleline)]
    private static partial Regex Article();
}
