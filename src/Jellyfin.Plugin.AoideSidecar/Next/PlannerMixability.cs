namespace Jellyfin.Plugin.AoideSidecar.Next;

/// <summary>
/// The planner's opinion of each pair, over what the server has measured.
/// </summary>
internal sealed class PlannerMixability : IMixability
{
    private readonly IReadOnlyDictionary<string, Measured> _measured;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlannerMixability"/> class.
    /// </summary>
    /// <param name="measured">What is measured about each track, by id.</param>
    public PlannerMixability(IReadOnlyDictionary<string, Measured> measured)
    {
        _measured = measured;
    }

    /// <inheritdoc />
    public double? Score(string from, string to)
    {
        var outgoing = _measured.GetValueOrDefault(from);
        var incoming = _measured.GetValueOrDefault(to);
        if (outgoing?.Grid is null || outgoing.Arrangement is null || incoming?.Grid is null || incoming.Arrangement is null)
        {
            return null;
        }

        var plan = DJPlanner.Plan(
            new MixRecord(outgoing.Grid, outgoing.Key, outgoing.Arrangement),
            new MixRecord(incoming.Grid, incoming.Key, incoming.Arrangement));

        return plan?.Score.Total ?? NextRanker.CrossfadeOnly;
    }
}
