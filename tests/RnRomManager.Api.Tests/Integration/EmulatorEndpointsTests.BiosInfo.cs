using System.Net;
using System.Text.Json;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary><c>GET /api/admin/bios</c>: the BIOS info tab's checks against the temp <c>Paths:Bios</c> folder.</summary>
public partial class EmulatorEndpointsTests
{
    private async Task<AuthClient> BiosAdminAsync()
    {
        var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "bios-admin", TestUsers.AllRoles);
        (await admin.LoginAsync("bios-admin", TestUsers.Password)).Dispose();
        return admin;
    }

    private async Task<JsonElement> BiosInfoAsync()
    {
        using var admin = await BiosAdminAsync();
        return await BiosInfoAsync(admin);
    }

    private static async Task<JsonElement> BiosInfoAsync(AuthClient admin)
    {
        using var response = await admin.GetAsync("/api/admin/bios");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static JsonElement Group(JsonElement info, string system) =>
        info.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("system").GetString() == system);

    private static string Status(JsonElement group, string name) =>
        group.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name).GetProperty("status").GetString()!;

    [Test]
    public async Task BiosInfo_reports_valid_wrong_size_missing_and_other_files()
    {
        WriteBios("COLECOVISION.ROM"); // 8 KiB, matched ignoring case
        WriteBios("scph5501.bin", new byte[100]);
        WriteBios("bios_CD_E.bin", new byte[128 * 1024]);
        WriteBios("neogeo.zip", new byte[10]);

        var info = await BiosInfoAsync();

        Assert.That(info.GetProperty("env").GetString(), Is.EqualTo("Paths__Bios"));
        Assert.That(info.GetProperty("folderExists").GetBoolean(), Is.True);

        var coleco = Group(info, "ColecoVision");
        Assert.That(Status(coleco, "colecovision.rom"), Is.EqualTo("Valid"));
        Assert.That(coleco.GetProperty("ok").GetBoolean(), Is.True);
        var md5 = coleco.GetProperty("files")[0].GetProperty("md5").GetString();
        Assert.That(md5, Is.EqualTo(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Bios))));

        var psx = Group(info, "PlayStation");
        Assert.That(Status(psx, "scph5501.bin"), Is.EqualTo("WrongSize"));
        Assert.That(psx.GetProperty("ok").GetBoolean(), Is.False, "an optional file that's present must be valid");

        var segaCd = Group(info, "Sega CD");
        Assert.That(Status(segaCd, "bios_CD_E.bin"), Is.EqualTo("Valid"));
        Assert.That(Status(segaCd, "bios_CD_U.bin"), Is.EqualTo("Missing"));
        Assert.That(segaCd.GetProperty("ok").GetBoolean(), Is.True, "one of the three is enough");
        Assert.That(segaCd.GetProperty("coreEnv").GetString(), Is.EqualTo("Emulator__GenesisCorePath"));
        Assert.That(segaCd.GetProperty("coreConfigured").GetBoolean(), Is.True);

        Assert.That(Group(info, "PC-FX").GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(Group(info, "Saturn").GetProperty("ok").GetBoolean(), Is.True, "optional and absent");
        Assert.That(Group(info, "Commodore Amiga CD32").GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(Group(info, "Commodore Amiga CD32 (extended ROM)").GetProperty("ok").GetBoolean(), Is.True, "no Kickstart, so not needed");
        Assert.That(Group(info, "Commodore CDTV").GetProperty("ok").GetBoolean(), Is.False);

        Assert.That(info.GetProperty("otherFiles").EnumerateArray().Select(f => f.GetProperty("name").GetString()),
            Is.EqualTo(new[] { "neogeo.zip" }));
    }

    [Test]
    public async Task BiosInfo_amiga_cd_kickstarts()
    {
        (bool Kick, bool Ext) Cd32Ok(JsonElement info) =>
            (Group(info, "Commodore Amiga CD32").GetProperty("ok").GetBoolean(), Group(info, "Commodore Amiga CD32 (extended ROM)").GetProperty("ok").GetBoolean());

        using var admin = await BiosAdminAsync();
        WriteBios("kick40060.CD32", new byte[512 * 1024]);
        var info = await BiosInfoAsync(admin);
        Assert.That(Cd32Ok(info), Is.EqualTo((true, false)), "a 512 KiB Kickstart needs its extended ROM");
        Assert.That(Status(Group(info, "Commodore Amiga CD32 (extended ROM)"), "kick40060.CD32.ext"), Is.EqualTo("Missing"));

        WriteBios("kick40060.CD32.ext", new byte[512 * 1024]);
        Assert.That(Cd32Ok(await BiosInfoAsync(admin)), Is.EqualTo((true, true)));

        WriteBios("kick40060.CD32", new byte[1024 * 1024]);
        File.Delete(Path.Combine(BiosDir, "kick40060.CD32.ext"));
        Assert.That(Cd32Ok(await BiosInfoAsync(admin)), Is.EqualTo((true, true)), "the 1 MiB combined ROM needs no extended ROM");

        WriteBios("kick34005.A500", new byte[256 * 1024]);
        WriteBios("kick34005.CDTV", new byte[128 * 1024]);
        var cdtv = Group(await BiosInfoAsync(admin), "Commodore CDTV");
        Assert.That((Status(cdtv, "kick34005.A500"), Status(cdtv, "kick34005.CDTV"), cdtv.GetProperty("ok").GetBoolean()), Is.EqualTo(("Valid", "WrongSize", false)));
    }
}
