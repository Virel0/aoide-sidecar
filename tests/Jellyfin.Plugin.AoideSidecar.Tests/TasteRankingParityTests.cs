using Jellyfin.Plugin.AoideSidecar.Next;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// The same sixteen candidates the phone and the desktop score, with the same sixteen
/// scores and the same one order.
/// </summary>
/// <remarks>
/// Transcribed from the clients' <c>TasteRankingParityTests.swift</c>, duplicated verbatim
/// in the desktop's <c>taste-ranking-parity.test.ts</c>. Rounded to six places, and all
/// three sides round: the arithmetic is IEEE doubles everywhere and lands on the same bits,
/// but a table full of 0.15000000000000002 tells a reader nothing about the rule.
/// </remarks>
public sealed class TasteRankingParityTests
{
    private static readonly TasteProfile Profile = new(
        new Dictionary<string, double> { ["electronic"] = 0.5, ["metal"] = 1 },
        new Dictionary<string, double> { ["perturbator"] = 0.4, ["sabaton"] = 1 },
        new HashSet<string> { "heard-an-hour-ago" },
        new HashSet<string>());

    private static readonly (string Why, string Id, string Artist, string[] Genres, FinishCounts? Finish, double Score)[] Rows =
    {
        ("a favourite genre is most of what there is to earn", "metal-stranger", "Nobody", new[] { "Metal" }, null, 0.45),
        ("an unfamiliar genre earns nothing, and is not held against a track", "ambient-stranger", "Nobody", new[] { "Ambient" }, null, 0),
        ("a genre matches however it was capitalised", "shouted-genre", "Nobody", new[] { "METAL" }, null, 0.45),
        ("so does an artist", "shouted-artist", "SABATON", Array.Empty<string>(), null, 0.3),
        ("four genres are not four times as relevant as the right one", "four-times-electronic", "Nobody", new[] { "Electronic", "Electronic", "Electronic", "Electronic" }, null, 0.225),
        ("the best match counts wherever in the tags it sits", "mixed-tags", "Nobody", new[] { "Ambient", "Metal", "Electronic" }, null, 0.45),
        ("the favourite artist, the favourite genre and always finished is the top of the scale", "favourite-everything", "Sabaton", new[] { "Metal" }, new FinishCounts(10, 10), 1.05),
        ("a track you never finish sinks, but its genre still counts for it", "always-skipped", "Nobody", new[] { "Metal" }, new FinishCounts(10, 0), 0.15),
        ("one skip out of one says nothing, and scores as no history at all", "barely-judged", "Nobody", new[] { "Metal" }, new FinishCounts(1, 0), 0.45),
        ("two finishes out of two is still under the sample floor", "two-of-two", "Nobody", Array.Empty<string>(), new FinishCounts(2, 2), 0),
        ("three is the first sample either app will speak from", "three-of-three", "Nobody", Array.Empty<string>(), new FinishCounts(3, 3), 0.3),
        ("and it counts against a track exactly as far as it counts for one", "none-of-three", "Nobody", Array.Empty<string>(), new FinishCounts(3, 0), -0.3),
        ("two thirds finished is a third of the way up the band", "two-of-three", "Nobody", Array.Empty<string>(), new FinishCounts(3, 2), 0.1),
        ("a second-favourite genre and artist together still lose to a favourite genre alone", "second-favourite", "Perturbator", new[] { "Electronic" }, null, 0.345),
        ("the best song there is, heard an hour ago, falls below the worst that has not been", "heard-an-hour-ago", "Sabaton", new[] { "Metal" }, new FinishCounts(10, 10), -0.35),
        ("nothing familiar and nothing measured is the middle of the scale", "nothing-known", "Nobody", Array.Empty<string>(), null, 0),
    };

    /// <summary>The one order the table's sixteen come out in, best first.</summary>
    private static readonly string[] Order =
    {
        "favourite-everything", "barely-judged", "metal-stranger", "mixed-tags", "shouted-genre",
        "second-favourite", "shouted-artist", "three-of-three", "four-times-electronic", "always-skipped",
        "two-of-three", "ambient-stranger", "nothing-known", "two-of-two", "none-of-three", "heard-an-hour-ago",
    };

    public static IEnumerable<object[]> Table() => Enumerable.Range(0, Rows.Length).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Table))]
    public void Scores_the_clients_table_identically(int index)
    {
        var (why, id, artist, genres, finish, expected) = Rows[index];
        var score = TasteRanking.Score(new TasteCandidate(id, artist, genres), Profile, finish);
        Assert.True(Rounded(score) == expected, $"{why}: scored {score}");
    }

    [Fact]
    public void Puts_the_whole_table_in_one_order_ties_broken_on_id()
    {
        var ranked = TasteRanking.Best(Scored(), c => c.Id, Rows.Length).Select(c => c.Id).ToArray();
        Assert.Equal(Order, ranked);
    }

    [Fact]
    public void Takes_the_best_five_when_five_are_asked_for()
    {
        var ranked = TasteRanking.Best(Scored(), c => c.Id, 5).Select(c => c.Id).ToArray();
        Assert.Equal(Order.Take(5).ToArray(), ranked);
    }

    [Fact]
    public void Asking_for_none_gets_none()
    {
        Assert.Empty(TasteRanking.Best(Scored(), c => c.Id, 0));
    }

    [Fact]
    public void Answers_every_case_in_the_table()
    {
        Assert.Equal(16, Rows.Length);
        Assert.Equal(Rows.Length, Order.Length);
    }

    private static IEnumerable<(TasteCandidate, double)> Scored() =>
        Rows.Select(r =>
        {
            var candidate = new TasteCandidate(r.Id, r.Artist, r.Genres);
            return (candidate, TasteRanking.Score(candidate, Profile, r.Finish));
        });

    private static double Rounded(double score) => Math.Round(score * 1_000_000) / 1_000_000;
}
