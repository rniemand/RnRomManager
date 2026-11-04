using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RnRomManager.Core.Systems;

public static class SystemHandlerRegistration
{
    /// <summary>Registers a handler; the first call also registers <see cref="ISystemRegistry"/> (spec §22).</summary>
    public static IServiceCollection AddSystemHandler<T>(this IServiceCollection services)
        where T : class, ISystemHandler
    {
        services.AddSingleton<ISystemHandler, T>();
        services.TryAddSingleton<ISystemRegistry, SystemRegistry>();
        return services;
    }
}
