namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Everything one decode of a file produces.
/// </summary>
/// <param name="Bounds">Where the sound starts and stops, or null for nothing worth trimming.</param>
/// <param name="Loudness">Integrated loudness and true peak, or null when there was too little to measure.</param>
/// <param name="Tempo">Tempo and confidence, or null when the track has no usable one.</param>
/// <remarks>
/// These travel together because they are taken together. Decoding is the expensive
/// part — everything after it is arithmetic over samples already in memory — so a file
/// asked about for any one of them is measured for all three, and the caches for each
/// are filled at once.
/// </remarks>
public sealed record AudioMeasurement(SoundBounds? Bounds, Loudness? Loudness, Tempo? Tempo);
