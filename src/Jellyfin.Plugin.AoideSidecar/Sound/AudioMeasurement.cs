namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Everything one decode of a file produces.
/// </summary>
/// <param name="Bounds">Where the sound starts and stops, or null for nothing worth trimming.</param>
/// <param name="Loudness">Integrated loudness and true peak, or null when there was too little to measure.</param>
/// <param name="Tempo">Tempo and confidence, or null when the track has no usable one.</param>
/// <param name="Grid">Where the beats fall, or null when there is no grid worth having.</param>
/// <param name="Key">The key, or null when there was not enough pitched material to judge.</param>
/// <param name="Arrangement">What the track is made of and in what order, or null.</param>
/// <remarks>
/// These travel together because they are taken together. Decoding is the expensive
/// part — everything after it is arithmetic over samples already in memory — so a file
/// asked about for any one of them is measured for all of them, and the caches for each
/// are filled at once.
/// </remarks>
public sealed record AudioMeasurement(
    SoundBounds? Bounds,
    Loudness? Loudness,
    Tempo? Tempo,
    BeatGrid? Grid = null,
    MusicalKey? Key = null,
    Arrangement? Arrangement = null);
