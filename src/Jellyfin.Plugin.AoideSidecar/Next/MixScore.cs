using Jellyfin.Plugin.AoideSidecar.Sound;

namespace Jellyfin.Plugin.AoideSidecar.Next;

/// <summary>
/// How well two records suit each other, as one number and the five it is made of. A port
/// of the clients' <c>MixScore</c>, held to their parity table.
/// </summary>
/// <param name="Tempo">1 up to a two per cent bend, falling to 0 at the six per cent limit.</param>
/// <param name="Key">1 for the same key, a neighbour or the relative; 0 for a clash; 0.5 when either is unknown.</param>
/// <param name="Vocals">1 when neither sings across the overlap, 0.6 when one does, 0.5 when nothing is known.</param>
/// <param name="Energy">1 when the exit and the entry are at the same energy, falling to 0 as they diverge. 0.5 unknown.</param>
/// <param name="Sections">How well the two sections suit a hand-over. 0.5 unknown.</param>
internal sealed record MixScore(double Tempo, double Key, double Vocals, double Energy, double Sections)
{
    private const double TempoWeight = 0.2;
    private const double KeyWeight = 0.2;
    private const double VocalsWeight = 0.15;
    private const double EnergyWeight = 0.2;
    private const double SectionsWeight = 0.25;

    /// <summary>Where a bend stops being free.</summary>
    private const double EasyBend = 0.02;

    /// <summary>Gets the weighted total, in the clients' order of operations.</summary>
    public double Total =>
        (Tempo * TempoWeight)
        + (Key * KeyWeight)
        + (Vocals * VocalsWeight)
        + (Energy * EnergyWeight)
        + (Sections * SectionsWeight);

    /// <summary>
    /// Gets how long the pair should run together, from the score alone, or null for a
    /// pair that earns nothing.
    /// </summary>
    public int? Bars
    {
        get
        {
            var total = Total;
            if (total >= 0.75)
            {
                return 32;
            }

            if (total >= 0.55)
            {
                return 16;
            }

            if (total >= 0.35)
            {
                return 8;
            }

            return null;
        }
    }

    /// <summary>The tempo factor for a playback rate.</summary>
    /// <param name="rate">The incoming deck's rate.</param>
    /// <returns>1 to 0.</returns>
    public static double TempoFactor(double rate)
    {
        var bend = Math.Abs(rate - 1);
        if (bend <= EasyBend + 1e-9)
        {
            return 1;
        }

        return Math.Max(0, 1 - ((bend - EasyBend) / (DJPlanner.MaximumStretch - EasyBend)));
    }

    /// <summary>The key factor for two Camelot keys.</summary>
    /// <param name="a">One key.</param>
    /// <param name="b">The other.</param>
    /// <returns>1, 0.5 or 0.</returns>
    public static double KeyFactor(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return 0.5;
        }

        return CamelotKey.AreCompatible(a, b) ? 1 : 0;
    }

    /// <summary>The vocals factor.</summary>
    /// <param name="outgoingSings">Whether the outgoing record sings across the overlap, or null when unread.</param>
    /// <param name="incomingSings">The same for the incoming record.</param>
    /// <returns>1, 0.6 or 0.5.</returns>
    public static double VocalsFactor(bool? outgoingSings, bool? incomingSings)
    {
        if (outgoingSings is null || incomingSings is null)
        {
            return 0.5;
        }

        return outgoingSings == false && incomingSings == false ? 1 : 0.6;
    }

    /// <summary>The energy factor.</summary>
    /// <param name="exit">The outgoing record's energy at its exit.</param>
    /// <param name="entry">The incoming record's energy at its entry.</param>
    /// <returns>1 to 0, or 0.5 when either is unknown.</returns>
    public static double EnergyFactor(double? exit, double? entry)
    {
        if (exit is not { } from || entry is not { } to)
        {
            return 0.5;
        }

        return Math.Max(0, 1 - Math.Abs(from - to));
    }

    /// <summary>The sections factor.</summary>
    /// <param name="exit">The kind of section the outgoing record leaves from.</param>
    /// <param name="entry">The kind the incoming record comes in on.</param>
    /// <returns>The mean of how well each end suits a hand-over, or 0.5 when either is unknown.</returns>
    public static double SectionsFactor(string? exit, string? entry)
    {
        if (exit is null || entry is null)
        {
            return 0.5;
        }

        var leaving = exit switch
        {
            SectionKind.Outro or SectionKind.Breakdown => 1.0,
            SectionKind.Build or "verse" or "chorus" or SectionKind.Intro => 0.7,
            SectionKind.Unknown => 0.6,
            SectionKind.Drop => 0.4,
            _ => 0.6
        };

        var arriving = entry switch
        {
            SectionKind.Build or SectionKind.Intro or SectionKind.Breakdown => 1.0,
            "verse" or "chorus" or SectionKind.Outro => 0.7,
            SectionKind.Unknown => 0.6,
            SectionKind.Drop => 0.2,
            _ => 0.6
        };

        return (leaving + arriving) / 2;
    }
}
