namespace RnRomManager.Core.Systems;

/// <summary>The registered <see cref="ISystemHandler"/>s, looked up the ways Core needs them.</summary>
public interface ISystemRegistry
{
    /// <summary>In registration order.</summary>
    IReadOnlyList<ISystemHandler> All { get; }

    /// <summary>Case-sensitive: ids are lower-case folder names.</summary>
    ISystemHandler? ById(string id);

    IReadOnlyList<ISystemHandler> ByFamily(string family);

    /// <summary>Handlers claiming <paramref name="extension"/> (with the dot, any case).</summary>
    IReadOnlyList<ISystemHandler> ByExtension(string extension);
}
