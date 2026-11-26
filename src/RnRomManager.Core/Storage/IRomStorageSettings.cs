using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Storage;

/// <summary>ROM Zip Config B2: the Admin's ROM storage choice per <see cref="ISystemHandler.RomStorageConfigurable"/> system.</summary>
public interface IRomStorageSettings
{
    /// <returns>The stored value, or null when none is stored (the handler default applies).</returns>
    bool? Override(string systemId);

    /// <summary>(Re)reads the keys of the configurable systems from <c>app_settings</c>, replacing every value held.</summary>
    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Writes the database first, then the in-memory value. Only called under the job lock (B7).</summary>
    Task SetAsync(string systemId, bool embed, CancellationToken cancellationToken);

    /// <summary>Z3: the system's ZIP compression level; <see cref="ZipCompression.Med"/> when none is stored.</summary>
    ZipCompression Compression(string systemId);

    /// <summary>Z3: writes the database first, then the in-memory value. Only called under the job lock.</summary>
    Task SetCompressionAsync(string systemId, ZipCompression level, CancellationToken cancellationToken);
}
