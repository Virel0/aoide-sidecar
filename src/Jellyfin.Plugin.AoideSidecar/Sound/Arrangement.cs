namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// The kinds of section that can be told apart without a model of what music means.
/// </summary>
/// <remarks>
/// <c>verse</c> and <c>chorus</c> are in the agreed set and are never returned. Telling a
/// verse from a chorus is a judgement about song form, not a property of the signal — the
/// two are frequently identical in level, spectrum and density, and separating them needs
/// a model trained on what people call things. The kinds below are all measurable: an
/// intro is the quiet start, a drop is the loudest thing with the most bottom, a breakdown
/// is the quiet stretch between two loud ones. Everything else is honestly
/// <see cref="Unknown"/>.
/// </remarks>
public static class SectionKind
{
    /// <summary>The quiet opening.</summary>
    public const string Intro = "intro";

    /// <summary>Energy rising into the section that follows.</summary>
    public const string Build = "build";

    /// <summary>The loudest, heaviest part of the track.</summary>
    public const string Drop = "drop";

    /// <summary>A quiet stretch between two loud ones.</summary>
    public const string Breakdown = "breakdown";

    /// <summary>The quiet ending.</summary>
    public const string Outro = "outro";

    /// <summary>Measured, and nothing about it is distinctive enough to name.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// One stretch of a track that is made of the same thing throughout.
/// </summary>
/// <param name="StartMs">Where it begins. On a phrase boundary where one was found, on a downbeat otherwise.</param>
/// <param name="EndMs">Where it ends.</param>
/// <param name="Kind">One of <see cref="SectionKind"/>.</param>
/// <param name="Energy">Zero to one, normalised within the track: a quiet record's loudest part still reads as one.</param>
public sealed record Section(double StartMs, double EndMs, string Kind, double Energy);

/// <summary>
/// A stretch where somebody is probably singing.
/// </summary>
/// <param name="StartMs">Where it begins.</param>
/// <param name="EndMs">Where it ends.</param>
public sealed record VocalSpan(double StartMs, double EndMs);

/// <summary>
/// What a track is made of and in what order, for deciding where a mix may run.
/// </summary>
/// <param name="Sections">The track's parts, contiguous and covering all of it.</param>
/// <param name="PhraseBars">Bars to a phrase — 8, 16 or 32 — or null where the structure would not commit.</param>
/// <param name="PhraseAnchorMs">A downbeat that begins a phrase, or null with the above.</param>
/// <param name="Vocals">
/// Where singing was found. An empty list means none was found; <b>null means it could not
/// be told</b>, which is not the same thing and must not be read as an instrumental.
/// </param>
public sealed record Arrangement(
    IReadOnlyList<Section> Sections,
    int? PhraseBars,
    double? PhraseAnchorMs,
    IReadOnlyList<VocalSpan>? Vocals);
