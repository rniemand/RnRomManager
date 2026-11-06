using System.Diagnostics;
using System.Net;
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
    [TestCase(SeedSet.Basic, 8, 6)]
    [TestCase(SeedSet.Full, 208, 206)]
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
            var expected = outcome switch { IngestOutcome.Ingested => ingested, IngestOutcome.Duplicate or IngestOutcome.Review or IngestOutcome.Failed => 1, _ => 0 };
            Assert.That(summary.RootElement.GetProperty(outcome).GetInt32(), Is.EqualTo(expected), outcome);
        }
        var items = await factory.Services.GetRequiredService<IJobItemRepository>().ListAsync(id, null, 1, 200, default);
        Assert.That(items.Total, Is.EqualTo(ingested + 3));
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
        var review = await factory.Services.GetRequiredService<IIngestReviewRepository>().ListAsync("Pending", 1, 48, default);
        Assert.That(review.Total, Is.EqualTo(1));
        Assert.That(review.Items.Single().Reason, Is.EqualTo("NoDatMatch"));
        Assert.That(review.Items.Single().Suggestions, Does.Contain("Orbit Blocks"));
        Assert.That(File.Exists(Path.Combine(factory.Root, "ingest", "gb", "_duplicates", "90-duplicate.gb")), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(factory.Root, "ingest", "gb", "_failed", "92-invalid.gb.error.txt")), Does.Contain("InvalidHeader"));
        Assert.That(Directory.GetFiles(Path.Combine(factory.Root, "ingest", "gb")), Has.Length.EqualTo(1));
    }
}
