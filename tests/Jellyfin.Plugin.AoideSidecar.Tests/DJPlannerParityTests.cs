using Jellyfin.Plugin.AoideSidecar.Next;
using Jellyfin.Plugin.AoideSidecar.Sound;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// The same forty-one pairs the phone and the desktop plan, with the same forty-one plans.
/// </summary>
/// <remarks>
/// Transcribed from the clients' <c>DJPlannerParityTests.swift</c>, which is duplicated
/// verbatim in the desktop's <c>dj-planner-parity.test.ts</c>. Starts and entries are
/// exact milliseconds and the score is the exact double, because two planners a bar apart
/// are still two planners. A pair scored differently from the phone is a bug in whichever
/// side is wrong, and the table says which.
/// </remarks>
public sealed class DJPlannerParityTests
{
    private static readonly double Bar = 60_000.0 / 128 * 4;

    /// <summary>The slower record in "bend eats record": 128 over 1.01, mixable from bar 50.</summary>
    private static readonly double SlowBpm = 128 / 1.01;
    private static readonly double SlowMixIn = 60_000 / SlowBpm * 4 * 50;

    private static readonly Arrangement Plain = Arranged((SectionKind.Intro, 16, 0.2), (SectionKind.Drop, 96, 0.9), (SectionKind.Outro, 16, 0.2));
    private static readonly Arrangement DropOut = Arranged((SectionKind.Intro, 16, 0.2), (SectionKind.Drop, 112, 0.9));
    private static readonly Arrangement DropIn = Arranged((SectionKind.Drop, 128, 0.9));
    private static readonly Arrangement BuildIn = Arranged((SectionKind.Build, 32, 0.4), (SectionKind.Drop, 96, 0.9));
    private static readonly Arrangement WithBuild = Arranged((SectionKind.Intro, 16, 0.2), (SectionKind.Build, 16, 0.5), (SectionKind.Drop, 80, 0.95), (SectionKind.Outro, 16, 0.2));
    private static readonly Arrangement MidEnergy = Arranged((SectionKind.Intro, 16, 0.2), (SectionKind.Drop, 96, 0.55), (SectionKind.Outro, 16, 0.2));
    private static readonly Arrangement BreakdownOut = Arranged((SectionKind.Intro, 16, 0.2), (SectionKind.Drop, 80, 0.9), (SectionKind.Breakdown, 16, 0.5), (SectionKind.Outro, 16, 0.2));
    private static readonly Arrangement SingingOut = Arranged(new[] { new VocalSpan(Bar * 90, Bar * 128) }, (SectionKind.Drop, 128, 0.9));
    private static readonly Arrangement SingingIn = Arranged(new[] { new VocalSpan(0, Bar * 40) }, (SectionKind.Drop, 128, 0.9));
    private static readonly Arrangement SingsLater = Arranged(new[] { new VocalSpan(Bar * 40, Bar * 80) }, (SectionKind.Drop, 128, 0.9));
    private static readonly Arrangement CannotTell = Arranged(null, (SectionKind.Drop, 128, 0.9));
    private static readonly Arrangement Instrumental = Arranged((SectionKind.Drop, 128, 0.9));
    private static readonly Arrangement NoPhrases = Arranged(phraseBars: null, Array.Empty<VocalSpan>(), (SectionKind.Unknown, 128, 0.5));
    private static readonly Arrangement BuildThenDrop = Arranged((SectionKind.Build, 32, 0.4), (SectionKind.Drop, 96, 0.9));
    private static readonly Arrangement SingingThroughout = Arranged(new[] { new VocalSpan(0, Bar * 128) }, (SectionKind.Build, 32, 0.4), (SectionKind.Drop, 96, 0.9));
    private static readonly Arrangement SingsAtEighty = Arranged(new[] { new VocalSpan(Bar * 80, Bar * 90) }, (SectionKind.Drop, 128, 1));
    private static readonly Arrangement QuietDrop = Arranged((SectionKind.Drop, 128, 0));

    /// <summary>Rows by index: the row types are internal to the plugin, and xUnit's data source must be public.</summary>
    public static IEnumerable<object[]> Table() => Enumerable.Range(0, Rows.Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Table))]
    public void Plans_the_clients_table_identically(int index)
    {
        var row = Rows[index];
        var plan = DJPlanner.Plan(row.Outgoing, row.Incoming, row.NotBeforeMs);

        if (row.Bars is not { } bars)
        {
            Assert.True(plan is null, $"{row.Why}: expected a refusal, got {plan?.Bars} bars");
            return;
        }

        Assert.True(plan is not null, $"{row.Why}: expected {bars} bars, got a refusal");
        Assert.True(plan!.Bars == bars, $"{row.Why}: bars {plan.Bars}");
        Assert.True(Math.Abs(plan.OutgoingStartMs - row.OutgoingStartMs) < 1e-9, $"{row.Why}: start {plan.OutgoingStartMs}");
        Assert.True(Math.Abs(plan.IncomingStartMs - row.IncomingStartMs) < 1e-9, $"{row.Why}: entry {plan.IncomingStartMs}");
        Assert.True(Math.Abs(plan.IncomingRate - row.IncomingRate) < 1e-12, $"{row.Why}: rate {plan.IncomingRate}");
        Assert.True(Math.Abs(plan.Score.Total - row.Total) < 1e-12, $"{row.Why}: total {plan.Score.Total}");
        Assert.True(plan.Style == row.Style, $"{row.Why}: style {plan.Style}");
        Assert.Equal(1, plan.OutgoingRate);
        Assert.Equal(8, plan.RestoreSeconds);
    }

    [Fact]
    public void Answers_every_case_in_the_table()
    {
        Assert.Equal(41, Rows.Count);
    }

    private static readonly IReadOnlyList<ParityCase> Rows = new[]
    {
        Unremarkable("two records at one tempo get sixteen bars",
            Record(128), Record(128),
            16, 206_250, 0, 1, 0.68),
        Unremarkable("the mix ends where the outgoing record stops being mixable",
            Record(128, mixOutMs: 180_000), Record(128),
            16, 150_000, 0, 1, 0.68),
        Unremarkable("an anchor off zero keeps the start on its own downbeats",
            Record(128, anchorMs: 431.2, mixOutMs: 431.2 + (1875 * 64)), Record(128),
            16, 90_431.2, 0, 1, 0.68),
        Unremarkable("126 into 128 bends the incoming record up",
            Record(128), Record(126),
            16, 206_250, 0, 1.0158730158730158, 0.68),
        Unremarkable("130 into 128 bends it down",
            Record(128), Record(130),
            16, 206_250, 0, 0.9846153846153847, 0.68),
        Unremarkable("100 into 128 is not a bend, it is a different record",
            Record(128), Record(100),
            null),
        Unremarkable("double time is played at its own speed",
            Record(85), Record(170),
            16, 189_176.4705882353, 0, 1, 0.68),
        Unremarkable("and so is half time",
            Record(170), Record(85),
            16, 214_588.23529411765, 0, 1, 0.68),
        Unremarkable("eight bars when sixteen will not fit",
            Record(128, mixOutMs: 20_000), Record(128),
            8, 3_750, 0, 1, 0.68),
        Unremarkable("and nothing when eight will not",
            Record(128, mixOutMs: 10_000), Record(128),
            null),
        Unremarkable("a mix cannot start before the engine could have got ready",
            Record(128, mixOutMs: 180_000), Record(128),
            8, 165_000, 0, 1, 0.68, notBeforeMs: 160_000),
        Unremarkable("and too late is a crossfade",
            Record(128, mixOutMs: 180_000), Record(128),
            null, notBeforeMs: 179_000),
        Unremarkable("no meter behind, no mix",
            Record(128, beatsPerBar: null), Record(128),
            null),
        Unremarkable("no meter ahead, no mix",
            Record(128), Record(128, beatsPerBar: null),
            null),
        Unremarkable("a grid its own record does not sit on is not one to mix to",
            Record(128), Record(128, residualMs: 60),
            null),
        Unremarkable("the incoming record must have the bars to give",
            Record(128), Record(128, lengthMs: 110_000, mixInMs: 100_000, mixOutMs: 105_000),
            null),
        Unremarkable("a bent record is consumed faster than the clock: thirty seconds is not enough",
            Record(128), Record(SlowBpm, lengthMs: SlowMixIn + 30_100, mixInMs: SlowMixIn, mixOutMs: 120_000),
            8, 221_250, 94_687.5, 1.01, 0.68),
        Unremarkable("half a second more record and it gets the sixteen",
            Record(128), Record(SlowBpm, lengthMs: SlowMixIn + 30_600, mixInMs: SlowMixIn, mixOutMs: 120_000),
            16, 206_250, 94_687.5, 1.01, 0.68),
        Unremarkable("records that clash are capped at eight and get the filter fade",
            Record(128, key: "3B"), Record(128, key: "10B"),
            8, 221_250, 0, 1, 0.58, style: MixStyle.FilterFade),
        Unremarkable("neighbours are not capped, and the score decides on longer",
            Record(128, key: "8A"), Record(128, key: "9A"),
            32, 176_250, 0, 1, 0.78),
        Unremarkable("both ends are put on the grid, whatever the mix points say",
            Record(128, mixOutMs: 200_234), Record(128, anchorMs: 100, mixInMs: 40_051),
            16, 168_750, 39_475, 1, 0.68),
        Case("an outgoing record that has not been read is not mixed",
            Grid(), Grid(), null, Plain, null),
        Case("nor is an incoming one",
            Grid(), Grid(), Plain, null, null),
        Case("matching tempo is not a substitute for knowing the arrangement",
            Grid(), Grid(), null, null, null),
        Case("a mix starts on the bar when the phrase line is more than two bars back",
            Grid(mixOutMs: Bar * 115), Grid(), Plain, Plain,
            32, 155_625, 0, 1, 0.7600000000000001),
        Case("and comes in on one",
            Grid(), Grid(mixInMs: Bar * 11), Plain, Plain,
            32, 150_000, 0, 1, 0.7600000000000001),
        Case("an entry a bar short of a phrase line moves forward onto it",
            Grid(), Grid(mixInMs: Bar * 15), Plain, Plain,
            32, 150_000, 30_000, 1, 0.8000000000000002),
        Case("without a phrase grid, a bar line is the best there is",
            Grid(mixOutMs: Bar * 115), Grid(mixInMs: Bar * 11), NoPhrases, NoPhrases,
            32, 155_625, 20_625, 1, 0.8000000000000002),
        Case("two records singing at once is not a mix",
            Grid(), Grid(), SingingOut, SingingIn, null),
        Case("one voice is fine, and gets the filter fade",
            Grid(), Grid(), SingingOut, SingsLater,
            8, 195_000, 15_000, 1, 0.665, MixStyle.FilterFade),
        Case("not being able to tell counts as singing",
            Grid(), Grid(), CannotTell, SingingIn, null),
        Case("looked and found none is not the same as could not tell",
            Grid(), Grid(), CannotTell, Instrumental,
            8, 195_000, 15_000, 1, 0.665, MixStyle.FilterFade),
        Case("a drop onto a drop gets eight bars, not sixteen",
            Grid(), Grid(), DropOut, DropIn,
            8, 195_000, 15_000, 1, 0.7250000000000001),
        Case("coming in on a build keeps the sixteen",
            Grid(), Grid(), DropOut, BuildIn,
            16, 180_000, 0, 1, 0.7250000000000001),
        Case("a record comes in on its build when it has one",
            Grid(), Grid(), MidEnergy, WithBuild,
            32, 150_000, 30_000, 1, 0.8900000000000001),
        Case("everything right earns thirty-two",
            Grid(key: "9A"), Grid(key: "8A"), BreakdownOut, WithBuild,
            32, 150_000, 30_000, 1, 0.92),
        Case("compatible keys and nobody singing is a blend",
            Grid(key: "8A"), Grid(key: "9A"), BuildThenDrop, BuildThenDrop,
            32, 150_000, 0, 1, 0.825),
        Case("clashing keys are a capped filter fade",
            Grid(key: "3B"), Grid(key: "10B"), BuildThenDrop, BuildThenDrop,
            8, 195_000, 0, 1, 0.625, MixStyle.FilterFade),
        Case("the outgoing record singing is a filter fade, uncapped",
            Grid(key: "8A"), Grid(key: "9A"), SingingThroughout, BuildThenDrop,
            32, 150_000, 0, 1, 0.7649999999999999, MixStyle.FilterFade),
        Case("and so is the incoming one",
            Grid(key: "8A"), Grid(key: "9A"), BuildThenDrop, SingingThroughout,
            32, 150_000, 0, 1, 0.7649999999999999, MixStyle.FilterFade),
        Case("a score refused at thirty-two bars refuses the entry, though sixteen would have been clean",
            Grid(key: "3B"), Record(128 / 1.03, key: "10B", mixInMs: Bar * 8, mixOutMs: Bar * 112), SingsAtEighty, QuietDrop,
            null),
    };

    /// <summary>
    /// A four-minute record at a tempo, gridded from an anchor, mixable from its first
    /// downbeat to two bars before the end.
    /// </summary>
    private static Gridded Record(
        double bpm,
        double anchorMs = 0,
        double lengthMs = 240_000,
        double residualMs = 5,
        string? key = null,
        double? mixInMs = null,
        double? mixOutMs = null,
        int? beatsPerBar = 4)
    {
        var barMs = 60_000 / bpm * 4;
        var grid = new BeatGrid(
            new[] { new BeatSegment(0, lengthMs, anchorMs, bpm, residualMs, (int)(lengthMs / (60_000 / bpm))) },
            beatsPerBar,
            beatsPerBar is null ? null : 0,
            beatsPerBar is null ? null : mixInMs ?? anchorMs,
            beatsPerBar is null ? null : mixOutMs ?? (anchorMs + (lengthMs - anchorMs - (barMs * 2))));
        return new Gridded(grid, key);
    }

    /// <summary>A four-minute record at 128, mixable from bar 8 to sixteen bars before the end.</summary>
    private static Gridded Grid(double? mixInMs = null, double? mixOutMs = null, string? key = null) =>
        Record(128, key: key, mixInMs: mixInMs ?? Bar * 8, mixOutMs: mixOutMs ?? Bar * 112);

    private static Arrangement Arranged(params (string Kind, int Bars, double Energy)[] parts) =>
        Arranged(Array.Empty<VocalSpan>(), parts);

    private static Arrangement Arranged(IReadOnlyList<VocalSpan>? vocals, params (string Kind, int Bars, double Energy)[] parts) =>
        Arranged(16, vocals, parts);

    /// <summary>Sections in bars, laid end to end.</summary>
    private static Arrangement Arranged(int? phraseBars, IReadOnlyList<VocalSpan>? vocals, params (string Kind, int Bars, double Energy)[] parts)
    {
        var sections = new List<Section>();
        var at = 0.0;
        foreach (var (kind, bars, energy) in parts)
        {
            sections.Add(new Section(at, at + (Bar * bars), kind, energy));
            at += Bar * bars;
        }

        return new Arrangement(sections, phraseBars, phraseBars is null ? null : 0, vocals);
    }

    /// <summary>
    /// A record that has been read and found unremarkable: one section nobody could name,
    /// instrumental, no phrase grid.
    /// </summary>
    private static Arrangement Read(double energy) =>
        new(new[] { new Section(0, 600_000, SectionKind.Unknown, energy) }, null, null, Array.Empty<VocalSpan>());

    private static ParityCase Unremarkable(
        string why, Gridded outgoing, Gridded incoming,
        int? bars, double outgoingStartMs = 0, double incomingStartMs = 0, double incomingRate = 1,
        double total = 0, MixStyle style = MixStyle.Blend, double notBeforeMs = 0) =>
        new(why, new MixRecord(outgoing.Grid, outgoing.Key, Read(0.8)), new MixRecord(incoming.Grid, incoming.Key, Read(0.2)),
            notBeforeMs, bars, outgoingStartMs, incomingStartMs, incomingRate, total, style);

    private static ParityCase Case(
        string why, Gridded outgoing, Gridded incoming, Arrangement? outgoingArrangement, Arrangement? incomingArrangement,
        int? bars, double outgoingStartMs = 0, double incomingStartMs = 0, double incomingRate = 1,
        double total = 0, MixStyle style = MixStyle.Blend) =>
        new(why, new MixRecord(outgoing.Grid, outgoing.Key, outgoingArrangement), new MixRecord(incoming.Grid, incoming.Key, incomingArrangement),
            0, bars, outgoingStartMs, incomingStartMs, incomingRate, total, style);

    private sealed record Gridded(BeatGrid Grid, string? Key);

    private sealed record ParityCase(
        string Why,
        MixRecord Outgoing,
        MixRecord Incoming,
        double NotBeforeMs,
        int? Bars,
        double OutgoingStartMs,
        double IncomingStartMs,
        double IncomingRate,
        double Total,
        MixStyle Style)
    {
        public override string ToString() => Why;
    }
}
