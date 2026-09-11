using Jellyfin.Plugin.AoideSidecar.Next;
using Jellyfin.Plugin.AoideSidecar.Sound;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// The rules the clients did not have: the pool, kinship, similarity to the seed, the
/// arc, and how mixability orders what was chosen without choosing it. Every constant here has a test that fails when it changes.
/// </summary>
public sealed class NextRankerTests
{
    private const long Now = 1_760_000_000_000;
    private const long Day = 24L * 60 * 60 * 1000;

    private static readonly LibraryTrack Seed = new("seed", "Sabaton", new[] { "Metal" });

    [Fact]
    public void The_seed_the_queue_and_the_recent_are_never_returned()
    {
        var library = new[] { Seed, Track("queued"), Track("heard"), Track("free") };
        var request = new NextRequest("seed", new[] { "queued" }, new[] { "heard" }, false, 10);

        var result = Rank(request, library);

        Assert.Equal(new[] { "free" }, result.Candidates.Select(c => c.Id));
    }

    [Fact]
    public void Not_interested_is_never_returned_in_any_mode()
    {
        var library = new[] { Seed, Track("flagged"), Track("free") };

        foreach (var autoDj in new[] { false, true })
        {
            var result = Rank(new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), autoDj, 10), library, notInterested: new[] { "flagged" });
            Assert.Equal(new[] { "free" }, result.Candidates.Select(c => c.Id));
        }
    }

    [Fact]
    public void Ties_break_on_id_so_the_same_request_gives_the_same_answer_twice()
    {
        var library = new[] { Seed, Track("b"), Track("c"), Track("a") };
        var request = new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), false, 10);

        var first = Rank(request, library).Candidates.Select(c => c.Id).ToArray();
        var second = Rank(request, library).Candidates.Select(c => c.Id).ToArray();

        Assert.Equal(new[] { "a", "b", "c" }, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Taste_is_built_from_finished_plays_inside_ninety_days_and_reported()
    {
        var library = new[] { Seed, Track("metal", genres: "Metal"), Track("ambient", genres: "Ambient") };
        var history = new[]
        {
            Finished("seed", daysAgo: 1),
            Finished("seed", daysAgo: 2),
            // Outside the window: counts for nothing.
            Finished("ambient", daysAgo: 120),
            // A skip is not a fact about taste.
            Skipped("ambient", daysAgo: 1),
        };

        var result = Rank(new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), false, 10), library, history);

        Assert.Equal(2, result.ProfileEvents);
        Assert.Equal(Now - NextRanker.WindowMs, result.ProfileSince);
        var metal = result.Candidates.Single(c => c.Id == "metal");
        var ambient = result.Candidates.Single(c => c.Id == "ambient");
        Assert.True(metal.Score > ambient.Score);
        Assert.Equal(0.45, metal.Factors.Taste, 6);
        Assert.Equal(0, ambient.Factors.Taste, 6);
    }

    /// <summary>
    /// A record heard lately is also by an artist heard lately, so it pays both penalties
    /// — as it does on the phone, whose recent artists are read from its recent tracks.
    /// </summary>
    [Fact]
    public void Heard_lately_on_the_server_costs_both_penalties_and_reports_zero_freshness()
    {
        var library = new[] { Seed, Track("old-favourite", artist: "Perturbator"), Track("never-heard", artist: "Carpenter Brut") };
        var history = new[] { Skipped("old-favourite", daysAgo: 1) };

        var result = Rank(new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), false, 10), library, history);

        var heard = result.Candidates.Single(c => c.Id == "old-favourite");
        var fresh = result.Candidates.Single(c => c.Id == "never-heard");
        Assert.Equal(0, heard.Factors.Freshness);
        Assert.Equal(1, fresh.Factors.Freshness);
        Assert.Equal(fresh.Score - TasteRanking.RecentPenalty - TasteRanking.SameArtistPenalty, heard.Score, 9);
    }

    [Fact]
    public void The_same_artist_as_the_end_of_the_queue_costs_point_four()
    {
        var library = new[] { Seed, Track("queued", artist: "Perturbator"), Track("same-artist", artist: "Perturbator"), Track("other", artist: "Nobody") };
        var request = new NextRequest("seed", new[] { "queued" }, Array.Empty<string>(), false, 10);

        var result = Rank(request, library);

        var same = result.Candidates.Single(c => c.Id == "same-artist");
        var other = result.Candidates.Single(c => c.Id == "other");
        Assert.Equal(0.5, same.Factors.Freshness);
        Assert.Equal(other.Score - TasteRanking.SameArtistPenalty, same.Score, 9);
    }

    [Fact]
    public void Kinship_is_the_seeds_first_genre_and_nothing_can_be_said_of_an_untagged_record()
    {
        var library = new[]
        {
            Seed,
            Track("twin", genres: "metal"),
            Track("cousin", "Nobody", "Rock", "Metal"),
            Track("stranger", genres: "Ambient"),
            Track("untagged"),
        };

        var result = Rank(new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), false, 10), library);

        // The first genre is what a record is; the rest is what it touches.
        Assert.Equal(1, result.Candidates.Single(c => c.Id == "twin").Factors.Kinship);
        Assert.Equal(0.5, result.Candidates.Single(c => c.Id == "cousin").Factors.Kinship);
        Assert.Equal(0, result.Candidates.Single(c => c.Id == "stranger").Factors.Kinship);
        Assert.Equal(0.5, result.Candidates.Single(c => c.Id == "untagged").Factors.Kinship);

        // The largest term after taste: a record of the seed's kind beats one that is not
        // by 0.6, whatever the room thinks of either.
        var twin = result.Candidates.Single(c => c.Id == "twin");
        var stranger = result.Candidates.Single(c => c.Id == "stranger");
        Assert.Equal(NextRanker.KinshipWeight, twin.Score - stranger.Score, 9);
    }

    [Fact]
    public void Similarity_is_the_mean_of_what_can_be_answered_and_genre_is_not_part_of_it()
    {
        var library = new[] { Seed, Track("twin", genres: "Metal"), Track("stranger", genres: "Ambient"), Track("untagged") };
        var measured = new Dictionary<string, Measured>
        {
            ["seed"] = new(new Tempo(128, 1, 1), null, "8A", Flat(0.8)),
            // Same tempo, relative key, same energy: every part is 1.
            ["twin"] = new(new Tempo(128, 1, 1), null, "8B", Flat(0.8)),
            // Tempo 0 (bend far past six per cent), key 0, energy 0.5.
            ["stranger"] = new(new Tempo(100, 1, 1), null, "3B", Flat(0.3)),
            // Nothing measured: only the key part answers, at 0.5.
            ["untagged"] = new(null, null, null, null),
        };

        var result = Rank(new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), false, 10), library, measured: measured);

        Assert.Equal(1, result.Candidates.Single(c => c.Id == "twin").Factors.Similarity, 9);
        Assert.Equal((0 + 0 + 0.5) / 3, result.Candidates.Single(c => c.Id == "stranger").Factors.Similarity, 9);
        Assert.Equal(0.5, result.Candidates.Single(c => c.Id == "untagged").Factors.Similarity, 9);
    }

    [Fact]
    public void Tempo_similarity_folds_into_one_octave_and_falls_off_past_two_per_cent()
    {
        var library = new[] { Seed, Track("double"), Track("bent"), Track("unstable") };
        var measured = new Dictionary<string, Measured>
        {
            ["seed"] = new(new Tempo(128, 1, 1), null, null, null),
            ["double"] = new(new Tempo(256, 1, 1), null, null, null),
            ["bent"] = new(new Tempo(128 * 1.04, 1, 1), null, null, null),
            // Its tempo does not describe it: the part is left out, not scored.
            ["unstable"] = new(new Tempo(128, 1, 0.2), null, null, null),
        };

        var result = Rank(new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), false, 10), library, measured: measured);

        // Key answers 0.5 for everyone; tempo answers 1, 0.5 and nothing.
        Assert.Equal((1 + 0.5) / 2, result.Candidates.Single(c => c.Id == "double").Factors.Similarity, 9);
        Assert.Equal((0.5 + 0.5) / 2, result.Candidates.Single(c => c.Id == "bent").Factors.Similarity, 9);
        Assert.Equal(0.5, result.Candidates.Single(c => c.Id == "unstable").Factors.Similarity, 9);
    }

    [Fact]
    public void Mixability_is_absent_outside_auto_dj_and_for_unread_records()
    {
        var library = new[] { Seed, Track("read"), Track("unread") };
        var measured = new Dictionary<string, Measured>
        {
            ["seed"] = Gridded(128),
            ["read"] = Gridded(128),
            ["unread"] = new(new Tempo(128, 1, 1), null, null, null),
        };

        var infinity = Rank(new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), false, 10), library, measured: measured);
        Assert.All(infinity.Candidates, c => Assert.Null(c.Factors.Mixability));

        var autoDj = Rank(new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), true, 10), library, measured: measured);
        Assert.NotNull(autoDj.Candidates.Single(c => c.Id == "read").Factors.Mixability);
        Assert.Null(autoDj.Candidates.Single(c => c.Id == "unread").Factors.Mixability);
    }

    /// <summary>
    /// Mixability is not in the choosing score in either mode: a good record that can only
    /// be crossfaded is still the better record. It decides the order, not the choice.
    /// </summary>
    [Fact]
    public void Mixability_does_not_change_what_is_chosen_only_the_order()
    {
        var library = new[] { Seed, Track("mixable"), Track("too-fast") };
        var measured = new Dictionary<string, Measured>
        {
            ["seed"] = Gridded(128),
            ["mixable"] = Gridded(128),
            ["too-fast"] = Gridded(100),
        };

        var infinity = Rank(new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), false, 10), library, measured: measured);
        var autoDj = Rank(new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), true, 10), library, measured: measured);

        foreach (var id in new[] { "mixable", "too-fast" })
        {
            Assert.Equal(infinity.Candidates.Single(c => c.Id == id).Score, autoDj.Candidates.Single(c => c.Id == id).Score, 12);
        }

        var expected = DJPlanner.Plan(
            new MixRecord(measured["seed"].Grid!, null, measured["seed"].Arrangement),
            new MixRecord(measured["mixable"].Grid!, null, measured["mixable"].Arrangement))!.Score.Total;
        Assert.Equal("mixable", autoDj.Candidates[0].Id);
        Assert.Equal(expected, autoDj.Candidates[0].Factors.Mixability!.Value, 12);
        // The pair mixable → too-fast is refused: the crossfade figure, from the record before it.
        Assert.Equal(NextRanker.CrossfadeOnly, autoDj.Candidates[1].Factors.Mixability!.Value, 12);
    }

    /// <summary>
    /// The chain starts from what the first chosen record will follow: the end of the
    /// queue when there is one, the seed otherwise.
    /// </summary>
    [Fact]
    public void The_chain_starts_from_the_end_of_the_queue()
    {
        var library = new[] { Seed, Track("queued"), Track("fits-seed"), Track("fits-queued") };
        var pairs = new Dictionary<string, double>
        {
            ["seed>fits-seed"] = 0.9,
            ["seed>fits-queued"] = 0.2,
            ["queued>fits-queued"] = 0.9,
            ["queued>fits-seed"] = 0.2,
        };

        var withQueue = NextRanker.Rank(
            new NextRequest("seed", new[] { "queued" }, Array.Empty<string>(), true, 10),
            library, Array.Empty<PlayEvent>(), new HashSet<string>(), new Dictionary<string, Measured>(), new Table(pairs), Now);
        var without = NextRanker.Rank(
            new NextRequest("seed", Array.Empty<string>(), Array.Empty<string>(), true, 10),
            library, Array.Empty<PlayEvent>(), new HashSet<string>(), new Dictionary<string, Measured>(), new Table(pairs), Now);

        Assert.Equal("fits-queued", withQueue.Candidates[0].Id);
        Assert.Equal("fits-seed", without.Candidates[0].Id);
    }

    private sealed class Table : IMixability
    {
        private readonly IReadOnlyDictionary<string, double> _pairs;

        public Table(IReadOnlyDictionary<string, double> pairs)
        {
            _pairs = pairs;
        }

        public double? Score(string from, string to) => _pairs.TryGetValue($"{from}>{to}", out var score) ? score : null;
    }

    [Fact]
    public void After_three_rises_the_set_wants_a_breather()
    {
        // recent (newest first): 0.5, 0.35, 0.2 → oldest first 0.2, 0.35, 0.5, then the seed at 0.7.
        var library = new[] { Seed, Track("r1"), Track("r2"), Track("r3"), Track("breather"), Track("banger") };
        var measured = new Dictionary<string, Measured>
        {
            ["seed"] = new(null, null, null, Flat(0.7)),
            ["r1"] = new(null, null, null, Flat(0.5)),
            ["r2"] = new(null, null, null, Flat(0.35)),
            ["r3"] = new(null, null, null, Flat(0.2)),
            ["breather"] = new(null, null, null, Flat(0.45)),
            ["banger"] = new(null, null, null, Flat(0.95)),
        };

        var result = Rank(new NextRequest("seed", Array.Empty<string>(), new[] { "r1", "r2", "r3" }, false, 10), library, measured: measured);

        // Target is 0.7 − 0.25 = 0.45.
        Assert.Equal(1, result.Candidates.Single(c => c.Id == "breather").Factors.Arc, 9);
        Assert.Equal(0.5, result.Candidates.Single(c => c.Id == "banger").Factors.Arc, 9);
    }

    [Fact]
    public void After_two_falls_the_set_wants_to_come_back()
    {
        var library = new[] { Seed, Track("r1"), Track("r2"), Track("up"), Track("unread") };
        var measured = new Dictionary<string, Measured>
        {
            ["seed"] = new(null, null, null, Flat(0.4)),
            ["r1"] = new(null, null, null, Flat(0.6)),
            ["r2"] = new(null, null, null, Flat(0.8)),
            ["up"] = new(null, null, null, Flat(0.6)),
        };

        var result = Rank(new NextRequest("seed", Array.Empty<string>(), new[] { "r1", "r2" }, false, 10), library, measured: measured);

        // 0.8 → 0.6 → 0.4 fell twice; target is 0.4 + 0.2 = 0.6.
        Assert.Equal(1, result.Candidates.Single(c => c.Id == "up").Factors.Arc, 9);
        Assert.Equal(0.5, result.Candidates.Single(c => c.Id == "unread").Factors.Arc, 9);
    }

    [Fact]
    public void The_arc_is_read_from_the_end_of_the_queue_when_there_is_one()
    {
        var library = new[] { Seed, Track("q1"), Track("q2"), Track("q3"), Track("hold") };
        var measured = new Dictionary<string, Measured>
        {
            ["seed"] = new(null, null, null, Flat(0.9)),
            ["q1"] = new(null, null, null, Flat(0.5)),
            ["q2"] = new(null, null, null, Flat(0.5)),
            ["q3"] = new(null, null, null, Flat(0.3)),
            ["hold"] = new(null, null, null, Flat(0.3)),
        };

        var result = Rank(new NextRequest("seed", new[] { "q1", "q2", "q3" }, Array.Empty<string>(), false, 10), library, measured: measured);

        // The last four are seed, q1, q2, q3: neither three rises nor two falls, so hold
        // at the last of them, 0.3 — not at the seed's 0.9.
        Assert.Equal(1, result.Candidates.Single(c => c.Id == "hold").Factors.Arc, 9);
    }

    [Fact]
    public void Mean_energy_is_weighted_by_section_length()
    {
        var arrangement = new Arrangement(
            new[] { new Section(0, 30_000, SectionKind.Intro, 0), new Section(30_000, 120_000, SectionKind.Drop, 1) },
            null, null, null);

        Assert.Equal(0.75, NextRanker.MeanEnergy(arrangement)!.Value, 9);
        Assert.Null(NextRanker.MeanEnergy(null));
    }

    private static NextResult Rank(
        NextRequest request,
        IReadOnlyList<LibraryTrack> library,
        IReadOnlyList<PlayEvent>? history = null,
        IReadOnlyDictionary<string, Measured>? measured = null,
        IEnumerable<string>? notInterested = null) =>
        NextRanker.Rank(
            request,
            library,
            history ?? Array.Empty<PlayEvent>(),
            (notInterested ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase),
            measured ?? new Dictionary<string, Measured>(),
            new PlannerMixability(measured ?? new Dictionary<string, Measured>()),
            Now);

    private static LibraryTrack Track(string id, string artist = "Nobody", params string[] genres) => new(id, artist, genres);

    private static PlayEvent Finished(string id, int daysAgo) => new(id, Now - (daysAgo * Day), Now - (daysAgo * Day) + 200_000, true, false);

    private static PlayEvent Skipped(string id, int daysAgo) => new(id, Now - (daysAgo * Day), Now - (daysAgo * Day) + 4_000, false, true);

    private static Arrangement Flat(double energy) =>
        new(new[] { new Section(0, 240_000, SectionKind.Unknown, energy) }, null, null, Array.Empty<VocalSpan>());

    /// <summary>A four-minute record, gridded, read and unremarkable, at a tempo.</summary>
    private static Measured Gridded(double bpm)
    {
        var barMs = 60_000 / bpm * 4;
        var grid = new BeatGrid(
            new[] { new BeatSegment(0, 240_000, 0, bpm, 5, (int)(240_000 / (60_000 / bpm))) },
            4, 0, barMs * 8, barMs * 112);
        return new Measured(new Tempo(bpm, 1, 1), grid, null, Flat(0.5));
    }
}
