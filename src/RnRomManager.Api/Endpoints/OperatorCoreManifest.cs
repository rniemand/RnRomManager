using RnRomManager.Core.Configuration;

namespace RnRomManager.Api.Endpoints;

/// <summary>One operator-supplied core file: its exact name, length and lowercase hex SHA-256.</summary>
public sealed record OperatorCoreFile(string Name, long Length, string Sha256, string ContentType);

/// <summary>
/// B1: one EmulatorJS core the operator supplies (never bundled): its id, the name messages use, the option holding its
/// folder, the problem code of a mismatch and the only files <c>/api/emulator/cores/{core}</c> serves.
/// </summary>
public sealed record OperatorCore(string Id, string DisplayName, Func<EmulatorOptions, string> PathOf, string MismatchCode,
    IReadOnlyList<OperatorCoreFile> Files)
{
    /// <summary>The manifest entry with exactly this name (ordinal), or null.</summary>
    public OperatorCoreFile? Find(string name) => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// B1/B2: every operator-supplied core, each file pinned to the EmulatorJS 4.2.3 build so a corrupt, other-version or
/// altered file never reaches the browser. Registered as a singleton that tests replace.
/// </summary>
public sealed record OperatorCoreManifest(IReadOnlyList<OperatorCore> Cores)
{
    public const string GenesisPlusGx = "genesis_plus_gx";
    public const string Picodrive = "picodrive";
    public const string Snes9x = "snes9x";
    public const string Opera = "opera";
    public const string Vice = "vice_x64sc";
    public const string VicePet = "vice_xpet";
    public const string ViceVic = "vice_xvic";
    public const string ViceC128 = "vice_x128";
    public const string Mupen64PlusNext = "mupen64plus_next";
    public const string Mame2003Plus = "mame2003_plus";
    public const string Fbneo = "fbneo";

    /// <summary>The pinned 4.2.3 files of each core (each report is its package's <c>reports/</c> copy).</summary>
    public static OperatorCoreManifest Pinned { get; } = new(
    [
        // @emulatorjs/core-genesis_plus_gx@4.2.3
        new(GenesisPlusGx, "Genesis Plus GX", o => o.GenesisCorePath, EmulatorEndpoints.GenesisCoreMismatch,
        [
            new("genesis_plus_gx-wasm.data", 1_203_661,
                "190297a6f86757405090f1a2266f67dfe1a570a528c583434ed3641a5664f768", "application/octet-stream"),
            new("genesis_plus_gx-legacy-wasm.data", 1_204_803,
                "2a1edeb68d7ec882149ed3cd5d3aa95a6ac231ebabaf82cdb56175a8b62967cc", "application/octet-stream"),
            new("genesis_plus_gx.json", 129,
                "5936a8ce8d7f010d5bfdd8c3bba2b2414f103b3a703121e56f2724b24dbe7ff3", "application/json"),
        ]),
        // https://registry.npmjs.org/@emulatorjs/core-picodrive/-/core-picodrive-4.2.3.tgz (payloads at the package
        // root, the report from reports/). Its licence forbids commercial use, so it's never bundled.
        new(Picodrive, "PicoDrive", o => o.PicodriveCorePath, EmulatorEndpoints.PicodriveCoreMismatch,
        [
            new("picodrive-wasm.data", 1_034_483,
                "bb5d50b8b88111b583977d2f7a16d01a822b3deda9205048d99ecaad2c56d861", "application/octet-stream"),
            new("picodrive-legacy-wasm.data", 1_035_609,
                "cc42b0c95e9edfd0e5c689211e8e85e45f4525be2d814af8ca9331ef90e0620b", "application/octet-stream"),
            new("picodrive.json", 123,
                "2b09f29e095a003c15abf8e5bdfb0eaca96d6c6121183b3bd92ed85e860918d3", "application/json"),
        ]),
        // SNES B2: https://registry.npmjs.org/@emulatorjs/core-snes9x/-/core-snes9x-4.2.3.tgz (payloads at the package
        // root, the report from reports/). Its licence forbids commercial use, so it's never bundled.
        new(Snes9x, "Snes9x", o => o.Snes9xCorePath, EmulatorEndpoints.Snes9xCoreMismatch,
        [
            new("snes9x-wasm.data", 1_093_765,
                "eaa0bcfce67673809886e50387a80a616b719502175db64c090d04c9d75958ee", "application/octet-stream"),
            new("snes9x-legacy-wasm.data", 1_092_437,
                "7d427a575cefad98ff400493fa1d7e892da63fe7bab68979babd9cea0bfaaf3b", "application/octet-stream"),
            new("snes9x.json", 120,
                "dc7ac963eb7935a7ac78956235ac0b8912ec785c57026336825aa2ed8031b3ad", "application/json"),
        ]),
        // 3DO B5: https://registry.npmjs.org/@emulatorjs/core-opera/-/core-opera-4.2.3.tgz (payloads at the package
        // root, the report from reports/). Its licence forbids commercial use, so it's never bundled.
        new(Opera, "Opera", o => o.OperaCorePath, EmulatorEndpoints.OperaCoreMismatch,
        [
            new("opera-wasm.data", 854_147,
                "3e737f4f739814c12c017a5f26cb1e43bdfe3ac6f2d3bf6e8972633df49e33d4", "application/octet-stream"),
            new("opera-legacy-wasm.data", 853_889,
                "41d3a7336cd304693c86974b50df5f7a90d9557077b01e0a1e928b3a38fe4f86", "application/octet-stream"),
            new("opera.json", 119,
                "0245b492b516ef4b1c0490e3f05dbeb1d74074ea767cd54fc34747e31b2a0203", "application/json"),
        ]),
        // C64 B7: https://registry.npmjs.org/@emulatorjs/core-vice_x64sc/-/core-vice_x64sc-4.2.3.tgz (payloads at the
        // package root, the report from reports/). Its binary embeds Commodore's system ROMs, so it's never bundled.
        new(Vice, "VICE", o => o.ViceCorePath, EmulatorEndpoints.ViceCoreMismatch,
        [
            new("vice_x64sc-wasm.data", 1_523_457,
                "77f58884c81b58721cbc4754ffc5574838219258e9390630c352a494a3f335ab", "application/octet-stream"),
            new("vice_x64sc-legacy-wasm.data", 1_523_390,
                "8a22bc81c0dd7104cd23ceec5117a9f47ca9503e280e4fbc9d0346f51b75253e", "application/octet-stream"),
            new("vice_x64sc.json", 147,
                "5c50bc4ff5546a2c6fc4bb7949efd200ea0ac6b4723713e742fb659228e9a3aa", "application/json"),
        ]),
        // Commodore PET: https://registry.npmjs.org/@emulatorjs/core-vice_xpet/-/core-vice_xpet-4.2.3.tgz. Embeds the PET's
        // ROMs like vice_x64sc, so it's never bundled; its files sit in the same folder as the C64 core's.
        new(VicePet, "VICE (PET)", o => o.ViceCorePath, EmulatorEndpoints.ViceCoreMismatch,
        [
            new("vice_xpet-wasm.data", 1_368_677,
                "eef0d83f7c1af43d51c3384a07db4196ea7c2bbe1292e9e52fade3d3107513c5", "application/octet-stream"),
            new("vice_xpet-legacy-wasm.data", 1_368_450,
                "98ac6fbb90824a77ae6480a431c465d41e8cecce39394e036648d57e86297361", "application/octet-stream"),
            new("vice_xpet.json", 123,
                "9bf5ec80ab1f58dc7cdccd73f0141385b030f262ce20aaae4403369dd7061301", "application/json"),
        ]),
        // Commodore VIC-20: https://registry.npmjs.org/@emulatorjs/core-vice_xvic/-/core-vice_xvic-4.2.3.tgz. Embeds the VIC-20's
        // ROMs like vice_x64sc, so it's never bundled; its files sit in the same folder as the C64 core's.
        new(ViceVic, "VICE (VIC-20)", o => o.ViceCorePath, EmulatorEndpoints.ViceCoreMismatch,
        [
            new("vice_xvic-wasm.data", 1_396_793,
                "4c7de6f858e100e9aea93085d5aef01bdfe87399c21c0fde4e1b6c7f1da56316", "application/octet-stream"),
            new("vice_xvic-legacy-wasm.data", 1_396_460,
                "42067705c2fd9c3861247f0d251a1481a85b5c00fb7ba93798ed0fe65b9fdd70", "application/octet-stream"),
            new("vice_xvic.json", 123,
                "9c3439164e63320da512deded61cdbfa37ce018eedb3995e9eb0af616a0198fe", "application/json"),
        ]),
        // Commodore 128: https://registry.npmjs.org/@emulatorjs/core-vice_x128/-/core-vice_x128-4.2.3.tgz. Embeds the C128's
        // ROMs like vice_x64sc, so it's never bundled; its files sit in the same folder as the C64 core's.
        new(ViceC128, "VICE (C128)", o => o.ViceCorePath, EmulatorEndpoints.ViceCoreMismatch,
        [
            new("vice_x128-wasm.data", 1_595_414,
                "dbac85e530b006c2d17e200c445de582c7ea272bacf333e4b4f8c1e391ed8506", "application/octet-stream"),
            new("vice_x128-legacy-wasm.data", 1_596_799,
                "71383f4b81943b936c2c0b4258af231f6adcbf3139c2d0a17b8f20a239712c86", "application/octet-stream"),
            new("vice_x128.json", 146,
                "bf4d43822a61a423e00e0d2bffd063ca139a77e78b66507aeb90343fcc221af3", "application/json"),
        ]),
        // N64: https://registry.npmjs.org/@emulatorjs/core-mupen64plus_next/-/core-mupen64plus_next-4.2.3.tgz (payloads at
        // the package root, the report from reports/). Its GLideN64 renderer is GPL-2.0-only, linked with GPL-3.0 code,
        // so it's never bundled.
        new(Mupen64PlusNext, "Mupen64Plus-Next", o => o.Mupen64PlusCorePath, EmulatorEndpoints.Mupen64PlusCoreMismatch,
        [
            new("mupen64plus_next-wasm.data", 1_451_795,
                "2da1cbce9fda395e3ae83ca5787353baa159142d45ef3ea90f108b92524f76cc", "application/octet-stream"),
            new("mupen64plus_next-legacy-wasm.data", 1_451_870,
                "425072f4bf94eec02633cbe9b84f47d6803dc0ec1b3c3b8de3ebbd2eb5617c3f", "application/octet-stream"),
            new("mupen64plus_next.json", 153,
                "270105553cec57fd1058c50e0541b8b89dbd30b2323fd70682036f6919b805cc", "application/json"),
        ]),
        // Arcade P1: https://registry.npmjs.org/@emulatorjs/core-mame2003_plus/-/core-mame2003_plus-4.2.3.tgz (payloads at the
        // package root, the report from reports/). The MAME licence forbids commercial use, so it's never bundled. The
        // -thread- payloads aren't used: the core plays in the modal.
        new(Mame2003Plus, "MAME 2003-Plus", o => o.Mame2003PlusCorePath, EmulatorEndpoints.Mame2003PlusCoreMismatch,
        [
            new("mame2003_plus-wasm.data", 5_391_355,
                "cb6d9c80a88b65d1579d16d02128a678f8d1cd3f51de1479e647cea27b13247b", "application/octet-stream"),
            new("mame2003_plus-legacy-wasm.data", 5_388_568,
                "a01286082b7b2b83cc10e702afaf730f5effedb49e583a6ca73f59bc28b0c12a", "application/octet-stream"),
            new("mame2003_plus.json", 127,
                "bda548626942b8878beb6de3487043eea80e633bd53dd69e7bae7457373a48a1", "application/json"),
        ]),
        // Arcade (FinalBurn Neo) P1: https://registry.npmjs.org/@emulatorjs/core-fbneo/-/core-fbneo-4.2.3.tgz (payloads at the
        // package root, the report from reports/). Its licence forbids commercial use, so it's never bundled. The -thread-
        // payloads aren't used: the core plays in the modal.
        new(Fbneo, "FinalBurn Neo", o => o.FbneoCorePath, EmulatorEndpoints.FbneoCoreMismatch,
        [
            new("fbneo-wasm.data", 8_273_551,
                "315a25e0bcd61d58ee0d9e8b1dbf3740b9e0ca4b7d0726f848ce1068de73437c", "application/octet-stream"),
            new("fbneo-legacy-wasm.data", 8_272_960,
                "9dbb6242c028f4179549f324688b654353881beb552292f939bc6171a0828b5f", "application/octet-stream"),
            new("fbneo.json", 119,
                "d2c5e07c2afc2b53937f14b721d7d025ad599d111e9627fc77e9b3fd17450e70", "application/json"),
        ]),
    ]);

    /// <summary>The core with exactly this id (ordinal), or null.</summary>
    public OperatorCore? Find(string id) => Cores.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
}
