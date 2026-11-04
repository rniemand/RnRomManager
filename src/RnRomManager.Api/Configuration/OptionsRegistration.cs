using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Api.Configuration;

public static class OptionsRegistration
{
    /// <summary>Binds every spec §4 section with its validator; all are checked when the host starts.</summary>
    public static IServiceCollection AddAppOptions(this IServiceCollection services, IConfiguration config) => services
        .AddValidated<ConnectionStringsOptions, ConnectionStringsOptionsValidator>(config, ConnectionStringsOptions.Section)
        .AddValidated<PathsOptions, PathsOptionsValidator>(config, PathsOptions.Section)
        .AddValidated<AdminOptions, AdminOptionsValidator>(config, AdminOptions.Section)
        .AddValidated<IntegrityOptions, IntegrityOptionsValidator>(config, IntegrityOptions.Section)
        .AddValidated<LoggingOptions, LoggingOptionsValidator>(config, LoggingOptions.Section)
        .AddValidated<SourcesOptions, SourcesOptionsValidator>(config, SourcesOptions.Section);

    private static IServiceCollection AddValidated<TOptions, TValidator>(
        this IServiceCollection services, IConfiguration config, string section)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();
        services.AddOptions<TOptions>().Bind(config.GetSection(section)).ValidateOnStart();
        return services;
    }
}
