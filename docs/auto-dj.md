# Auto DJ

Auto DJ mixes one record into the next the way a DJ would: on the beat, on a phrase
line, with the bass handed over rather than faded, and with the queue chosen so that
neighbours suit each other. This document covers the whole feature, server and clients,
in one place. The field-by-field contracts are in
[client-integration.md](client-integration.md); this is the design around them.

> **Status: shelved since 2026-10-22.** Auto DJ is switched off and hidden in both apps.
> Nothing was deleted, and the tests still run. [Why it was shelved](#why-it-was-shelved)
> and [what would bring it back](#what-would-bring-it-back) are at the end.

## Contents

- [What it is](#what-it-is)
- [How it fits together](#how-it-fits-together)
- [What the sidecar measures](#what-the-sidecar-measures)
- [Choosing the records](#choosing-the-records)
- [Planning a transition](#planning-a-transition)
- [Programming the set](#programming-the-set)
- [When a pair cannot be mixed](#when-a-pair-cannot-be-mixed)
- [The decks](#the-decks)
- [Why it was shelved](#why-it-was-shelved)
- [What would bring it back](#what-would-bring-it-back)
- [Turning it back on](#turning-it-back-on)
- [Where the code is](#where-the-code-is)

## What it is

The apps can be three things:

| Mode | For | What it does |
| --- | --- | --- |
| **Crossfade** | Listening without gaps | Every record, all of it, faded into the next |
| **Infinity + Crossfade** | Listening that never ends | The same, and the queue keeps going |
| **Auto DJ** | A room | Records mixed where they suit each other, played for their hook rather than to the fade-out; Infinity is implied |

Auto DJ is the one that plays *less* of each record: about 60%, because a room hears the
first hook, maybe a second chorus, and moves on.

It is not a mode that mixes every pair. A transition is mixed only when the measurements
say it will work; otherwise it is cut, thrown with an echo, or crossfaded. A player that
beat-matches when it can and does something honest when it cannot is one people trust.

## How it fits together

Three stages, and only the first involves measurement of the audio:

1. **Analysis, on the server, once per track.** The sidecar decodes each file once and
   measures it: where its sound starts and stops, loudness, tempo, a beat grid, meter,
   mix points, key, sections and vocal spans. All of it is signal processing; there is no
   trained model.
2. **Planning, by rules.** For each pair, decide whether to mix, where the outgoing record
   leaves, where the incoming one enters, how long the overlap is, and which part swaps
   when. The rules are a small set of known DJ moves, each written down and each testable.
3. **Playback, in the apps.** Two decks on one audio clock play the plan: volume, filters,
   tempo and an echo, scheduled to the sample.

The rules live in the clients and are ported to the sidecar, which uses them to rank
records by how well they follow each other. Three ports (Swift, TypeScript, C#) are held
together by shared parity tables, so the server's opinion of a pair is the decks' plan for
it.

## What the sidecar measures

Every measurement comes from the same decode. Contracts, field by field, are in
[client-integration.md](client-integration.md).

| Measurement | Endpoint | What Auto DJ uses it for |
| --- | --- | --- |
| Sound bounds | `GET /aoide/sound-bounds` | Where a record's sound actually starts and stops |
| Loudness, tempo, tempo stability | `GET /aoide/audio-analysis` | Levelling both decks to the same loudness; whether a tempo describes the whole track |
| Beat grid, meter, mix points, key | `GET /aoide/beat-grid` | Landing beats and bars on each other; where a mix may begin and end |
| Sections, phrase grid, vocals | `GET /aoide/arrangement` | Mixing on phrase lines, out of a loud part, and never two singers at once |
| Coverage | `GET /aoide/analysis/coverage` | How much of the library is ready |

The parts that decide whether a mix is attempted:

- **The grid is a fit, not a list of beats.** Each segment has an anchor, a tempo and a
  residual: how far the detected onsets sit from the fitted beats. A sixteen-bar blend at
  128 BPM is thirty seconds, and to stay inside a twentieth of a beat across it the grid
  has to be good to about 20 ms. `residualMs` is what tells a client to refuse.
- **A track that changes tempo comes back in segments**, and a mix never crosses a
  segment boundary. Segments cover the track end to end.
- **Every segment's beat zero is a downbeat** when the meter is known, so
  `bar(n) = anchorMs + n × beatsPerBar × 60000 / bpm` holds in every segment.
- **Mix points need every bar of a four-bar phrase to be grooving**, so an atmospheric
  intro is never taken for the start of the groove.
- **Sections are measured, not guessed.** Kinds are `intro`, `build`, `drop`,
  `breakdown`, `outro` and `unknown`. `verse` and `chorus` are never returned, because
  telling them apart is a judgement about song form that the signal does not settle.
  `energy` is normalised within the track.
- **Vocals have three states.** A list of spans, an empty list (none found), or `null`
  (could not tell). Clients treat `null` as singing throughout: a false "singing" costs a
  mix, a false "silent" puts two voices on top of each other.
- **The key is a preference, never a refusal.** It is weakest between a key and its
  relative major or minor and has not been tested at scale on real music.

## Choosing the records

With Auto DJ on, Infinity is on, and Infinity asks the sidecar what plays next:
`POST /aoide/next` with `mode: "autodj"`. The full rule is in
[client-integration.md](client-integration.md#what-plays-next); in short:

**Choosing**, from the whole library rather than a sample of it:

```
score = taste
      + kinship    × 0.6
      + similarity × 0.4
      + arc        × 0.2
      − 1.4 if heard lately
      − 0.4 if by an artist heard lately
```

- **Kinship** is whether the candidate is the seed's kind: 1 when the first genres match,
  0.5 for any other shared genre, 0 when both are tagged and nothing is shared, 0.5 when
  either is untagged. It is the largest term after taste, because what plays next is
  mostly a record of the same kind.
- **Similarity** is tempo, key and energy against the seed.
- **Arc** steers energy across the last few records, so a set rises and rests rather than
  sitting at one level.

**Mixability is not part of choosing.** An earlier version included it, and with Auto DJ
on the set was made of whatever mixed: mediocre records in the same key beat good ones
that could only be crossfaded.

**Ordering**, `autodj` mode only: the chosen records are put in the order they should
play. Starting from the end of the queue (or the seed), each next record is the one the
planner scores highest out of the last. A pair the planner refuses or cannot read counts
0.15, the crossfade figure, so an unread record goes wherever nothing better fits. The
`mixability` reported on each row is from the record before it in that order.

The apps show `mixability` at or under 0.15 as *Crossfade*, and `null` (could not be
read) as nothing.

## Planning a transition

The planner turns two records' measurements into a plan, or into nothing.

### The score sets the length

`MixScore` grades five things and weighs them:

| Part | Weight |
| --- | --- |
| Tempo (free within 2%, gone at 6%) | 0.2 |
| Key (same, adjacent or relative on the Camelot wheel) | 0.2 |
| Vocals (who is singing during the overlap) | 0.15 |
| Energy at the exit and the entry | 0.2 |
| What kind of section each side is in | 0.25 |

The total earns **32 bars at 0.75, 16 at 0.55, 8 at 0.35**, and below that no mix. A pair
with nothing known against it gets sixteen; thirty-two has to be earned.

The score also chooses **where the incoming record comes in**: its mix-in point, or the
start of any build, intro or breakdown, whichever scores best. Coming in on a build is
what a DJ does; the mix-in point is only where the groove starts.

### Rules the score cannot outvote

- **A record that has not been read is not mixed.** Matching tempo is not a substitute for
  knowing the arrangement; an unread pair crossfades.
- **Two records singing at once is refused.**
- **A drop onto a drop is capped at eight bars.**
- **A known key clash is capped at eight bars.**
- **A filter fade is capped at eight bars.** Thirty-two bars of a voice being slowly
  filtered under another record is thirty-two bars of clash.

### Phrase lines

A mix starts on a phrase line, not just a bar line, on both records. A moment moves onto a
phrase line **only when one is within two bars**; otherwise it stays on its bar line. An
exit is the end of a hook, and snapping it back to the previous phrase line could move it
seven bars into the hook.

### Tempo

- **Only the incoming record bends.** The record already playing is the one the room is
  hearing.
- **The bend is capped at ±6%**, after folding the ratio into one octave: a 170 BPM record
  over an 85 BPM one is not slowed, because its beats already land on every other beat.
- **Pitch is held** while the tempo moves. Bending 170 BPM to 164 by speed alone would put
  the record most of a semitone flat against the other one.
- **The bend eases off** over eight seconds after the mix, so the set does not drift to
  the tempo of whatever came first.

### The hand-over

- **Volume** follows an equal-power curve, so the pair never dips or piles up.
- **The bass swaps; it does not fade.** A second-order high-pass on the incoming record
  sweeps from 20 Hz to 220 Hz on a log scale and hands the low end over at the halfway
  bar, in about a bar. Two kick drums at once is the muddiest sound there is, and no
  volume curve fixes it. Measured: 60 Hz is 22 dB down on the record that has given up
  its bass, and the level through the mix holds within a decibel.
- **The incoming record comes up over half the mix**, the way a DJ raises the new drums
  and top under the old record over eight to sixteen bars.
- **A filter fade hides what would clash.** When the keys clash or either record is
  singing across the overlap, the outgoing record's high-pass sweeps up to 1.5 kHz over
  the first half of the mix. Bass, chords and the body of a voice go; hats, snare and air
  stay. The two melodies are never on top of each other. Pairs with nothing to hide keep
  the plain blend: a filter on every hand-over is how a robot announces itself.

## Programming the set

The set programmer decides when a record leaves.

- It aims to play **about 60%** of a record: never less than 90 seconds, never more than
  five minutes.
- It leaves at a section boundary where **a loud part ends** (a drop into a breakdown or an
  outro), on a phrase line, no earlier than 40% in and no later than the record's own
  mix-out point. A record with no drop plays out.
- **The exit is chosen for the pair.** Every hook end inside the window is offered, best
  first, and the planner tries each against every entry. The first choice stands unless
  another exit scores at least 0.1 better.
- **An escape leaves late.** When the next record cannot be mixed in, there is no reason to
  leave early: the exit becomes the end of the *last* hook.

## When a pair cannot be mixed

A pair that has been read but cannot be mixed (tempi too far apart, or both singing) is
not crossfaded, because for two singers a crossfade is the vocal clash by another route.

- **Cut.** The outgoing record stops on the phrase line at its exit; the incoming one starts
  on the phrase line at its entry, at its own tempo, that instant.
- **Echo-out ("throw"), from something loud into something quieter.** The outgoing record
  stops on its phrase line and its last hit repeats: a delay of one beat of its own tempo,
  65% feedback, the first repeat held at −4.4 dB under the word. One bar of nothing else
  follows; the new record lands on the next downbeat while the fourth repeat is still
  fading. The echo is mid-only: a 3 kHz low-pass inside the delay and a high-pass at
  200 Hz after it, swept there across the two beats before the cut, so the tail never holds
  a kick and has no hats.
- **Filtered cut**, when no line in reach is clean enough to echo: the top is rolled off
  from 20 kHz to 300 Hz over the last two beats, then the record stops on the line and the
  new one comes in dry.

**Lyrics place the cut.** With the record's synced lyrics loaded, a cut lands on the latest
phrase line inside the hook that is not mid-line: within 400 ms before a line starts, in a
rest of more than eight seconds, or after the last line. The vocal spans decide only when
there are no lyrics. The new record lands **on its first line** when that comes within
sixteen bars of its mix-in, so it answers the echo rather than starting mid-thought.

A pair that has not been read still crossfades. A cut is a decision about two records, and
a decision needs to know them.

## The decks

**iOS.** Two players in one `AVAudioEngine`, so one render clock addressed by sample:

```
player → time-pitch → high-pass → delay ─┐
                                         ├→ main mixer → output
player → time-pitch → high-pass → delay ─┘
```

A queue player starts a track to within tens of milliseconds; a beat at 128 BPM is
469 ms, so 20 ms is an audible flam. The decks start to the sample. The delay is silent
except during an echo-out.

**Desktop.** The same plan on the Web Audio API, with Signalsmith Stretch (MIT) for tempo
changes that hold pitch.

Things learned the hard way:

- **The decks take every read record from its beginning**, whatever its hand-over turns
  out to be. Moving a record onto the decks mid-play ran two engines on the same instant
  of the same file a few milliseconds apart, which is a comb filter.
- **A join on two decks is a better gapless than a queue player's**: one clock, one
  scheduled sample.
- **Files only.** A mix that might stall halfway is not a mix. With Auto DJ on, the next
  record or two are fetched to a cache ahead of time; a record that has not arrived when
  it is needed crossfades.
- **The engine stops when the hardware changes** (headphones, a different sample rate) and
  is torn down by an interruption such as a call, so both are restarted where the session
  says the record had reached.

**Measured, not asserted.** The decks have no dependency on the audio session, so the
whole engine renders offline on a Mac and the output is measured: every deck starts with
the same 1.1 ms latency (common to both, so it cancels), two decks land within 2 ms of
each other, a bend of 1.05 turns 500 ms clicks into 476 ms ones, and the filters take
what they claim to.

## Why it was shelved

Every round above made one mechanism measurably right, and the whole still did not sound
like a DJ. The ceiling was not a missing rule:

- **No stems.** A DJ can take the vocal or the drums of one record on their own. The decks
  can only filter the whole mix, so two vocal records can never be blended and most pairs
  end in a cut or a throw.
- **Coarse measurements.** Sections and vocal spans are blocks of time, not the song as a
  listener hears it; the rules were guessing from them.
- **The library.** Mixing works best on club records: steady tempo, long instrumental intros
  and outros. Vocal pop and rock at different tempi and keys is the hardest case there is.

Still in use from the work: Infinity choosing from the whole library on the server, Share
Diagnostics, and the clock and booking fixes in the decks.

## What would bring it back

### Stems

Per-track stems, separated on the server, so a throw can be the vocal alone and a blend
can keep one record's band under the other's voice. The plan, parked on 2026-10-23:

- **Vocals: Mel-Band RoFormer** (Kim's checkpoint). About 11.0 dB vocal SDR on MVSEP's
  multisong benchmark against about 8.8 for htdemucs, and its weights are tagged MIT.
- **Drums and bass: Demucs** (`htdemucs`), run on the original minus the vocal rather than on
  the original, so a voice leaking into the drums is not subtracted twice. "Other" is what
  is left, so the parts always add back to the original exactly.
- **Demucs weights are not redistributed** until their licence is settled. The code is
  MIT, but its maintainer has said the weights are "not covered by the MIT license" and
  are for scientific use; the newer Hugging Face card says MIT and the question of which
  holds is unanswered. The separator would download them onto the server at first run, as
  the Demucs package itself does; no release or image would carry them.
- **Only the vocal needs storing** for the first round. Clients play the original and the
  stem together on one clock and set the vocal level by adding the stem at a gain:
  `heard = original + (g − 1) × vocals`, so `g = 0` removes the voice and `g = 0.3` is a
  karaoke level. That needs the stem lossless (FLAC), at the source's own sample rate and
  channels, sample for sample the same length. Most libraries that are mostly FLAC meet
  that; a lossy file whose decoders disagree on padding refuses the stem on a length
  mismatch.
- **Where it runs: a separate container beside Jellyfin**, not inside the plugin. Jellyfin's
  image has no CUDA libraries, the ONNX Runtime GPU package alone is 233 MB, and every
  working export of these models leaves the signal processing outside the graph to be
  rewritten. The container would run PyTorch 2.7 or later (the first with RTX 50-series
  support) with `python-audio-separator` (MIT), take jobs as files in a shared folder,
  expose no network service, and read the music read-only. A CPU-only fallback to
  benchmark is BSRoformer.cpp.
- **Before building:** run both models on about ten real tracks, measuring seconds per
  track, GPU memory alongside transcoding, how far original minus vocal cancels a known
  vocal (target −40 dB), and how it sounds at 30%.

Stems also give the apps a karaoke mode on the way: the vocal at about 30% with the synced
lyrics on screen.

### Rules that fit a pop and rock library

From how DJs and commercial auto-mixers handle large tempo gaps:

- **Gate on the tempo gap.** Under about 8%, beat-match. About 8–30%, ramp both records
  toward the middle across the transition, preferring slowing down, and return to 1.0
  afterwards. Beyond that, do not beat-match at all.
- **The default transition is the vocal throw**: the outgoing singer's last line echoed
  alone over the incoming record's band, then a cut on a phrase. Nothing has to stay
  beat-locked, and short intros do not matter.
- **Never pitch-shift a vocal by more than about a semitone.** Shift only the band, by two
  semitones at most, or do not key-match.
- **Stretching limits:** Signalsmith Stretch sounds best between about 0.75× and 1.5×.
  `AVAudioUnitTimePitch` has been reported to flutter when speeding up; test it above 1.0×
  before relying on it there.
- **Separating on the phone is not the way.** Open models that run in real time are 4–5 dB
  worse than offline ones; only proprietary engines do it well live.

### Learning from listening

The rules stay rules; listening adjusts their weights. If skips follow a certain kind of
transition, that transition should score lower. That needs the transition recorded: a play
event today holds completed, skipped, time played, source and activity, but not how the
record began. Recording the mix style, length and score on the play event that follows is
a small client change, and the data has to start accumulating before there is anything to
learn from.

## Turning it back on

Two flags, both `false`:

- iOS: `AutoDJPreference.isAvailable`
- desktop: `AOIDE_AUTO_DJ_AVAILABLE`

With them off, the switch is not shown and the setting reads as off whatever an old install
stored, so the decks, the planner, the set programmer and the grid and arrangement fetches
all stay idle. Setting either to `true` brings it all back. The sidecar needs no change:
its measurements and the `autodj` mode of `/aoide/next` are always available.

## Where the code is

**Sidecar** (this repository):

| Part | Code | Tests |
| --- | --- | --- |
| One decode, many measurements | `Sound/PcmPump.cs`, `Sound/AudioMeasurer.cs` | `AudioAnalysisServiceTests` |
| Tempo and stability | `Sound/TempoAnalyzer.cs` | `TempoAnalyzerTests` |
| Beat grid, meter, mix points | `Sound/BeatTracker.cs`, `Sound/BeatGridAnalyzer.cs` | `BeatGridTests` |
| Key | `Sound/ChromaScan.cs` | `ChromaScanTests` |
| Sections, phrases, vocals | `Sound/ArrangementAnalyzer.cs`, `Sound/VocalScan.cs` | `ArrangementTests` |
| The planner (port) | `Next/DJPlanner.cs`, `Next/MixScore.cs`, `Next/GridMath.cs`, `Next/CamelotKey.cs` | `DJPlannerParityTests` (41 pairs) |
| Choosing and ordering | `Next/NextRanker.cs`, `Next/PlannerMixability.cs` | `NextRankingFixtureTests`, `NextRankerTests` |

**iOS app**, in `PlaybackKit`: the decks (`DJMixer`), the planner (`DJTransition`,
`MixScore`, `BeatGrid`), the set programmer (`SetProgrammer`) and the session that drives
them (`DJSession`).

**Desktop app** ([aoide-desktop](https://github.com/Virel0/aoide-desktop)): the planner in
`src/shared/aoide/` (`dj-planner.ts`, `mix-score.ts`, `mix-transition.ts`), and the decks,
schedule and prefetch in `src/renderer/aoide/features/playback/` (`buffer-deck.ts`,
`dj-schedule.ts`, `use-mix-transition.ts`, `use-dj-prefetch.ts`, `auto-dj.ts`).

The planner parity table and the next-ranking fixture are shared by all three.
