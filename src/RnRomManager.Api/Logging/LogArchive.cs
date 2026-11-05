using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Configuration;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;

namespace RnRomManager.Api.Logging;

/// <summary>
/// "Download logs" (spec §13): a ZIP of every <c>*.log</c> in <c>Paths:Logs</c> plus <c>settings.json</c> (the redacted
/// effective config) and <c>version.txt</c>. Log files are opened <c>FileShare.ReadWrite</c> because NLog keeps today's
/// file open. The ZIP is built in a temp file under <c>cache/work/</c> (deleted on close), so the response can be
/// streamed without synchronous writes to it.
/// </summary>
public sealed class LogArchive(IServiceProvider services, IOptions<PathsOptions> paths)
{
    public const string SettingsEntry = "settings.json";
    public const string VersionEntry = "version.txt";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <returns>The finished ZIP, positioned at 0; disposing it deletes the temp file.</returns>
    public async Task<FileStream> BuildAsync(CancellationToken cancellationToken)
    {
        var work = Path.Combine(paths.Value.Cache, CrashRecovery.WorkFolder);
        Directory.CreateDirectory(work);
        var file = new FileStream(Path.Combine(work, $"logs-{Guid.NewGuid():N}.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                var folder = paths.Value.Logs;
                if (Directory.Exists(folder))
                {
                    foreach (var name in Directory.EnumerateFiles(folder, "*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await using var source = new FileStream(SafePath.Resolve(folder, name!), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        await using var entry = zip.CreateEntry(name!, CompressionLevel.Optimal).Open();
                        await source.CopyToAsync(entry, cancellationToken);
                    }
                }

                await using (var settings = zip.CreateEntry(SettingsEntry).Open())
                    await JsonSerializer.SerializeAsync(settings, EffectiveSettings.From(services), Json, cancellationToken);
                await using (var version = new StreamWriter(zip.CreateEntry(VersionEntry).Open()))
                    await version.WriteLineAsync(StartupRunner.Version);
            }
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }
}
