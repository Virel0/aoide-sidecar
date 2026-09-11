using System.Globalization;

namespace Jellyfin.Plugin.AoideSidecar.Data;

/// <summary>
/// One spelling for a Jellyfin id, whichever way a client wrote it.
/// </summary>
internal static class AudioIds
{
    /// <summary>
    /// Lowercase, no dashes — the form the sidecar's caches are keyed by. Anything that is
    /// not a GUID is lowercased and left alone.
    /// </summary>
    /// <param name="id">The id as sent.</param>
    /// <returns>The normalised id.</returns>
    public static string Normalise(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Guid.TryParse(id, out var guid)
            ? guid.ToString("N", CultureInfo.InvariantCulture)
            : id.Trim().ToLowerInvariant();
    }
}
