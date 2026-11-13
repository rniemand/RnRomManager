namespace RnRomManager.Core.Naming;

/// <summary>
/// Known No-Intro region names (spec §6.1): the No-Intro naming convention's country list. Every region used in
/// the libretro GB/GBC DATs (checked 2026-11-04) is included. A <c>(…)</c> group whose comma-separated parts are
/// all in this list is the region group; it ends the base title.
/// </summary>
public static class NoIntroRegions
{
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "World", "Europe", "Asia", "Latin America", "Scandinavia", "Unknown",
        "Argentina", "Australia", "Austria", "Belgium", "Brazil", "Canada", "China", "Croatia", "Czech Republic",
        "Denmark", "Finland", "France", "Germany", "Greece", "Hong Kong", "Hungary", "India", "Ireland", "Israel",
        "Italy", "Japan", "Korea", "Mexico", "Netherlands", "New Zealand", "Norway", "Poland", "Portugal", "Russia",
        "Singapore", "Slovakia", "South Africa", "Spain", "Sweden", "Switzerland", "Taiwan", "Thailand", "Turkey",
        "United Kingdom", "USA",
        // B3a: Redump names use these.
        "UK", "United Arab Emirates",
    };

    /// <summary>True when every <c>", "</c>-separated part of a group's content is a known region.</summary>
    public static bool IsRegionGroup(string content) =>
        content.Length > 0 && content.Split(", ").All(All.Contains);
}
