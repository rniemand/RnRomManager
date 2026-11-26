using System.IO.Compression;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>A6 tests: a game whose ZIP has <c>actions.coverImagePulled</c> and whose <c>game_actions</c> row has an IGDB rating.</summary>
internal static class GameActionsSeed
{
    public const string Pulled = "2026-11-26T10:15:30.0000000+00:00";

    public static async Task SeedAsync(ApiFactory factory, Guid gameId)
    {
        var row = (await factory.Services.GetRequiredService<IGameRepository>().GetAsync(gameId, default))!;
        var path = Path.Combine(factory.Root, "roms", row.ZipPath);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry(LibraryZip.InfoJsonEntry)!;
            JsonObject json;
            using (var reader = new StreamReader(entry.Open()))
                json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            json["actions"] = new JsonObject { ["coverImagePulled"] = Pulled };
            entry.Delete();
            await using var writer = new StreamWriter(zip.CreateEntry(LibraryZip.InfoJsonEntry).Open());
            await writer.WriteAsync(json.ToJsonString());
        }
        InfoJsonGame game;
        using (var zip = LibraryZip.Open(path, factory.Services.GetRequiredService<InfoJsonReader>()))
            game = zip.InfoJson!.Game!;
        var file = new FileInfo(path);
        await factory.Services.GetRequiredService<ILibraryMirror>().ReplaceAsync(
            new MirrorEntry(game, row.ZipPath, file.Length, file.LastWriteTimeUtc), default);
        await using var db = factory.Services.GetRequiredService<IDbConnectionFactory>().Create();
        await db.ExecuteAsync(
            "UPDATE game_actions SET igdb_rating = 81.5, igdb_rating_count = 10, last_igdb_sync_at = UTC_TIMESTAMP(6) WHERE game_id = @gameId",
            new { gameId });
    }

    /// <summary>The row's cover-pulled time and rating, or null when the game has no row.</summary>
    public static async Task<(DateTime? Pulled, double? Rating)?> ReadAsync(ApiFactory factory, Guid gameId)
    {
        await using var db = factory.Services.GetRequiredService<IDbConnectionFactory>().Create();
        return await db.QuerySingleOrDefaultAsync<(DateTime?, double?)?>(
            "SELECT cover_image_pulled_at, igdb_rating FROM game_actions WHERE game_id = @gameId", new { gameId });
    }
}
