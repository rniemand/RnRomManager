using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// B8, B10: a save upload read with <see cref="MultipartReader"/> (never <c>ReadFormAsync</c>, so the global form limit for
/// images stays as it is). The text parts come first, each at most 4 KiB; the <c>file</c> part is the last one and is
/// streamed to <c>cache/work/saves/</c>, counted and hashed, only after the caller's cheap checks passed.
/// </summary>
public sealed class SaveUploadReader : IDisposable
{
    public const long RequestLimit = GameSaves.MaxSaveBytes + 64 * 1024;
    private const int MaxFieldBytes = 4096;

    private readonly MultipartReader _reader;
    private MultipartSection? _file;
    private string? _tempPath;

    private SaveUploadReader(MultipartReader reader) => _reader = reader;

    public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

    public static IResult TooLarge(HttpContext ctx) =>
        Problems.Create(ctx, StatusCodes.Status413PayloadTooLarge, "PayloadTooLarge", "The save is larger than 256 MiB.");

    private static IResult Invalid(HttpContext ctx, string field, string error) =>
        Problems.Validation(ctx, field, error);

    /// <summary>
    /// Checks <c>Content-Length</c> first (TestServer doesn't enforce the endpoint's size metadata), then reads the text
    /// parts up to the <c>file</c> part. Only <paramref name="allowed"/> names may come before it, each once.
    /// </summary>
    public static async Task<(SaveUploadReader? Reader, IResult? Problem)> ReadFieldsAsync(HttpContext ctx, IReadOnlySet<string> allowed)
    {
        if (ctx.Request.ContentLength > RequestLimit)
            return (null, TooLarge(ctx));
        if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var type) || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 } boundary)
            return (null, Invalid(ctx, "file", "Send the save as multipart/form-data."));

        var upload = new SaveUploadReader(new MultipartReader(boundary, ctx.Request.Body));
        try
        {
            while (await upload._reader.ReadNextSectionAsync(ctx.RequestAborted) is { } section)
            {
                var name = Name(section);
                if (name == "file")
                {
                    upload._file = section;
                    return (upload, null);
                }
                if (name is null || !allowed.Contains(name) || upload.Fields.ContainsKey(name))
                    return (null, Invalid(ctx, name ?? "file", "Unexpected or repeated part; send the fields once, before the file."));
                using var bounded = new MemoryStream();
                var buffer = new byte[MaxFieldBytes + 1];
                int read;
                while ((read = await section.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                {
                    bounded.Write(buffer, 0, read);
                    if (bounded.Length > MaxFieldBytes)
                        return (null, Invalid(ctx, name, "Must be at most 4 KiB."));
                }
                upload.Fields[name] = Encoding.UTF8.GetString(bounded.ToArray());
            }
            return (upload, null);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, TooLarge(ctx));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return (null, Invalid(ctx, "file", "The multipart body can't be read."));
        }
    }

    /// <summary>
    /// Streams the <c>file</c> part to a temp file (deleted on <see cref="Dispose"/>): over 256 MiB is <c>413</c>, then a
    /// missing, second or empty file (or any part after it) is <c>400</c>.
    /// </summary>
    public async Task<(SaveUpload? Upload, IResult? Problem)> ReadFileAsync(HttpContext ctx, IOptions<PathsOptions> paths)
    {
        if (_file is null)
            return (null, Invalid(ctx, "file", "Choose a save file."));
        var folder = Path.Combine(paths.Value.Cache, CrashRecovery.WorkFolder, "saves");
        Directory.CreateDirectory(folder);
        _tempPath = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long size = 0;
        try
        {
            await using (var output = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await _file.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                {
                    size += read;
                    if (size > GameSaves.MaxSaveBytes)
                        return (null, TooLarge(ctx));
                    sha1.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                }
            }
            if (await _reader.ReadNextSectionAsync(ctx.RequestAborted) is { } extra)
                return (null, Invalid(ctx, Name(extra) == "file" ? "file" : Name(extra) ?? "file", "Send one file, as the last part."));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, TooLarge(ctx));
        }
        catch (InvalidDataException)
        {
            return (null, Invalid(ctx, "file", "The multipart body can't be read."));
        }
        if (size == 0)
            return (null, Invalid(ctx, "file", "The save file is empty."));
        var fileName = ContentDispositionHeaderValue.TryParse(_file.ContentDisposition, out var disposition)
            ? HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value
            : null;
        return (new SaveUpload(_tempPath, GameSaves.ExtensionOf(fileName), size, Convert.ToHexStringLower(sha1.GetHashAndReset())), null);
    }

    /// <summary>The part's form field name (a file part included), or null when it isn't a <c>form-data</c> part.</summary>
    private static string? Name(MultipartSection section) =>
        ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
        && disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
            ? HeaderUtilities.RemoveQuotes(disposition.Name).Value
            : null;

    /// <summary>The temp file goes on every path.</summary>
    public void Dispose()
    {
        if (_tempPath is not null)
            File.Delete(_tempPath);
    }
}
