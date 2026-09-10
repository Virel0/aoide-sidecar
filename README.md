# Aoide Sidecar

A Jellyfin plugin that gives a music app the things Jellyfin's music model is bad at:
playlists that sync between devices, collaborative playlists, real play history,
resume-across-devices, playlist artwork, silence trimming for gapless playback, loudness
and tempo for a library mastered decades apart, and a matcher for importing playlists
from elsewhere.

It is the server half of [Aoide](#the-aoide-apps). Jellyfin stays the source of truth
for files, tags, artwork and which music exists at all. The sidecar owns everything
Jellyfin does not: it relays an append-only op log between a user's devices and does
almost nothing else on purpose.

**It is deliberately dumb.** It does not merge, resolve conflicts, evaluate smart-playlist
rules, or understand what a playlist is. All of that happens on the client, against local
SQLite. Offline is not a degraded mode; it is the normal mode that sometimes also syncs.

## What it does

| feature | endpoint | since |
| ------- | -------- | ----- |
| Sync the curation store between a user's devices | `POST /aoide/sync/push`, `GET /aoide/sync/pull` | 1.0.0.0 |
| Storage diagnostics, accepted entities, plugin version | `GET /aoide/sync/status` | 1.0.2.0 |
| Playlist artwork, content-addressed | `PUT`/`GET`/`HEAD /aoide/images/{sha256}` | 1.1.0.0 |
| Mirror playlists into Jellyfin's own, one-way | `POST /aoide/export/playlists` | 1.2.0.0 |
| Artwork housekeeping | `GET /aoide/images/orphans`, `POST …/reclaim` | 1.4.0.0 |
| Play-history retention | `GET /aoide/retention`, `POST …/prune` | 1.5.0.0 |
| Collaborative playlists between Jellyfin users | `GET`/`POST`/`DELETE /aoide/shares` | 1.6.0.0 |
| Resume playback on another device | `GET /aoide/queue` | 1.7.0.0 |
| Match an imported track list against the library | `POST /aoide/match` | 1.8.0.0 |
| Where each track's sound starts and stops | `GET`/`POST /aoide/sound-bounds` | 1.10.0.0 |
| How loud each track is and how fast | `GET`/`POST /aoide/audio-analysis` | 1.11.0.0 |
| Whether a track keeps its tempo | `bpmStability` on the same endpoint | 1.12.0.0 |

Two scheduled tasks exist and both are **off by default**, switchable on the plugin's
configuration page: a nightly playlist export, and a library-wide analysis sweep (hours
of decoding on a large library; tracks are measured on request regardless).

## Requirements

- Jellyfin **10.11.x**. The plugin targets `Jellyfin.Controller` 10.11.11 on .NET 9 and
  ships as a single DLL.
- The bundled ffmpeg Jellyfin already has — used for every audio measurement, found
  through Jellyfin's own encoder path rather than `PATH`. Nothing else needs installing.

Every endpoint takes a normal Jellyfin **user** token. An admin API key returns 401: it
carries no user id, and everything here is scoped to a user.

## Install

This repository doubles as a Jellyfin plugin repository. Add it once, in
Dashboard → Plugins → Repositories → **+**:

```
https://raw.githubusercontent.com/Virel0/aoide-sidecar/main/manifest.json
```

The plugin then appears under Dashboard → Plugins → Catalog. Install it and restart
Jellyfin. Every later version is a click in the same place.

Confirm it loaded under Dashboard → Plugins, and look for
`Aoide sync database ready at /config/data/aoide-sidecar/aoide-sync.db` in the log.

The database lives under Jellyfin's data folder inside the config volume — deliberately
not the plugin's own versioned folder, which changes on every upgrade — so it is covered
by whatever already backs that volume up. It is the only copy of a user's curation
history that is not on a device.

### Verify it works

```bash
JELLYFIN_URL=https://jellyfin.example.com JELLYFIN_TOKEN=<user token> ./scripts/verify.sh
```

Exercises the whole contract against the live server: plugin loaded, token accepted, an
op pushed, pulled back with its sequence, re-pushed to prove idempotency, a bad op
rejected without taking the good op in its batch down with it, and an unauthenticated
request refused. It writes real ops, which are append-only — run it before any device
has synced, and it prints the commands to wipe the database afterwards.

## For app developers

[docs/client-integration.md](docs/client-integration.md) is the contract in full: the
sync loop, cursors, every rule that gets an op rejected, the failure modes that are
silent rather than loud, and each feature's wire shape. Per-release notes are in the
[GitHub releases](https://github.com/Virel0/aoide-sidecar/releases).

Clients discover what a server accepts from `GET /aoide/sync/status`, which lists
`acceptedEntities` and `pluginVersion`. Hold ops for an entity the server does not yet
list rather than pushing them; the entity list is an allow-list, and a rejected op is
otherwise final.

## What the server guarantees

**Ordering.** SQLite permits one write transaction at a time, so commits are totally
ordered, and `seq` is assigned inside the writing transaction. A reader that observes
sequence N is guaranteed to already see everything below it, which is what makes a
monotonic cursor safe: a puller can never skip an op that was still in flight.
Rolled-back transactions burn a sequence, so the log has gaps; the cursor means
"everything up to here", not "the next number".

**Idempotency.** `(user_id, op_id)` is unique. Re-pushing is accepted and ignored, which
is what makes a retry after a timeout safe. Scoped by user rather than globally because
op ids are client-generated, and a shared namespace would let one account void
another's op by pushing the same id first.

**Durability.** `synchronous=FULL`, one fsync per push batch. A client marks its ops
synced on the strength of a push response and will not send them again, so a commit
lost to power failure is lost user data.

**Opacity.** Payloads are stored verbatim and never parsed on the sync path; only the
envelope is validated. That is what lets the client schema gain a column without a
coordinated server deploy. The two places the server does read a payload — the
playlist id, for routing a collaborator's edits, and the playlist projection behind
export — read one field each and accept both naming conventions.

**Bounded depth.** No stored payload nests deeper than 32 levels. A JSON decoder refuses
an over-nested document whole rather than element by element, so one pathological row
would otherwise wedge inbound sync on every device with no seam to skip it. An op past
the limit is refused on its own, leaving the rest of its batch intact.

**Clock skew.** Every op carries a server `receivedAt` beside the client's `createdAt`,
so a device with a badly wrong clock cannot win every conflict forever. Conflict
resolution itself is the client's job.

**Per-op rejection.** An op that fails validation is reported in `rejected` with a
reason; valid ops in the same batch still land. Failing the whole batch would let one
malformed op wedge a queue forever, and dropping it silently would lose it without a
trace.

## Design limits, stated plainly

- **Nothing destructive runs on its own.** Play-history pruning and artwork reclamation
  are manual, report before they act, and their age parameters can only make a sweep
  more cautious. A device that has *never* synced still receives a shortened history
  after a prune — no cursor can protect a device the server has never met.
- **Export is one-way and structurally so.** Jellyfin playlists are a mirror. Edits made
  in Jellyfin's UI are overwritten; every exported playlist is stamped with a provider
  id, and the stamp is the only thing export will ever delete.
- **Sound bounds match the phone exactly for lossless files.** The measurement is the
  client's algorithm ported line for line over ffmpeg-decoded PCM. For lossy files the
  two decoders can differ by samples, inside the trim margins.
- **A file is decoded once for all of it.** Sound bounds, loudness and tempo come out of
  a single ffmpeg pass, so asking for any one of them fills the cache for the others.
- **Tempo is an estimate and says so.** Loudness is ffmpeg's own R128 measurement and is
  exact. Tempo is multi-band spectral flux autocorrelated in managed code — within
  0.2 BPM across 62 to 198 on synthesised beats, silent on music without a beat, and still
  liable to report half or double on real music. Every answer carries a confidence, and
  anything under 0.5 is not reported at all.
- **A tempo is not a beat grid.** There is no phase: the server says how far apart the
  beats are, never where they fall. `bpmStability` says whether a fixed grid would fit at
  all, which for anything played by people it usually would not.
- **One server process.** The store is SQLite in WAL mode; it is not designed for
  several Jellyfin instances sharing one database.

## Development

```bash
dotnet test
```

```bash
./scripts/package.sh 1.0.0.0
```

`Microsoft.Data.Sqlite` is referenced with `ExcludeAssets="runtime"` because the server
already loads it via EF Core, and a second copy would arrive with its own `SQLitePCLRaw`
that has no native provider registered against it.

Changes are verified against a real Jellyfin in Docker before release, not only against
unit tests — several bugs in this project's history were invisible to tests and obvious
on a running server (PascalCase responses, a provider stamp that vanished on update, an
empty GUID that failed a whole batch). Something like:

```bash
docker run -d --name jf -p 8096:8096 -v "$PWD/rig/config:/config" -v "$PWD/rig/media:/media" jellyfin/jellyfin:10.11.11
```

then complete the setup wizard over the API, copy `artifacts/Aoide Sidecar_<version>`
into `rig/config/plugins/`, and restart the container. Stop Jellyfin before replacing a
plugin DLL in place: overwriting a mapped file corrupts the running process's copy, and
Jellyfin will mark the plugin malfunctioned and remove it.

### Releasing

```bash
./scripts/release.sh 1.0.1.0 "What changed"
```

Runs the tests, builds, tags, uploads the zip to GitHub Releases, and rewrites
`manifest.json` with the new version and its MD5. Jellyfin verifies that checksum on
download, so the manifest must always be updated by this script rather than by hand.
Versions are four-part because Jellyfin parses them as `System.Version`.

### Installing by hand

```bash
./scripts/package.sh 1.0.0.0 && scp -r "artifacts/Aoide Sidecar_1.0.0.0" user@server:/path/to/jellyfin/config/plugins/
```

```bash
ssh user@server 'docker compose -f /path/to/docker-compose.yml restart jellyfin'
```

The plugin folder must keep the `Name_Version` shape with the DLL directly inside it —
the layout Jellyfin's own installer produces.

## The Aoide apps

Aoide is a music player for Jellyfin, on iOS and desktop, built around a local-first
curation store. The apps hold the full contract in their own repositories; this plugin
is what lets their stores meet.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
