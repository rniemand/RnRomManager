using System.Reflection;

namespace RnRomManager.Core.Systems;

/// <summary>Reads a system handler's embedded file from its assembly's <c>Resources</c> folder.</summary>
public static class EmbeddedResource
{
    /// <summary>The bytes of <c>{assembly name}.Resources.{name}</c>; throws when the resource is missing.</summary>
    public static byte[] Load(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.Resources.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
