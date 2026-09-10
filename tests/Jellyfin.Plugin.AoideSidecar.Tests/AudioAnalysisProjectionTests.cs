using Jellyfin.Plugin.AoideSidecar.Api;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Sound;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// What a cached row becomes on the wire — in particular the confidence threshold, which
/// is applied here rather than when measuring so it can be reconsidered later.
/// </summary>
public sealed class AudioAnalysisProjectionTests
{
    [Fact]
    public void A_confident_tempo_is_reported_with_its_confidence_and_stability()
    {
        var dto = AudioAnalysisController.Project(Row(new Loudness(-9.7, -0.3), new Tempo(128, 0.82, 1.0)));

        Assert.Equal(-9.7, dto.LoudnessLufs);
        Assert.Equal(-0.3, dto.TruePeakDbfs);
        Assert.Equal(128, dto.Bpm);
        Assert.Equal(0.82, dto.BpmConfidence);
        Assert.Equal(1.0, dto.BpmStability);
        Assert.False(dto.IsEmpty);
    }

    /// <summary>
    /// Stability rides with the tempo. Withheld tempo, withheld stability — it describes a
    /// number the client is not being given.
    /// </summary>
    [Fact]
    public void Stability_is_withheld_along_with_the_tempo_it_describes()
    {
        var dto = AudioAnalysisController.Project(Row(null, new Tempo(96, 0.31, 0.9)));

        Assert.Null(dto.Bpm);
        Assert.Null(dto.BpmStability);
    }

    /// <summary>
    /// A track too short to compare windows across still has a tempo; it just has no
    /// answer to whether the tempo holds.
    /// </summary>
    [Fact]
    public void A_tempo_with_no_stability_is_still_reported()
    {
        var dto = AudioAnalysisController.Project(Row(null, new Tempo(128, 0.9)));

        Assert.Equal(128, dto.Bpm);
        Assert.Null(dto.BpmStability);
        Assert.False(dto.IsEmpty);
    }

    /// <summary>
    /// An unsure tempo is worse than none: the clients order by it, and a made-up number
    /// is ordered against as if it were true.
    /// </summary>
    [Fact]
    public void An_unsure_tempo_is_not_reported_but_the_loudness_still_is()
    {
        var dto = AudioAnalysisController.Project(Row(new Loudness(-14.2, -1.1), new Tempo(96, 0.31, 0.8)));

        Assert.Equal(-14.2, dto.LoudnessLufs);
        Assert.Null(dto.Bpm);
        Assert.Null(dto.BpmConfidence);
        Assert.False(dto.IsEmpty);
    }

    [Fact]
    public void A_tempo_without_a_loudness_is_reported_on_its_own()
    {
        var dto = AudioAnalysisController.Project(Row(null, new Tempo(174, 0.9, 0.95)));

        Assert.Null(dto.LoudnessLufs);
        Assert.Equal(174, dto.Bpm);
        Assert.False(dto.IsEmpty);
    }

    /// <summary>
    /// Measured, and nothing came of it. The endpoint sends null for the whole entry
    /// rather than an object of four nulls.
    /// </summary>
    [Fact]
    public void A_row_with_nothing_worth_reporting_is_empty()
    {
        Assert.True(AudioAnalysisController.Project(Row(null, null)).IsEmpty);
        Assert.True(AudioAnalysisController.Project(Row(null, new Tempo(96, 0.1))).IsEmpty);
    }

    private static AudioAnalysisRow Row(Loudness? loudness, Tempo? tempo) =>
        new("t1", 1, loudness, tempo, "server", null, 0);
}
