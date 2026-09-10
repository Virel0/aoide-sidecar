using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// How loud a track is, by EBU R128.
/// </summary>
/// <param name="LoudnessLufs">Integrated loudness over the whole track, in LUFS.</param>
/// <param name="TruePeakDbfs">True peak, in dBFS. Above zero on a track clipped by its own master.</param>
public sealed record Loudness(double LoudnessLufs, double TruePeakDbfs);

/// <summary>
/// Reads the measurement ffmpeg's <c>loudnorm</c> filter prints.
/// </summary>
/// <remarks>
/// <para>
/// The contract names <c>ffmpeg -af loudnorm=print_format=json</c> as the reference
/// implementation, so the numbers come from that filter rather than from a second R128
/// implementation that would agree with it only approximately. <c>input_i</c> is the
/// integrated loudness and <c>input_tp</c> the true peak; the rest of what the filter
/// prints describes a normalisation pass the sidecar never runs.
/// </para>
/// <para>
/// The filter prints to stderr at info level, mixed in with everything else ffmpeg says,
/// so the block is located by the one field that identifies it rather than by parsing
/// the log.
/// </para>
/// </remarks>
public static class LoudnessAnalyzer
{
    /// <summary>
    /// Quieter than this is not a measurement. <c>loudnorm</c> reports −70 LUFS as its
    /// floor and <c>-inf</c> for digital silence.
    /// </summary>
    public const double SilenceFloorLufs = -70;

    /// <summary>
    /// Shorter than this and <c>loudnorm</c>'s single pass has not seen enough audio for
    /// its gating to mean anything.
    /// </summary>
    public const double MinimumSeconds = 3;

    /// <summary>
    /// Pulls the loudness out of ffmpeg's stderr.
    /// </summary>
    /// <param name="log">Everything ffmpeg wrote to stderr.</param>
    /// <returns>The measurement, or null when the filter printed nothing usable.</returns>
    public static Loudness? Parse(string log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var marker = log.LastIndexOf("\"input_i\"", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var open = log.LastIndexOf('{', marker);
        var close = log.IndexOf('}', marker);
        if (open < 0 || close < 0)
        {
            return null;
        }

        double integrated;
        double truePeak;
        try
        {
            using var document = JsonDocument.Parse(log.AsSpan(open, close - open + 1).ToString());
            if (!TryNumber(document.RootElement, "input_i", out integrated)
                || !TryNumber(document.RootElement, "input_tp", out truePeak))
            {
                return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        // "-inf", "-70.0" and anything below the floor all mean the same thing: there was
        // not enough signal to measure, and a client must leave the track alone.
        return integrated <= SilenceFloorLufs ? null : new Loudness(integrated, truePeak);
    }

    /// <summary>
    /// Reads one of loudnorm's fields, which it prints as quoted decimal strings.
    /// </summary>
    private static bool TryNumber(JsonElement root, string name, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value) && double.IsFinite(value),
            _ => false
        };
    }
}
