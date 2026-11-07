using Microsoft.Extensions.Options;
using RnRomManager.Core.Backups;
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
        .AddValidated<SourcesOptions, SourcesOptionsValidator>(config, SourcesOptions.Section)
        // B19: checked by the startup restore (it needs Paths:Backups and the file), not by an options validator.
        .AddBackupOptions(config);

    private static IServiceCollection AddBackupOptions(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<BackupOptions>().Bind(config.GetSection(BackupOptions.Section));
        return services;
    }

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
