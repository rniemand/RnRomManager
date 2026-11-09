using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>M5 regression: built-in seeds → startup DAT loading → authenticated ingest API → library.</summary>
[Category("Integration")]
public class SeedIngestTests
{
    [TestCase(SeedSet.Basic, 13, 11)]
    [TestCase(SeedSet.Full, 213, 211)]
    public async Task SeededSet_IngestsWithExpectedOutcomesAndLibrary(SeedSet set, int ingested, int games)
    {
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        IngestSeeder.Seed(Path.Combine(factory.Root, "ingest"), Path.Combine(factory.Root, "cache"), set, TimeProvider.System);
        using var admin = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "seed-admin", TestUsers.AllRoles);
        (await admin.LoginAsync("seed-admin", TestUsers.Password)).Dispose();
        using var response = await admin.PostAsync("/api/jobs/ingest");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var id = (await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var elapsed = Stopwatch.StartNew();
        Job job;
        do
        {
            job = (await jobs.GetAsync(id, default))!;
            if (job.Status != JobStatus.Running) break;
            await Task.Delay(50);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(90));
        Assert.That(job.Status, Is.EqualTo(JobStatus.Completed), job.Error);
        using var summary = JsonDocument.Parse(job.Summary!);
        foreach (var outcome in IngestOutcome.All)
        {
            var expected = outcome switch { IngestOutcome.Ingested => ingested, IngestOutcome.Review => 2, IngestOutcome.Duplicate or IngestOutcome.Failed => 1, _ => 0 };
            Assert.That(summary.RootElement.GetProperty(outcome).GetInt32(), Is.EqualTo(expected), outcome);
        }
        var items = await factory.Services.GetRequiredService<IJobItemRepository>().ListAsync(id, null, 1, 200, default);
        Assert.That(items.Total, Is.EqualTo(ingested + 4));
        var repository = factory.Services.GetRequiredService<IGameRepository>();
        var library = await repository.ListSyncStateAsync(default);
        Assert.That(library, Has.Count.EqualTo(games));
        var infos = Directory.GetFiles(Path.Combine(factory.Root, "roms"), "*.zip", SearchOption.AllDirectories).Select(path =>
        {
            using var zip = LibraryZip.Open(path, factory.Services.GetRequiredService<InfoJsonReader>());
            return zip.InfoJson!.Game!;
        }).ToList();
        Assert.That(infos.Sum(g => g.Roms.Count), Is.EqualTo(ingested));
        Assert.That(infos.Single(g => g.Title == "Quest, The").DisplayTitle, Is.EqualTo("The Quest"));
        var orbit = infos.Single(g => g.Title == "Orbit Blocks");
        Assert.That(orbit.Roms, Has.Count.EqualTo(3));
        Assert.That(orbit.Roms.Single(r => r.IsPrimary).Revision, Is.EqualTo("Rev 1"));
        Assert.That(infos.Single(g => g.Title == "Color Garden").System, Is.EqualTo("gbc"));
        Assert.That(infos.All(g => g.Images.Single().IsDefault), Is.True);
        var courier = Directory.GetFiles(Path.Combine(factory.Root, "roms", "gba", "S"), "*.zip").Single();
        using (var zip = LibraryZip.Open(courier, factory.Services.GetRequiredService<InfoJsonReader>()))
        {
            var game = zip.InfoJson!.Game!;
            Assert.That((game.Title, game.System), Is.EqualTo(("Star Courier", "gba")));
            Assert.That(game.Roms.Single().Header!["gameCode"]!.GetValue<string>(), Is.EqualTo("ASCE"));
        }
        var kart = Directory.GetFiles(Path.Combine(factory.Root, "roms", "n64", "O"), "*.zip").Single();
        using (var zip = LibraryZip.Open(kart, factory.Services.GetRequiredService<InfoJsonReader>()))
        {
            var game = zip.InfoJson!.Game!;
            var rom = game.Roms.Single();
            var z64 = new SyntheticN64RomBuilder { Seed = 6401, Title = "ORBIT KART 64", GameCode = "NOKE" }.Build();
            Assert.That((game.Title, game.System, rom.FileName), Is.EqualTo(("Orbit Kart 64", "n64", "Orbit Kart 64 (USA).z64")));
            Assert.That(rom.Header!["gameCode"]!.GetValue<string>(), Is.EqualTo("NOKE"));
            Assert.That(rom.Metadata.Sha1, Is.EqualTo(Convert.ToHexStringLower(SHA1.HashData(z64))));
        }
        var pioneer = Directory.GetFiles(Path.Combine(factory.Root, "roms", "nes", "P"), "*.zip").Single();
        Assert.That(Path.GetFileName(pioneer), Is.EqualTo("Pixel Pioneer.zip"));
        using (var zip = LibraryZip.Open(pioneer, factory.Services.GetRequiredService<InfoJsonReader>()))
        {
            var game = zip.InfoJson!.Game!;
            var rom = game.Roms.Single();
            Assert.That((game.Title, game.System, rom.FileName), Is.EqualTo(("Pixel Pioneer", "nes", "Pixel Pioneer (USA).nes")));
            Assert.That(zip.HasEntry(LibraryZip.RomEntry("Pixel Pioneer (USA).nes")), Is.True);
            Assert.That((rom.Header!["mapper"]!.GetValue<int>(), rom.Header["battery"]!.GetValue<bool>()), Is.EqualTo((1, true)));
            Assert.That((game.ReleaseYear, game.Genres), Is.EqualTo(((int?)1987, new[] { "Platform" })));
        }
        var rally = Directory.GetFiles(Path.Combine(factory.Root, "roms", "snes", "M"), "*.zip").Single();
        using (var zip = LibraryZip.Open(rally, factory.Services.GetRequiredService<InfoJsonReader>()))
        {
            var game = zip.InfoJson!.Game!;
            var rom = game.Roms.Single();
            var sfc = new SyntheticSnesRomBuilder { Seed = 1101, Mapping = SnesMapping.LoROM, Title = "MODE SEVEN RALLY", SramSizeByte = 3 }.Build();
            Assert.That((game.Title, game.System, rom.FileName, rom.OriginalFileName),
                Is.EqualTo(("Mode Seven Rally", "snes", "Mode Seven Rally (USA).sfc", "01-mode-seven-rally.smc")));
            using (var entry = zip.OpenEntry(LibraryZip.RomEntry("Mode Seven Rally (USA).sfc"), sfc.Length))
            using (var bytes = new MemoryStream())
            {
                entry.CopyTo(bytes);
                Assert.That(bytes.ToArray(), Is.EqualTo(sfc), "stored without the copier header");
            }
            Assert.That((rom.Header!["mapping"]!.GetValue<string>(), rom.Header["title"]!.GetValue<string>(), rom.Header["sramSizeKiB"]!.GetValue<int>()),
                Is.EqualTo(("LoROM", "MODE SEVEN RALLY", 8)));
            Assert.That((game.ReleaseYear, game.Genres), Is.EqualTo(((int?)1992, new[] { "Racing" })));
        }
        var lanes = Directory.GetFiles(Path.Combine(factory.Root, "roms", "a2600", "L"), "*.zip").Single();
        using (var zip = LibraryZip.Open(lanes, factory.Services.GetRequiredService<InfoJsonReader>()))
        {
            var game = zip.InfoJson!.Game!;
            var rom = game.Roms.Single();
            var seeded = new byte[4096];
            new Random(1201).NextBytes(seeded);
            Assert.That((game.Title, game.System, rom.FileName, rom.OriginalFileName),
                Is.EqualTo(("Laser Lanes", "a2600", "Laser Lanes (USA).a26", "01-laser-lanes.bin")));
            using (var entry = zip.OpenEntry(LibraryZip.RomEntry("Laser Lanes (USA).a26"), seeded.Length))
            using (var bytes = new MemoryStream())
            {
                entry.CopyTo(bytes);
                Assert.That(bytes.ToArray(), Is.EqualTo(seeded));
            }
            Assert.That(rom.Header, Is.Null);
            Assert.That((game.ReleaseYear, game.Genres), Is.EqualTo(((int?)1982, new[] { "Shooter" })));
        }
        var pursuit = Directory.GetFiles(Path.Combine(factory.Root, "roms", "coleco", "P"), "*.zip").Single();
        using (var zip = LibraryZip.Open(pursuit, factory.Services.GetRequiredService<InfoJsonReader>()))
        {
            var game = zip.InfoJson!.Game!;
            var rom = game.Roms.Single();
            var seeded = new byte[8192];
            new Random(1301).NextBytes(seeded);
            (seeded[0], seeded[1]) = (0xAA, 0x55);
            Assert.That((game.Title, game.System, rom.FileName, rom.OriginalFileName),
                Is.EqualTo(("Pixel Pursuit", "coleco", "Pixel Pursuit (USA).col", "01-pixel-pursuit.rom")));
            using (var entry = zip.OpenEntry(LibraryZip.RomEntry("Pixel Pursuit (USA).col"), seeded.Length))
            using (var bytes = new MemoryStream())
            {
                entry.CopyTo(bytes);
                Assert.That(bytes.ToArray(), Is.EqualTo(seeded));
            }
            Assert.That(rom.Header!["magic"]!.GetValue<string>(), Is.EqualTo("AA55"));
            Assert.That((game.ReleaseYear, game.Genres), Is.EqualTo(((int?)1983, new[] { "Action" })));
        }
        var review = await factory.Services.GetRequiredService<IIngestReviewRepository>().ListAsync("Pending", 1, 48, default);
        Assert.That(review.Total, Is.EqualTo(2));
        Assert.That(review.Items.Select(i => i.Reason), Is.All.EqualTo("NoDatMatch"));
        Assert.That(review.Items.Single(i => i.SourcePath.EndsWith("91-unmatched.gb", StringComparison.Ordinal)).Suggestions, Does.Contain("Orbit Blocks"));
        Assert.That(File.Exists(Path.Combine(factory.Root, "ingest", "gb", "_duplicates", "90-duplicate.gb")), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(factory.Root, "ingest", "gb", "_failed", "92-invalid.gb.error.txt")), Does.Contain("InvalidHeader"));
        Assert.That(Directory.GetFiles(Path.Combine(factory.Root, "ingest", "gb")), Has.Length.EqualTo(2));
    }
}
