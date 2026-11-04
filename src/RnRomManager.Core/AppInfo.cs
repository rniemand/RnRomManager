using System.Reflection;

namespace RnRomManager.Core;

/// <summary>The build's version (<c>info.json</c> <c>metadata.appVersion</c>, logs, User-Agent).</summary>
public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
}
