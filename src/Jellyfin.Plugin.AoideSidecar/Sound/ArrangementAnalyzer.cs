namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Works out what a track is made of and in what order.
/// </summary>
/// <remarks>
/// <para>
/// Sections come from self-similarity, which is the classical way and needs no model. Each
/// beat is described by what the spectrum was made of over it, every beat is compared with
/// every other, and a checkerboard kernel is dragged down the diagonal of that comparison:
/// it reads high where the beats before a point resemble each other, the beats after
/// resemble each other, and the two halves do not resemble one another. That is what a
/// section boundary is, expressed as arithmetic.
/// </para>
/// <para>
/// Per beat rather than per hop, using the grid that was already fitted. It makes the
/// comparison a few hundred rows square instead of tens of thousands, and it puts every
/// boundary on a beat for free.
/// </para>
/// </remarks>
internal static class ArrangementAnalyzer
{
    /// <summary>Half-width of the checkerboard, in beats. Eight bars either side.</summary>
    private const int KernelBeats = 32;

    /// <summary>No section shorter than this, in beats — eight bars of four.</summary>
    private const int MinimumSectionBeats = 32;

    /// <summary>A novelty peak this far above the mean, in standard deviations, is a boundary.</summary>
    private const double BoundaryThreshold = 0.8;

    /// <summary>Phrase lengths considered, in bars.</summary>
    private static readonly int[] PhraseLengths = { 8, 16, 32 };

    /// <summary>How much more novelty must fall on a phrase line than on an average bar line.</summary>
    private const double PhraseContrast = 0.25;

    /// <summary>
    /// How much better a longer phrase must look before it is preferred over a shorter one.
    /// A track built in eights also has changes on every sixteenth and thirty-second bar
    /// line, so without a bias the longest candidate wins by having fewer lines to average
    /// over — and a thirty-two-bar reading of an eight-bar record is wrong in a way that
    /// pulls every section boundary out of place with it.
    /// </summary>
    private const double LongerPhraseMargin = 1.3;

    /// <summary>
    /// How far a boundary may be moved to land on a phrase line, in bars. Beyond this it
    /// goes to the nearest bar line instead: the spec asks for boundaries on downbeats and
    /// ideally on phrase lines, and dragging a real change eight bars to fit the phrase
    /// grid is inventing structure rather than finding it.
    /// </summary>
    private const int PhraseNudgeBars = 2;

    /// <summary>At or below this, a section is quiet enough to be an intro, outro or breakdown.</summary>
    private const double QuietEnergy = 0.4;

    /// <summary>At or above this, and with the weight to match, a section is a drop.</summary>
    private const double LoudEnergy = 0.75;

    /// <summary>
    /// Describes a track's arrangement.
    /// </summary>
    /// <param name="timbre">Log energy per band over time.</param>
    /// <param name="timbreRate">Timbre frames per second.</param>
    /// <param name="onsets">Onset strength per hop.</param>
    /// <param name="onsetRate">Onset values per second.</param>
    /// <param name="firstOnsetMs">Where the first onset value sits in the track.</param>
    /// <param name="beats">The beats the grid was fitted to.</param>
    /// <param name="grid">The grid, for bar lines to snap to.</param>
    /// <param name="durationMs">The track's length.</param>
    /// <param name="vocals">Where singing was found, or null when it could not be told.</param>
    /// <returns>The arrangement, or null when the track has no structure worth describing.</returns>
    public static Arrangement? Analyze(
        IReadOnlyList<float[]> timbre,
        double timbreRate,
        IReadOnlyList<float> onsets,
        double onsetRate,
        double firstOnsetMs,
        TrackedBeats? beats,
        BeatGrid? grid,
        double durationMs,
        IReadOnlyList<VocalSpan>? vocals)
    {
        ArgumentNullException.ThrowIfNull(timbre);
        ArgumentNullException.ThrowIfNull(onsets);

        if (beats is null || grid is null || timbre.Count == 0)
        {
            return null;
        }

        var from = beats.First;
        var to = beats.Last;
        var count = to - from + 1;
        if (count < MinimumSectionBeats * 2)
        {
            return null;
        }

        var features = PerBeat(timbre, timbreRate, beats, from, to);
        var loudness = PerBeatLevel(timbre, timbreRate, beats, from, to);
        var density = PerBeatDensity(onsets, onsetRate, firstOnsetMs, beats, from, to);
        var novelty = Novelty(features);

        var (phraseBars, phraseAnchorMs) = Phrase(novelty, beats, from, grid);
        var boundaries = Boundaries(novelty, beats, from, to, phraseBars, phraseAnchorMs);
        var sections = Describe(boundaries, beats, from, to, loudness, density, timbre, timbreRate, durationMs, grid);

        return sections.Count == 0
            ? null
            : new Arrangement(sections, phraseBars, phraseAnchorMs, vocals);
    }

    /// <summary>
    /// One vector per beat, describing what the spectrum was made of over it.
    /// </summary>
    /// <remarks>
    /// Deliberately not normalised to unit length. Doing that measures the shape of a
    /// passage and not its weight, which sounds right — a chorus and the same chorus played
    /// quietly ought to be the same section — and is wrong here: level is most of what
    /// separates a breakdown from the drop either side of it. Normalised, a whole record
    /// came back as two sections. The band energies are already logarithmic, so level
    /// contributes without swamping everything else.
    /// </remarks>
    private static double[][] PerBeat(
        IReadOnlyList<float[]> timbre,
        double timbreRate,
        TrackedBeats beats,
        int from,
        int to)
    {
        var bands = timbre[0].Length;
        var features = new double[to - from + 1][];

        for (var i = from; i <= to; i++)
        {
            var startMs = beats.TimesMs[i];
            var endMs = i < beats.TimesMs.Length - 1 ? beats.TimesMs[i + 1] : startMs + 500;
            var vector = new double[bands];
            var frames = 0;

            var first = (int)Math.Floor(startMs * timbreRate / 1000);
            var last = (int)Math.Ceiling(endMs * timbreRate / 1000);
            for (var f = Math.Max(0, first); f < Math.Min(timbre.Count, last); f++)
            {
                for (var band = 0; band < bands; band++)
                {
                    vector[band] += timbre[f][band];
                }

                frames++;
            }

            if (frames > 0)
            {
                for (var band = 0; band < bands; band++)
                {
                    vector[band] /= frames;
                }
            }

            features[i - from] = vector;
        }

        return features;
    }

    /// <summary>How loud each beat was, before any normalising.</summary>
    private static double[] PerBeatLevel(
        IReadOnlyList<float[]> timbre,
        double timbreRate,
        TrackedBeats beats,
        int from,
        int to)
    {
        var level = new double[to - from + 1];
        for (var i = from; i <= to; i++)
        {
            var startMs = beats.TimesMs[i];
            var endMs = i < beats.TimesMs.Length - 1 ? beats.TimesMs[i + 1] : startMs + 500;
            var first = Math.Max(0, (int)Math.Floor(startMs * timbreRate / 1000));
            var last = Math.Min(timbre.Count, (int)Math.Ceiling(endMs * timbreRate / 1000));

            double sum = 0;
            var frames = 0;
            for (var f = first; f < last; f++)
            {
                foreach (var band in timbre[f])
                {
                    sum += band;
                }

                frames++;
            }

            level[i - from] = frames > 0 ? sum / frames : 0;
        }

        return level;
    }

    /// <summary>How much was starting during each beat.</summary>
    private static double[] PerBeatDensity(
        IReadOnlyList<float> onsets,
        double onsetRate,
        double firstOnsetMs,
        TrackedBeats beats,
        int from,
        int to)
    {
        var density = new double[to - from + 1];
        for (var i = from; i <= to; i++)
        {
            var startMs = beats.TimesMs[i];
            var endMs = i < beats.TimesMs.Length - 1 ? beats.TimesMs[i + 1] : startMs + 500;
            var first = Math.Max(0, (int)Math.Floor((startMs - firstOnsetMs) * onsetRate / 1000));
            var last = Math.Min(onsets.Count, (int)Math.Ceiling((endMs - firstOnsetMs) * onsetRate / 1000));

            double sum = 0;
            var frames = 0;
            for (var f = first; f < last; f++)
            {
                sum += onsets[f];
                frames++;
            }

            density[i - from] = frames > 0 ? sum / frames : 0;
        }

        return density;
    }

    /// <summary>
    /// How much the track changes at each beat, by dragging a checkerboard down the
    /// diagonal of the beat-to-beat similarity.
    /// </summary>
    /// <remarks>
    /// The similarity matrix is never built. Only the band around the diagonal is ever
    /// read, so each beat's score is computed from the couple of thousand comparisons the
    /// kernel actually covers — which keeps a ten-minute track to a few million operations
    /// instead of a few hundred million and a matrix that would not fit anywhere sensible.
    /// </remarks>
    private static double[] Novelty(double[][] features)
    {
        var kernel = Checkerboard(KernelBeats);
        var novelty = new double[features.Length];
        var scale = Scale(features);

        for (var centre = 0; centre < features.Length; centre++)
        {
            double sum = 0;
            for (var i = -KernelBeats; i < KernelBeats; i++)
            {
                var a = centre + i;
                if (a < 0 || a >= features.Length)
                {
                    continue;
                }

                for (var j = -KernelBeats; j < KernelBeats; j++)
                {
                    var b = centre + j;
                    if (b < 0 || b >= features.Length)
                    {
                        continue;
                    }

                    sum += kernel[i + KernelBeats, j + KernelBeats] * Similarity(features[a], features[b], scale);
                }
            }

            novelty[centre] = sum;
        }

        return novelty;
    }

    /// <summary>
    /// A checkerboard with the corners faded off: positive where both offsets are on the
    /// same side of the centre, negative where they straddle it.
    /// </summary>
    private static double[,] Checkerboard(int half)
    {
        var kernel = new double[half * 2, half * 2];
        var width = half / 2.0;

        for (var i = -half; i < half; i++)
        {
            for (var j = -half; j < half; j++)
            {
                var taper = Math.Exp(-((i * i) + (j * j)) / (2 * width * width));
                var sign = Math.Sign(i + 0.5) * Math.Sign(j + 0.5);
                kernel[i + half, j + half] = sign * taper;
            }
        }

        return kernel;
    }

    /// <summary>
    /// How alike two beats are: one where they are identical, falling away as they differ.
    /// </summary>
    private static double Similarity(double[] a, double[] b, double scale)
    {
        double squared = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var difference = a[i] - b[i];
            squared += difference * difference;
        }

        return Math.Exp(-squared / (2 * scale * scale));
    }

    /// <summary>
    /// What counts as "different" for this particular track, taken from how much it varies
    /// over four bars. A record with a wide dynamic range and one that barely moves both
    /// need their changes to stand out against their own background.
    /// </summary>
    private static double Scale(double[][] features)
    {
        const int lag = 16;
        var distances = new List<double>();
        for (var i = 0; i + lag < features.Length; i++)
        {
            double squared = 0;
            for (var band = 0; band < features[i].Length; band++)
            {
                var difference = features[i][band] - features[i + lag][band];
                squared += difference * difference;
            }

            distances.Add(Math.Sqrt(squared));
        }

        if (distances.Count == 0)
        {
            return 1;
        }

        distances.Sort();
        return Math.Max(1e-6, distances[distances.Count / 2]);
    }

    /// <summary>
    /// Finds how many bars a phrase runs to, and where one begins.
    /// </summary>
    /// <remarks>
    /// A mix is counted in phrases, not bars. If a track is built in eights, the changes in
    /// it fall on every eighth bar line and hardly ever on the others — so each candidate
    /// length and offset is scored by how much of the track's novelty lands on the bar
    /// lines it predicts. Where nothing stands out the answer is that the structure would
    /// not commit, which is a real answer for a live recording or anything through-composed.
    /// </remarks>
    private static (int? Bars, double? AnchorMs) Phrase(
        double[] novelty,
        TrackedBeats beats,
        int from,
        BeatGrid grid)
    {
        if (beats.BeatsPerBar is not { } beatsPerBar || grid.Segments.Count == 0)
        {
            return (null, null);
        }

        var downbeats = new List<(int Bar, double Novelty, double TimeMs)>();
        for (var i = 0; i < novelty.Length; i++)
        {
            var number = beats.Numbers[from + i];
            if (((number - beats.Phase) % beatsPerBar + beatsPerBar) % beatsPerBar != 0)
            {
                continue;
            }

            downbeats.Add(((number - beats.Phase) / beatsPerBar, novelty[i], beats.TimesMs[from + i]));
        }

        if (downbeats.Count < PhraseLengths[0] * 3)
        {
            return (null, null);
        }

        var average = downbeats.Average(d => d.Novelty);
        if (Math.Abs(average) < 1e-9)
        {
            return (null, null);
        }

        int? bestBars = null;
        double? bestAnchor = null;
        double bestScore = 0;

        foreach (var bars in PhraseLengths)
        {
            for (var offset = 0; offset < bars; offset++)
            {
                var on = downbeats.Where(d => ((d.Bar - offset) % bars + bars) % bars == 0).ToList();
                if (on.Count < 3)
                {
                    continue;
                }

                var contrast = (on.Average(d => d.Novelty) - average) / Math.Abs(average);
                var required = bestBars is null ? bestScore : bestScore * LongerPhraseMargin;
                if (contrast > required)
                {
                    bestScore = contrast;
                    bestBars = bars;
                    bestAnchor = on[0].TimeMs;
                }
            }
        }

        return bestScore >= PhraseContrast ? (bestBars, Math.Round(bestAnchor!.Value, 1)) : (null, null);
    }

    /// <summary>
    /// Picks the beats where the track changes, and moves each one onto a phrase line where
    /// there is one and a bar line otherwise.
    /// </summary>
    private static List<int> Boundaries(
        double[] novelty,
        TrackedBeats beats,
        int from,
        int to,
        int? phraseBars,
        double? phraseAnchorMs)
    {
        var mean = novelty.Average();
        var deviation = Math.Sqrt(novelty.Sum(v => (v - mean) * (v - mean)) / novelty.Length);
        var floor = mean + (BoundaryThreshold * deviation);

        var picked = new List<int> { 0 };
        for (var i = KernelBeats; i < novelty.Length - KernelBeats; i++)
        {
            if (novelty[i] < floor
                || novelty[i] < novelty[i - 1]
                || novelty[i] < novelty[i + 1]
                || i - picked[^1] < MinimumSectionBeats)
            {
                continue;
            }

            picked.Add(i);
        }

        if (beats.BeatsPerBar is not { } beatsPerBar)
        {
            return picked;
        }

        // On to a phrase line where one is close, and to a bar line otherwise. A section
        // that starts mid-bar is not one a mix can use; a section dragged half a phrase to
        // fit the grid is not one that happened.
        var anchorNumber = beats.Phase;
        if (phraseAnchorMs is { } anchorMs)
        {
            var nearest = 0;
            for (var i = from; i <= to; i++)
            {
                if (Math.Abs(beats.TimesMs[i] - anchorMs) < Math.Abs(beats.TimesMs[nearest + from] - anchorMs))
                {
                    nearest = i - from;
                }
            }

            anchorNumber = beats.Numbers[from + nearest];
        }

        var phraseStride = phraseBars is { } bars ? beatsPerBar * bars : 0;
        var snapped = new List<int>();
        foreach (var at in picked)
        {
            var best = at;
            if (phraseStride > 0)
            {
                best = Nearest(novelty, beats, from, at, phraseStride, anchorNumber, beatsPerBar * PhraseNudgeBars);
            }

            if (best == at)
            {
                best = Nearest(novelty, beats, from, at, beatsPerBar, beats.Phase, beatsPerBar);
            }

            if (snapped.Count == 0 || best - snapped[^1] >= MinimumSectionBeats)
            {
                snapped.Add(best);
            }
        }

        if (snapped.Count == 0 || snapped[0] != 0)
        {
            snapped.Insert(0, 0);
        }

        return snapped;
    }

    /// <summary>
    /// The nearest beat to <paramref name="at"/> that sits on a line of the given stride,
    /// or <paramref name="at"/> itself when none is close enough.
    /// </summary>
    private static int Nearest(
        double[] novelty,
        TrackedBeats beats,
        int from,
        int at,
        int stride,
        int anchorNumber,
        int reach)
    {
        var best = at;
        var bestDistance = reach + 1;

        for (var i = Math.Max(0, at - reach); i <= Math.Min(novelty.Length - 1, at + reach); i++)
        {
            if (((beats.Numbers[from + i] - anchorNumber) % stride + stride) % stride != 0)
            {
                continue;
            }

            var distance = Math.Abs(i - at);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Turns boundaries into sections with an energy and, where the evidence allows, a name.
    /// </summary>
    private static List<Section> Describe(
        List<int> boundaries,
        TrackedBeats beats,
        int from,
        int to,
        double[] loudness,
        double[] density,
        IReadOnlyList<float[]> timbre,
        double timbreRate,
        double durationMs,
        BeatGrid grid)
    {
        var count = to - from + 1;
        var raw = new double[boundaries.Count];
        var weight = new double[boundaries.Count];
        var rising = new bool[boundaries.Count];

        for (var i = 0; i < boundaries.Count; i++)
        {
            var start = boundaries[i];
            var end = i == boundaries.Count - 1 ? count : boundaries[i + 1];
            raw[i] = (0.6 * Mean(loudness, start, end)) + (0.4 * Mean(density, start, end));
            weight[i] = Bottom(timbre, timbreRate, beats, from + start, from + Math.Min(count - 1, end - 1));

            // Whether the section climbs across itself, which is what actually makes a
            // build a build rather than just something quieter before something louder.
            var middle = start + ((end - start) / 2);
            var before = (0.6 * Mean(loudness, start, middle)) + (0.4 * Mean(density, start, middle));
            var after = (0.6 * Mean(loudness, middle, end)) + (0.4 * Mean(density, middle, end));
            rising[i] = after > before * 1.05;
        }

        // Normalised within the track, so the loudest part of a quiet record still reads as
        // its loudest part.
        var lowest = raw.Min();
        var highest = raw.Max();
        var span = highest - lowest;
        var energy = raw.Select(v => span > 1e-9 ? (v - lowest) / span : 0.5).ToArray();

        var heaviest = weight.Length == 0 ? 0 : weight.Max();
        var kinds = Name(energy, weight, rising, heaviest);

        var sections = new List<Section>(boundaries.Count);
        for (var i = 0; i < boundaries.Count; i++)
        {
            // On to a bar line of the published grid rather than on to the tracked beat the
            // boundary was found at. The two are a few milliseconds apart when the tracker
            // ran true and a beat or three apart when it did not, and a client reading
            // "this section starts at a downbeat" has only the grid to check that against.
            var startMs = i == 0 ? 0 : Bar(beats.TimesMs[from + boundaries[i]], grid);
            var endMs = i == boundaries.Count - 1
                ? Math.Max(durationMs, beats.TimesMs[to])
                : Bar(beats.TimesMs[from + boundaries[i + 1]], grid);

            sections.Add(new Section(
                Math.Round(startMs, 1),
                Math.Round(endMs, 1),
                kinds[i],
                Math.Round(energy[i], 2)));
        }

        return sections;
    }

    /// <summary>
    /// Names what can be named from level, weight and position, and admits the rest.
    /// </summary>
    private static string[] Name(double[] energy, double[] weight, bool[] rising, double heaviest)
    {
        var kinds = new string[energy.Length];
        for (var i = 0; i < energy.Length; i++)
        {
            var quiet = energy[i] <= QuietEnergy;
            var loud = energy[i] >= LoudEnergy;
            var heavy = heaviest > 1e-9 && weight[i] >= heaviest * 0.8;

            // Everything before the music starts properly is the intro, and everything
            // after it stops is the outro — not just the first and last sections. A record
            // that opens on silence and then a pad has two quiet sections before anything
            // happens, and calling the second of them a build because a drop follows it
            // eventually is not a description anybody would recognise.
            if (quiet && energy.Take(i + 1).All(e => e <= QuietEnergy))
            {
                kinds[i] = SectionKind.Intro;
            }
            else if (quiet && energy.Skip(i).All(e => e <= QuietEnergy))
            {
                kinds[i] = SectionKind.Outro;
            }
            else if (loud && heavy)
            {
                kinds[i] = SectionKind.Drop;
            }
            else if (i > 0 && i < energy.Length - 1
                     && energy[i - 1] - energy[i] > 0.15
                     && energy[i + 1] - energy[i] > 0.15)
            {
                // Quieter than both of its neighbours by a clear margin. Relative rather
                // than against a fixed level, because a record with a narrow dynamic range
                // still has a breakdown in it.
                kinds[i] = SectionKind.Breakdown;
            }
            else if (quiet && i > 0 && i < energy.Length - 1 && energy[i - 1] > energy[i] && energy[i + 1] > energy[i])
            {
                kinds[i] = SectionKind.Breakdown;
            }
            else
            {
                kinds[i] = SectionKind.Unknown;
            }
        }

        // A build sits between something quieter and something louder, and usually climbs
        // across itself as well. The first half of that is what separates it from a
        // breakdown, which sits between two louder things — without it every quiet stretch
        // before a drop read as a build, and a breakdown is where a client would most like
        // to bring a record in.
        for (var i = 0; i < kinds.Length - 1; i++)
        {
            if (kinds[i] != SectionKind.Unknown || energy[i + 1] - energy[i] <= 0.15)
            {
                continue;
            }

            var notFallingInto = i == 0 || energy[i] >= energy[i - 1] - 0.05;
            if (notFallingInto || rising[i])
            {
                kinds[i] = SectionKind.Build;
            }
        }

        return kinds;
    }

    /// <summary>
    /// The nearest bar line of the published grid to a moment.
    /// </summary>
    private static double Bar(double ms, BeatGrid grid)
    {
        if (grid.Segments.Count == 0 || grid.BeatsPerBar is not { } beatsPerBar)
        {
            return ms;
        }

        var segment = grid.Segments.FirstOrDefault(s => ms >= s.StartMs && ms <= s.EndMs) ?? grid.Segments[0];
        var barMs = beatsPerBar * 60000 / segment.Bpm;
        var bars = Math.Round((ms - segment.AnchorMs) / barMs);
        return segment.AnchorMs + (bars * barMs);
    }

    /// <summary>How much of a section sits in the bottom third of the spectrum.</summary>
    private static double Bottom(
        IReadOnlyList<float[]> timbre,
        double timbreRate,
        TrackedBeats beats,
        int firstBeat,
        int lastBeat)
    {
        if (timbre.Count == 0 || firstBeat > lastBeat)
        {
            return 0;
        }

        var bands = Math.Max(1, timbre[0].Length / 3);
        var first = Math.Max(0, (int)(beats.TimesMs[firstBeat] * timbreRate / 1000));
        var last = Math.Min(timbre.Count, (int)(beats.TimesMs[lastBeat] * timbreRate / 1000) + 1);

        double sum = 0;
        var frames = 0;
        for (var f = first; f < last; f++)
        {
            for (var band = 0; band < bands; band++)
            {
                sum += timbre[f][band];
            }

            frames++;
        }

        return frames > 0 ? sum / frames : 0;
    }

    private static double Mean(double[] values, int from, int to)
    {
        double sum = 0;
        var count = 0;
        for (var i = from; i < Math.Min(to, values.Length); i++)
        {
            sum += values[i];
            count++;
        }

        return count > 0 ? sum / count : 0;
    }
}
