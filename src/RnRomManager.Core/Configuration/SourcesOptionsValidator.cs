using Microsoft.Extensions.Options;

namespace RnRomManager.Core.Configuration;

public sealed class SourcesOptionsValidator : IValidateOptions<SourcesOptions>
{
    public ValidateOptionsResult Validate(string? name, SourcesOptions options)
    {
        var failures = new List<string>();
        if (!IsHttpUrl(options.DatBaseUrl))
            failures.Add($"Sources:DatBaseUrl must be an absolute http(s) URL (got '{options.DatBaseUrl}').");
        if (options.ThumbnailBaseUrl.Length > 0 && !IsHttpUrl(options.ThumbnailBaseUrl))
            failures.Add($"Sources:ThumbnailBaseUrl must be an absolute http(s) URL or empty (got '{options.ThumbnailBaseUrl}').");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
