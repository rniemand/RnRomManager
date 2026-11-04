using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Jobs;

/// <summary>Spec §7.4 crash recovery (startup step 7).</summary>
public class CrashRecoveryTests
{
    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private IJobRepository _jobs = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Roms = _dir.Combine("roms"), Cache = _dir.Combine("cache") };
        Directory.CreateDirectory(_paths.Roms);
        _jobs = Substitute.For<IJobRepository>();
        _jobs.AbortRunningAsync(default, default).ReturnsForAnyArgs([Guid.NewGuid()]);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private Task RunAsync() =>
        new CrashRecovery(_jobs, Options.Create(_paths), TimeProvider.System, NullLogger<CrashRecovery>.Instance).RunAsync(CancellationToken.None);

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Test]
    public async Task AbortsRunningJobs()
    {
        await RunAsync();

        await _jobs.Received(1).AbortRunningAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EmptiesCacheWork_KeepsTheFolderAndTheRestOfTheCache()
    {
        var work = Path.Combine(_paths.Cache, CrashRecovery.WorkFolder);
        Touch(Path.Combine(work, Guid.NewGuid().ToString(), "nested", "a.gb"));
        Touch(Path.Combine(work, "stray.bin"));
        var dat = Touch(Path.Combine(_paths.Cache, "dats", "dats.json"));

        await RunAsync();

        Assert.That(Directory.Exists(work), Is.True);
        Assert.That(Directory.EnumerateFileSystemEntries(work), Is.Empty);
        Assert.That(File.Exists(dat), Is.True);
    }

    [Test]
    public async Task DeletesEveryZipTmpUnderRoms_KeepsZips()
    {
        var tmp1 = Touch(Path.Combine(_paths.Roms, "gb", "T", "Tetris.zip.tmp"));
        var tmp2 = Touch(Path.Combine(_paths.Roms, "gbc", "#", "1942.zip.tmp"));
        var zip = Touch(Path.Combine(_paths.Roms, "gb", "T", "Tetris.zip"));
        var other = Touch(Path.Combine(_paths.Roms, "gb", "T", "notes.tmp"));

        await RunAsync();

        Assert.That(File.Exists(tmp1) || File.Exists(tmp2), Is.False);
        Assert.That(File.Exists(zip) && File.Exists(other), Is.True);
    }

    [Test]
    public Task NoWorkFolder_IsFine() => RunAsync();
}
