using System.Globalization;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// Turns client-supplied ids into audio files the caller is allowed to see.
/// </summary>
internal static class AudioItems
{
    /// <summary>
    /// Resolves one id to a visible audio file, or nothing.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="id">The client's id.</param>
    /// <param name="user">The requesting user, for the visibility check.</param>
    /// <returns>The canonical id and the file's path, or null.</returns>
    /// <remarks>
    /// Nothing here may throw. An unknown id is omitted from the answer, not an error —
    /// and Jellyfin's <c>GetItemById</c> throws on an empty GUID, so without the guard one
    /// malformed id in a request of two hundred would fail all of them.
    /// </remarks>
    public static (string Id, string Path)? Resolve(ILibraryManager libraryManager, string id, object? user)
    {
        if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty)
        {
            return null;
        }

        try
        {
            if (libraryManager.GetItemById(guid) is not Audio audio || string.IsNullOrEmpty(audio.Path))
            {
                return null;
            }

            if (user is Jellyfin.Database.Implementations.Entities.User jellyfinUser && !audio.IsVisible(jellyfinUser))
            {
                return null;
            }

            return (guid.ToString("N", CultureInfo.InvariantCulture), audio.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
