namespace RnRomManager.Core.Media;

/// <summary>
/// IGDB suggestions S5: each registered system id → its IGDB platform id, used to narrow the game search.
/// <c>IgdbPlatformsTests</c> keeps it in step with the registered handlers.
/// </summary>
public static class IgdbPlatforms
{
    public static IReadOnlyDictionary<string, int> ById { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["3do"] = 50,
        ["3ds"] = 37,
        ["a2600"] = 59,
        ["a5200"] = 66,
        ["a7800"] = 60,
        ["amiga"] = 16,
        ["c128"] = 15, // shared with c64
        ["c64"] = 15,
        ["coleco"] = 68,
        ["fds"] = 51,
        ["dos"] = 13,
        ["gb"] = 33,
        ["gba"] = 24,
        ["gbc"] = 22,
        ["gc"] = 21,
        ["gg"] = 35,
        ["jaguar"] = 62,
        ["lynx"] = 61,
        ["mame2003"] = 52, // IGDB's Arcade platform
        ["fbneo"] = 52, // shared with mame2003 (S3)
        ["md"] = 29,
        ["n64"] = 4,
        ["nds"] = 20,
        ["nes"] = 18,
        ["ngp"] = 119,
        ["ngpc"] = 120,
        ["pcfx"] = 274,
        ["pet"] = 90,
        ["psp"] = 38,
        ["psx"] = 7,
        ["s32x"] = 30,
        ["saturn"] = 32,
        ["segacd"] = 78,
        ["sms"] = 64,
        // Satellaview and Sufami Turbo: the SNES platform (spec B7), the fallback for a system whose own IGDB platform
        // wasn't looked up; IgdbPlatformsTests names them as the only repeated ids.
        ["satellaview"] = 19,
        ["sufami"] = 19,
        ["snes"] = 19,
        ["switch"] = 130,
        ["tg16"] = 86,
        ["tgcd"] = 150,
        ["vb"] = 87,
        ["vic20"] = 71,
        ["wii"] = 5,
        ["x360"] = 12,
    };
}
