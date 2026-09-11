using System.Globalization;

namespace Jellyfin.Plugin.AoideSidecar.Next;

/// <summary>
/// The Camelot wheel, as far as anything here needs it. A port of the clients' <c>CamelotKey</c>.
/// </summary>
/// <remarks>
/// Only ever used to prefer one pair over another, never to refuse a pair: the estimator
/// is weakest between a key and its relative major or minor, which share all seven notes
/// and which a listener would not have objected to anyway.
/// </remarks>
internal static class CamelotKey
{
    /// <summary>
    /// A key as its number (1…12) and whether it is minor.
    /// </summary>
    /// <param name="key">Such as <c>8A</c>.</param>
    /// <returns>The parts, or null when the string is not a Camelot key.</returns>
    public static (int Number, bool IsMinor)? Parse(string? key)
    {
        if (key is null || key.Length < 2)
        {
            return null;
        }

        var letter = char.ToUpperInvariant(key[^1]);
        if (letter != 'A' && letter != 'B')
        {
            return null;
        }

        if (!int.TryParse(key[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || number < 1 || number > 12)
        {
            return null;
        }

        return (number, letter == 'A');
    }

    /// <summary>
    /// The compatible moves: the same key, one step either way around the wheel, or the
    /// same number in the other letter.
    /// </summary>
    /// <param name="a">One key.</param>
    /// <param name="b">The other.</param>
    /// <returns>Whether they suit each other. False when either is unknown.</returns>
    public static bool AreCompatible(string? a, string? b)
    {
        if (Parse(a) is not { } first || Parse(b) is not { } second)
        {
            return false;
        }

        if (first.Number == second.Number)
        {
            return true;
        }

        var distance = Math.Abs(first.Number - second.Number);
        var around = Math.Min(distance, 12 - distance);
        return around == 1 && first.IsMinor == second.IsMinor;
    }
}
