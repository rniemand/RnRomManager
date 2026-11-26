namespace RnRomManager.Core.Systems;

/// <summary>The lazily loaded placeholder box art a handler embeds (<see cref="ISystemHandler"/>).</summary>
public static class BoxArt
{
    /// <summary>Loads <paramref name="name"/> from <paramref name="owner"/>'s assembly on first use.</summary>
    public static Lazy<ReadOnlyMemory<byte>> Lazy(Type owner, string name) =>
        new(() => EmbeddedResource.Load(owner.Assembly, name));
}
