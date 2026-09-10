namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// One stretch of a track over which a single fixed grid fits the beats.
/// </summary>
/// <param name="StartMs">Where the segment begins in the track.</param>
/// <param name="EndMs">Where it ends.</param>
/// <param name="AnchorMs">The fitted position of beat zero of this segment.</param>
/// <param name="Bpm">The fitted tempo. <c>beat(n) = AnchorMs + n * 60000 / Bpm</c>.</param>
/// <param name="ResidualMs">RMS distance between the fitted beats and the beats actually detected.</param>
/// <param name="Beats">How many beats the fit spans.</param>
/// <remarks>
/// A fit rather than a list of timestamps. The same information at a tenth of the size,
/// and it says something a list cannot: <see cref="ResidualMs"/> is how well the track
/// holds a grid at all, which is the question anything that wants to lock to it must ask
/// first.
/// </remarks>
public sealed record BeatSegment(
    double StartMs,
    double EndMs,
    double AnchorMs,
    double Bpm,
    double ResidualMs,
    int Beats);

/// <summary>
/// Everything a mixed transition needs to know about where a track's beats fall.
/// </summary>
/// <param name="Segments">One fit, or several where the track genuinely changes tempo.</param>
/// <param name="BeatsPerBar">Four for nearly everything, three for a waltz, null when the meter could not be established.</param>
/// <param name="DownbeatIndex">Which beat of the first segment's fit begins a bar, or null with the meter.</param>
/// <param name="MixInMs">The first downbeat of the first bar that is actually playing, past any intro that is only atmosphere.</param>
/// <param name="MixOutMs">The downbeat of the last bar before the ending stops being part of the groove.</param>
public sealed record BeatGrid(
    IReadOnlyList<BeatSegment> Segments,
    int? BeatsPerBar,
    int? DownbeatIndex,
    double? MixInMs,
    double? MixOutMs);
