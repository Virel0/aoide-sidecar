# Client integration contract

What the Aoide app needs to know to talk to the sidecar. This documents the server as
built, and calls out the places where it differs from `sync-design.md` or where it
enforces something the design left implicit.

The server is deliberately dumb. It relays ops and interprets nothing. Merging,
conflict resolution, smart-rule evaluation and relinking are all yours.

## Endpoints

Base path is `/aoide/sync` on the Jellyfin server itself — same host, same port, same
token as every other Jellyfin call.

```
POST /aoide/sync/push
GET  /aoide/sync/pull?since=<cursor>&limit=<n>
```

Everything else hangs off `/aoide` on the same server: `/aoide/images`, `/aoide/export`,
`/aoide/shares`, `/aoide/queue`, `/aoide/match`, `/aoide/sound-bounds`,
`/aoide/audio-analysis`, `/aoide/beat-grid` and `/aoide/arrangement`, each described in its
own section below.

```
Authorization: MediaBrowser Token="<the user's Jellyfin access token>"
```

There is no separate login. If a Jellyfin call works, these work.

## Response casing — fixed in 1.0.2.0, action needed

Before 1.0.2.0 the server returned **PascalCase** JSON — `{"Accepted":…,"Cursor":…}`,
`{"Ops":…,"HasMore":…}` — because Jellyfin configures MVC's serialiser with a PascalCase
naming policy for its own API, and the responses inherited it. That silently contradicted
this document and fails to decode on anything case-sensitive.

From 1.0.2.0 every field is pinned with an explicit wire name and comes back **camelCase**,
exactly as documented here. If anything on the client was written to tolerate or expect
the PascalCase shape, undo it — plain `CodingKeys`-free `Decodable` structs now work.

## When storage fails: 503

If the database cannot be read or written, both endpoints now return **503** with a
`ProblemDetails` body naming the real cause, instead of a bare 500:

```json
{ "title": "Sync storage unavailable",
  "detail": "SqliteException: SQLite Error 26: 'file is not a database'.",
  "instance": "/config/data/aoide-sidecar/aoide-sync.db" }
```

503 rather than 500 is deliberate: this is usually transient or fixable, and the ops are
still yours. **Keep them queued and retry** — do not mark them synced, and do not
quarantine them the way you would a `rejected` op.

## `GET /aoide/sync/status`

Diagnostics, same auth as everything else:

```json
{ "databasePath": "/config/data/aoide-sidecar/aoide-sync.db",
  "schemaVersion": 1, "writable": true, "journalMode": "wal",
  "directoryWritable": true, "cursor": 2, "opCount": 2 }
```

Since 1.9.0.0 it also carries `pluginVersion` and **`acceptedEntities`** — the exact list
push will accept. The entity list is an allow-list: an op naming an entity the server
does not know is *rejected*, and the contract says a rejected op is dead. So a client
shipped ahead of the server would quarantine real user data. **Before pushing an entity
the server might predate, check `acceptedEntities`; if it is absent, hold those ops
locally — neither push nor quarantine — until it appears.** That makes every future
entity safe to roll out in either order.

`writable: false` with `error` set means pushes will fail while pulls keep working.
`journalMode` should read `wal`; `delete` means SQLite declined WAL, which happens on
filesystems without shared-memory support and makes every write need a journal file
beside the database. `directoryWritable: false` is the other half of that story.

## The sync loop

Push before pull. Your own ops come back from pull carrying the sequence number the
server assigned, which is how you learn they were durably accepted.

```
1. push all local ops where synced = 0
2. mark the returned `accepted` ids as synced
3. quarantine anything in `rejected` (see below) — never retry it
4. pull from your stored cursor, repeatedly, while hasMore
5. apply each batch, then store that batch's cursor
```

Step 5 is ordered that way on purpose: store the cursor **only after the whole batch is
applied**, so an interrupted sync replays rather than skips.

### Cursors

`pull` returns a `cursor`. That is your pull cursor. Persist it.

`push` also returns a `cursor` — **do not store it as your pull cursor.** It is the
server's head sequence, and ops from your other devices may sit below it that you have
never seen. Storing it would skip them permanently. It is informational only.

### Your own ops come back

Pull returns everything for the user, including ops this device pushed. Either apply
them idempotently (they are the same rows you already have) or skip rows whose
`deviceId` matches yours. Both are fine; do one of them deliberately.

## Push

```json
{
  "deviceId": "…",
  "ops": [
    {
      "opId": "uuid",
      "entity": "playlists",
      "entityId": "uuid",
      "operation": "upsert",
      "payload": { },
      "createdAt": 1754500000000
    }
  ]
}
```

```json
{
  "accepted": ["uuid"],
  "rejected": [],
  "cursor": 12345
}
```

`accepted` includes ops the server had **already** seen. Re-pushing is accepted and
ignored, so a push that times out can simply be sent again — that is the whole point of
the client-generated `opId`. Retry freely.

### `rejected` — not in the original design

An op that fails validation is listed in `rejected` with a reason and is left out of
`accepted`. Valid ops in the same batch still land.

This exists because the two obvious alternatives are both bad: failing the whole batch
lets one malformed op wedge the queue forever, and dropping it silently loses it with
no trace. **An op in `rejected` will never be accepted.** Mark it dead and surface it
in a log — retrying is an infinite loop.

## Pull

```json
{ "ops": [ … ], "cursor": 12400, "hasMore": true }
```

Ops come back in ascending `seq` order, each with three fields the server added:

| field        | meaning                                             |
| ------------ | --------------------------------------------------- |
| `seq`        | server sequence number; the basis of the cursor      |
| `deviceId`   | which device pushed it                               |
| `receivedAt` | server receipt time, ms since epoch                  |

`hasMore` is exact — the query reads one row past the limit — so you will never be sent
back for a page that turns out to be empty.

`limit` defaults to 500 and is clamped to 1000.

### Use `receivedAt` for conflict resolution

Conflict resolution stays client-side: last-writer-wins per field on `updated_at`, with
`origin_device` as the tiebreak. But `updated_at` comes from the writing device's clock,
and a device with a badly wrong clock would otherwise win every conflict forever.

`receivedAt` is the server's own clock, stamped on arrival. Use it to sanity-check
`createdAt` — a `createdAt` far in the future relative to `receivedAt` is a broken
clock, not a genuinely newer write.

## What will get your op rejected

These are enforced. Most are exactly the design doc, but the string matching is strict.

| rule | detail |
| ---- | ------ |
| `entity` must be one of | `playlists`, `playlist_items`, `folders`, `likes`, `play_events`, `queue_state`, `track_flags` (1.9.0.0) — or whatever `acceptedEntities` says, see below |
| `tracks` is refused | it is a per-device cache; rebuild it from this device's own Jellyfin connection |
| `operation` must be | `upsert` or `delete` — **lowercase, case-sensitive**. `UPSERT` is rejected |
| `payload` must be | a JSON **object**, the full row after the change. Not an array, string or null |
| `payload` size | ≤ 256 KB |
| `payload` nesting | ≤ 32 levels — see [Payload depth](#payload-depth) |
| `opId` | non-empty, ≤ 128 characters |
| `entityId` | non-empty, ≤ 256 characters |
| `createdAt` | a positive millisecond timestamp |
| batch size | ≤ 1000 ops, else the whole push is 400. Split it |

A batch over the size limit is a hard 400 rather than a partial accept, because you can
just split it — no data is at risk.

The payload's *contents* are never inspected. The sidecar stores the JSON verbatim, so
you can add a column to the curation store without a server deploy.

## Payload depth

**The server guarantees no stored payload nests deeper than 32 levels.** A pull response
therefore tops out around 35 including the envelope, far under the ~512 where
Foundation gives up. Inbound sync cannot be wedged by a deep document.

This mattered because the failure has no client-side seam: `JSONDecoder` refuses an
over-nested document *whole*, so a single bad row would stall every device with no way
to skip past it. It can only be stopped on the way in, which is where it now is.

Two limits, doing different jobs:

- **32 (configurable)** — the per-op limit. An op past it is refused individually, with
  a reason, and everything else in the batch still lands.
- **128** — the parser ceiling for the whole request. A body past *that* is a 400 for
  the batch. It is bounded so a hostile body cannot run the parser away, and it sits far
  enough above 32 that anything realistic gets the clean per-op rejection instead.

Before this, a deep payload threw during model binding and took the **entire batch**
with it — a poison pill that would stall the outbound queue with no clue which op was at
fault. Reachable by accident through a recursive `smart_rules` bug, not just by a
hostile peer. If you ever see a 400 titled `Malformed request body`, that is the >128
case and the batch has a genuinely broken op in it.

## Conflict resolution, per field

`sync-design.md` specifies last-writer-wins **per field**, but the schema carries only
one `updated_at` per row, so as written it can only do per-row — and one of two
concurrent edits to different fields is lost.

**Fixing this needs no server change.** The sidecar never parses payloads; it stores the
JSON object verbatim and hands it back. Per-field timestamps live *inside* the payload,
which is exactly the kind of schema evolution payload opacity was preserved for. Add the
field and push — no plugin release, no coordinated deploy, no wire-format negotiation.

A convention that stays backward compatible:

```json
{
  "id": "…",
  "name": "Late Night",
  "description": "…",
  "folder_id": null,
  "updated_at": 1754500000000,
  "origin_device": "phone",
  "deleted": 0,
  "field_updated_at": {
    "name": 1754500000000,
    "description": 1754400000000
  }
}
```

Merge rule: for each field, take its timestamp from `field_updated_at`, **falling back to
the row's `updated_at` when absent**; higher wins; tie breaks on `origin_device`. That
fallback is what makes it safe to roll out — ops already in the log, and devices on older
builds, keep working as per-row, and the two interoperate. No migration.

Keep `id` and `deleted` row-level. A soft delete is a fact about the row, not a field,
and merging it per-field invites a half-deleted row.

### Only two entities need this

| entity | mutable fields | verdict |
| ------ | -------------- | ------- |
| `playlists` | name, description, folder_id, smart_rules, sort_index | **needs per-field** |
| `folders` | name, parent_id, sort_index | **needs per-field** |
| `playlist_items` | position | per-row is already per-field |
| `likes` | liked | per-row is already per-field |
| `play_events` | none — append-only | no conflict is possible |
| `queue_state` | whole-row snapshot | **keep per-row** |

`queue_state` is the one to be careful with: merging a queue field-by-field could
produce a playback position from one device against a track list from another — a state
that existed nowhere. Take the whole row or none of it.

Note also that the reordering half of the design doc's motivating example never reaches
this path at all: order lives in `playlist_items.position` as a fractional index, so
concurrent reorders are separate rows and already both survive.

### `seq` as a tiebreak

Every op carries `seq`, a total order every device agrees on. It is a better tiebreak
than `origin_device` if you want one, since it reflects arrival rather than an arbitrary
string comparison.

It is **not** a substitute for `updated_at`. A device that edits while offline and syncs
an hour later gets a *higher* seq than an edit made after it, so ordering by seq alone
would let a stale edit win.

## Playlist artwork

Added in 1.1.0.0.

**Artwork bytes never go in the op log.** Payloads are capped at 256 KB and every device
replays the entire log, so images in it would make a full history sync grow without
bound. The log carries a reference; the bytes travel separately.

Add two fields to the `playlists` row. No server change is needed for this part —
payloads are opaque, so this is yours to define:

```json
{ "id": "…", "name": "Late Night",
  "image_hash": "8581e780…",   // lowercase hex SHA-256 of the bytes, or null
  "image_mime": "image/png" }
```

### Endpoints

```
PUT  /aoide/images/{sha256}     body = raw bytes, Content-Type = image/jpeg|png|webp
GET  /aoide/images/{sha256}
HEAD /aoide/images/{sha256}     cheap "do I need to upload this?"
```

Same auth as everything else. Limits: 5 MB (configurable), and only JPEG, PNG or WebP.

### Order matters

**Upload the blob before pushing the op that references it.** Push the playlist row
first and every other device sees an `image_hash` it cannot fetch until you catch up.

Setting artwork:

1. Compute the SHA-256 of the bytes.
2. `HEAD` it — 200 means it is already stored and you can skip the upload entirely.
3. `PUT` the bytes under that hash if it 404s.
4. *Then* push the playlist op carrying `image_hash` and `image_mime`.

Receiving artwork: an inbound playlist op whose `image_hash` you have no local copy of
is a `GET` away. Cache it by hash on disk.

### Seeing what the store holds, and reclaiming what it doesn't need

Added in 1.4.0.0.

```
GET  /aoide/images/orphans                          → reports; deletes nothing
POST /aoide/images/orphans/reclaim?olderThanDays=30 → explicit, opt-in
```

```json
{ "totalBlobs": 14, "totalBytes": 3221225, "orphanBytes": 184320, "graceDays": 30,
  "orphans": [ { "imageHash": "8581e780…", "sizeBytes": 20480, "ageDays": 41, "reclaimable": true } ],
  "reclaimed": 0, "reclaimedBytes": 0 }
```

The report is also the answer to "did my upload actually arrive?" — a blob you just
pushed shows up immediately with `ageDays: 0` and `reclaimable: false`. That is why
orphans inside the grace period are listed rather than hidden.

**Nothing is ever reclaimed automatically.** A blob that looks unreferenced is not always
safe to delete: the contract has you upload bytes *before* pushing the row that names
them, so in between, a blob is genuinely unreferenced and genuinely still needed. A
device that uploaded, went offline, and returns weeks later with its op still queued is
the same situation stretched out. The grace period, measured from when the blob was
stored, covers it.

`olderThanDays` can only make a sweep **more** cautious — a smaller value is raised to the
configured grace period. Waiving it would defeat the point of having one.

Artwork shared by two playlists stays alive while either one does, which matters because
content addressing makes sharing the normal case.

### Why hashes rather than playlist ids

The address *is* the content, which buys several things at once: re-uploading is a
no-op rather than a conflict, two playlists sharing artwork store one copy, and a client
can cache a hash forever because the bytes behind it can never change — responses carry
`immutable` and a year-long `max-age`. Changing a playlist's art is just a new hash in
the next op.

The server recomputes the SHA-256 of every upload and rejects a mismatch with 400. That
check is what makes the store trustworthy: without it a client could park arbitrary
bytes under a hash every other device already believes it knows.

## Exporting playlists into Jellyfin

Added in 1.2.0.0; adoption by source id and artwork in 1.3.0.0.

```
POST /aoide/export/playlists
```

Runs for the calling user and returns what it did:

```json
{ "created": 0, "adopted": 1, "updated": 0, "unchanged": 3, "deleted": 0,
  "skippedSmart": 1, "unresolvedTracks": 0,
  "artworkApplied": 1, "artworkMissing": 0, "errors": [] }
```

Manual only — nothing runs on a timer, so an export happens because someone asked.

### One-way, structurally

The exporter reads the op log and writes to Jellyfin. **No code path turns a Jellyfin
playlist into an op**, so a feedback loop is impossible here rather than merely avoided.

That leaves exactly one rule, and it lives on the client: **never auto-import Jellyfin
playlists.** Import is deliberate and user-initiated. An automatic read-back turns an
exported edit into an inbound change, which exports again, and playlists grow duplicates
on every cycle.

Every managed playlist is stamped `ProviderIds["AoideSidecar"] = <aoide playlist id>`.
Use it to tell a mirror from a real Jellyfin playlist when importing — skip anything
carrying it. It is also the only thing the exporter will ever delete, so a playlist the
user made in Jellyfin cannot be removed by a sync.

### Adoption: source id first

An Aoide playlist with no export mapping yet is matched against Jellyfin in this order:

1. **`sourceJellyfinId`** in the payload — an exact identity. Adopted unless that
   playlist is already stamped for a *different* Aoide playlist.
2. **Exactly one unstamped playlist with the same name** — the fallback for playlists
   created fresh in Aoide, where no source exists.
3. Otherwise a new playlist is created.

Keep `sourceJellyfinId` on anything imported from Jellyfin. It is right where a name
cannot be: two server playlists sharing a name are still two distinct ids, and renaming
the copy in Aoide before the first export still adopts the original and renames it
rather than stranding it and making a duplicate.

Both `sourceJellyfinId` and `source_jellyfin_id` are read.

### Smart playlists are never exported

They are rules, and Jellyfin has no concept of one. Evaluating them needs track metadata
and play-event aggregates the sidecar does not hold, so they stay Aoide-only rather than
exporting as a snapshot that silently goes stale. They appear as `skippedSmart`.

### Artwork

An exported playlist takes its Jellyfin cover from `image_hash`, fetched from the blob
store. A hash the store does not hold yet counts as `artworkMissing` and is retried on
the next run — so pushing the op before uploading the bytes is recoverable, not
permanent. It is still worth uploading first.

### What export overwrites

Jellyfin becomes a mirror. Name and track membership are rewritten from Aoide on every
run where they differ, so **edits made in Jellyfin's UI are overwritten**. Deleting a
playlist in Aoide deletes its Jellyfin mirror.

Unresolvable tracks are counted and skipped, never fatal — a file may simply be offline,
and the entry returns on its own once the id resolves again.

## Handing playback between your own devices

Added in 1.7.0.0.

```
GET /aoide/queue
```

```json
[ { "deviceId": "laptop", "isCurrentDevice": false, "ageSeconds": 12,
    "receivedAt": 1786000000000, "updatedAt": 1786000000000,
    "payload": { "deviceName": "Desk PC", "trackIds": ["…"], "position": 3, "elapsedMs": 41200 } } ]
```

One entry per device, **most recently updated first** — so the queue to offer is
normally the first one that is not `isCurrentDevice`.

Nothing new to write. Keep pushing `queue_state` ops exactly as before; this is a fast
read of the rows already in the log, because handover is a foreground action with someone
waiting on it and walking the op log to answer "what is my phone playing" would make it
as slow as a full sync.

**Superseded queue rows are compacted away on push.** `queue_state` is one row per device,
replaced whole every time playback moves, so all but the latest are values that have been
overwritten rather than history. Without this it would be the fastest-growing table in the
store. Two consequences worth knowing:

- Pull will not necessarily show every intermediate queue state. It was never meaningful
  to replay them, and the current one always survives.
- Push as often as playback genuinely changes. The log will not grow because of it.

`ageSeconds` and `receivedAt` come from the **server's** clock on both sides. Judge
freshness on those rather than on `updatedAt` — a device with a wrong clock would
otherwise always claim to be the most recent and win every handover.

## Collaborative playlists

Added in 1.6.0.0.

A playlist belongs to whoever first pushed it. The owner invites another Jellyfin user,
and from then on both accounts push edits to it and both receive the other's **through
the pull they already make** — one loop, one cursor, no second sync path.

```
GET    /aoide/shares                                → shares owned by, and granted to, the caller
POST   /aoide/shares  { playlistId, granteeUserId, canEdit }
DELETE /aoide/shares/{playlistId}/{granteeUserId}
```

Only the owner can share. Either side can revoke.

### What changes for the client

**Pull can now return ops authored by someone else.** Every op carries `authorUserId`,
which differs from your user only on a shared playlist. Use it to attribute an edit; do
not treat a foreign op as your own device's.

A shared playlist otherwise behaves like any other: the same conflict rules, the same
fractional indices, the same last-writer-wins per field. Two people reordering at once is
exactly the case fractional indexing already handles.

**Only playlists travel.** `play_events`, `likes` and `queue_state` carry no playlist id,
so they never cross between accounts. Sharing a playlist shares the playlist, not the
account's listening.

### The one rejection that is not permanent

Everywhere else in this document, an op in `rejected` will never be accepted. Editing a
playlist you lack access to is the exception — the reason reads:

```
Playlist 'p1' belongs to another user and is not shared with you for editing.
```

That can change, and the same op would then succeed. It usually means the UI let someone
edit a playlist whose share was revoked while their change sat queued. **Do not retry it
blindly, and do not silently discard it either**: refresh `/aoide/shares`, and if access
really is gone, surface it rather than dropping the user's edit without a word.

### Revoking

Revoking stops the owner's future changes reaching the collaborator. It does **not**
retroactively withdraw ops the collaborator already wrote — those are their own
authorship and stay in their own stream — and it does not reach into their device to
delete a playlist they already synced. Drop it locally when it disappears from
`/aoide/shares`.

## Matching an imported track list

Added in 1.8.0.0.

```
POST /aoide/match
```

The hard part of a Spotify import is not parsing the export, it is finding a thousand
rows in a library of twenty thousand. From a phone that is a thousand round trips; on the
server the library is already in memory, so it is one call. **Try this first and fall
back to local matching when it is unreachable** — the rules are identical, so the two
paths agree about what is missing.

Send your `ImportedTrack` rows as a JSON array, up to **5000** per call (400 above that;
split the import):

```json
[ { "title": "bad idea right?", "artists": ["Olivia Rodrigo"],
    "album": "GUTS", "durationMs": 184000, "isrc": "USUG12305108" } ]
```

`album` and `isrc` are optional — the Spotify embed page gives neither, so the fuzzy
path is the main path, not the edge case. `artists` is every credited name.

```json
{ "matched": 1, "librarySize": 4213,
  "results": [ { "jellyfinId": "…", "confidence": 0.92,
                 "titleScore": 0.9, "artistScore": 1, "durationScore": 0.8,
                 "title": "bad idea right?", "artists": ["Olivia Rodrigo"],
                 "album": "GUTS", "durationMs": 184000 } ] }
```

`results` is aligned with your rows, in order; a row that matched nothing is `null`. The
matched track's own metadata comes back so the UI can show what was chosen, and the
three component scores are there so a surprising match can be explained rather than
just believed.

### The rules, exactly as the clients have them

Normalise: lower-case, fold diacritics, `&` to `and`, drop bracketed groups **and** ` - `
suffixes that contain a noise word — a bracket group without one, like the
"(What's the Story)" in Morning Glory, is part of the title and stays — then keep only
letters, digits and spaces. The noise-word list is one list across all three codebases:
feat, featuring, ft, remaster(ed), remix(ed), mix, live, version, edit(ed), deluxe, mono,
stereo, acoustic, instrumental, demo, radio, single, bonus, anniversary, explicit, clean,
extended, reissue, with, from, original, edition. Score each field from word sets —
equal 1, one a subset of the other 0.9, otherwise Jaccard; artist is the best over every
pair of credited names. Duration: within 5 s 1, within 15 s 0.8, beyond that reject;
unknown on either side 0.9. Accept when title ≥ 0.75, artist ≥ 0.7 and duration did not
reject; **with no artist to compare on either side, only an exact title matches**. Rank
by 0.5·title + 0.35·artist + 0.15·duration.

An ISRC that matches on both sides is decisive on its own — rare in Jellyfin tags,
certain when present.

The server's implementation is verified against the spec above and against the 16-row
table the two clients share, which is a hard test gate here. The canonical copy lives in
the desktop repo at `docs/match-table.json`; the sidecar carries a copy at
`tests/Jellyfin.Plugin.AoideSidecar.Tests/match-table.json`. **When the table changes,
copy it over** — a stale copy is the one way the phone and the server could quietly
disagree about what is missing.

## Taste flags

Accepted since 1.9.0.0. `track_flags` is a personal entity and needs nothing further from
the server: it carries no playlist id, so it can never reach a collaborator through a
shared playlist; retention only ever touches `play_events`; compaction only
`queue_state`; export only playlists. The payload is opaque here as everywhere — the
per-field `fieldUpdatedAt` merge and the `(user, jellyfinId)` dedupe are the client's,
exactly as with `playlists` and `likes`.

Gate the feature on `acceptedEntities` containing `track_flags`. Ship the server first
if you can; if you cannot, the gate is what keeps a flag set against an older server
from being quarantined as garbage.

## Sound bounds: where each track's sound starts and stops

Added in 1.10.0.0. For the devices that do not hold the file — the desktop always, the
phone for anything streamed.

```
GET  /aoide/sound-bounds?ids=<jellyfinId>,…        (≤ 200)
POST /aoide/sound-bounds  { bounds: { id: { soundStartMs, soundEndMs } | null } }
```

```json
{ "bounds": { "3b1c…": { "soundStartMs": 1940, "soundEndMs": 5200 },
              "a71f…": null },
  "pending": ["9c0e…"] }
```

- **`null`** means measured, nothing to trim: play the file whole.
- **`pending`** means queued: play it whole this time and ask again next time. Nothing is
  measured until someone asks, and one file decodes at a time by default, so the first
  request for a long playlist is mostly pending and the next is mostly answered.
- **Missing from both** means unknown, not visible to you, or no file to measure — the
  same thing as far as the player is concerned. A 404 on the whole endpoint (an older
  server) is also "trim nothing", as the desktop already assumes.

The cache is keyed by the file's modification time, so a replaced file is measured again
without anyone clearing anything.

### The measurement is the phone's, not an approximation of it

`PlaybackKit/SilenceBounds.swift` is ported line for line and run over PCM decoded by
the ffmpeg Jellyfin ships: any channel above 0.00178 absolute is sound; first and last
such frame to milliseconds by schoolbook rounding; −60 ms and +200 ms margins, floored
and capped; nothing reported when both trims are under 300 ms. The phone's six fixtures
pass here with the spec's exact numbers, 1940 and 5200.

`silencedetect` was deliberately not used. It finds *runs* of silence with its own
minimum-duration window and reports its own crossings — a second algorithm approximating
the first. With the reference scan over decoded samples, only the decoder differs.

**Where the decoder can differ:** for lossless files (FLAC, ALAC, WAV) both sides see the
same samples and the numbers are identical. For lossy files (MP3, AAC) the two decoders
can disagree by a few samples, and MP3 encoder-delay handling can shift a frame — well
inside the 60 ms and 200 ms margins, so a trim never lands on audible sound, but the
millisecond values may not be byte-equal between a phone measurement and a server one.
Treat them as the same answer.

### `POST` — a phone sharing what it measured

Optional. A client that measured a file itself may send the result so the server serves
it without decoding. It is stored as-is, keyed on the file's current modification time,
and served with `source: client` internally; it is not re-measured.

## Loudness and tempo

Added in 1.11.0.0. Two facts about a file that neither client can work out for something
it is streaming, and that nobody wants to compute on a phone for a library of thousands.

```
GET  /aoide/audio-analysis?ids=<jellyfinId>,…      (≤ 200)
POST /aoide/audio-analysis  { analysis: { id: { loudnessLufs, truePeakDbfs, bpm, bpmConfidence, bpmStability } | null } }
```

```json
{ "analysis": { "3b1c…": { "loudnessLufs": -9.7, "truePeakDbfs": -0.3,
                           "bpm": 127.97, "bpmConfidence": 1.0, "bpmStability": 1.0 },
                "a71f…": { "loudnessLufs": -14.2, "truePeakDbfs": -1.1,
                           "bpm": null, "bpmConfidence": null, "bpmStability": null },
                "c904…": null },
  "pending": ["9c0e…"] }
```

Every field inside an entry is always present, `null` included — the server pins that
rather than leaving it to Jellyfin's serialiser, which drops nulls by default.
`bpmStability` was added in 1.12.0.0 and is absent from servers older than that.

`pending`, `null` and a missing id mean exactly what they mean for sound bounds: queued,
measured with nothing to report, and unknown-or-invisible. Every field is independently
nullable — a track can have a loudness and no usable tempo, which is the common case for
ambient and for anything rubato.

**It is the same decode.** Asking either endpoint about a file measures it for both, so a
client that has already fetched sound bounds for a playlist gets its loudness and tempo
without the server decoding anything again. Both caches are keyed on the file's
modification time, and a replaced file re-measures for both.

### Loudness

`loudnessLufs` is EBU R128 integrated loudness over the whole track and `truePeakDbfs` is
its true peak, both straight out of `ffmpeg -af loudnorm=print_format=json` — the
implementation the spec pins, rather than a second R128 implementation that would agree
with it only approximately. A track under three seconds, or quieter than −70 LUFS, is not
measured and reports `null`: `loudnorm`'s gating has not seen enough audio to mean
anything.

`truePeakDbfs` above zero is not an error. A track clipped by its own master really does
peak above full scale, and that is exactly what a client needs to know it has no headroom.

Normalise to **−18 LUFS** and attenuate only, as agreed: gain is `−18 − loudnessLufs` dB,
and a track already quieter than the reference is left alone rather than amplified into
its own peak. A file carrying ReplayGain tags should use those; this is for the ones that
do not.

### Tempo

`bpm` is beats per minute, `bpmConfidence` is between 0 and 1, and `bpmStability` says
how much of the track actually keeps that tempo. **A tempo the server is not at least 0.5
confident of is not sent at all** — you will see `bpm: null` with a loudness beside it.
That is deliberate: ambient, spoken word and rubato classical have no single tempo, and a
made-up 128 would be sorted against as though it were true. `bpmStability` rides with the
tempo, so it is null whenever `bpm` is.

`aubio` and Essentia were both suggested and neither is available: Jellyfin ships ffmpeg
and nothing else, and the sidecar is a single managed DLL that Jellyfin's own installer
drops into place. Requiring a server admin to install a native library before a plugin
works is out of all proportion to two numbers used for ordering. So the estimate is
written out: the spectrum of each 23 ms window is folded into log-spaced bands, each
band's rise since the previous hop is added up into an onset strength, and that is
autocorrelated over the lags corresponding to 60–200 BPM.

Bands rather than total energy, because most of what carries a beat does not make a track
louder — a hi-hat over a sustained pad, a snare under a bass note. Watching the total,
those events are invisible; watching each band, they are unmistakable.

**One passage cannot decide the answer for a track.** Autocorrelation weights by energy, so
a short loud regular stretch counts for its amplitude squared while minutes of the actual
groove barely register — thirty seconds of sung syllables at 800 ms inside three and a half
minutes of 128 BPM came back as 73 BPM, and the same file with that passage removed came
back as 128. Since 1.15.0.0 the onset signal is levelled over a six-second window before it
is correlated, which puts a quiet verse and a loud chorus on the same footing. If you have
tempi from 1.14.0.0 or earlier on tracks with a loud bridge or a long sung passage, they
are worth re-measuring.

**Accuracy.** Across synthesised beats from 62 to 198 BPM the estimate lands within
**0.2 BPM**, with no octave errors anywhere in the range. Half and double are still the
failure mode to expect on real music — an envelope that repeats every beat repeats every
two beats and correlates equally well with both — but the cases that used to break
outright do not any more.

**What that is and is not good enough for.** It orders a mix properly. It is not a beat
grid: there is no phase, so nothing tells you where the beats fall, only how far apart
they are. See `bpmStability` below before assuming a fixed grid would fit at all.

### `bpmStability`

The tempo is measured again over overlapping twenty-second windows and compared with the
whole-track answer. `bpmStability` is the fraction of windows that agreed to within half a
per cent, with half and double counted as agreeing — they are the same grid read at a
different resolution.

| value | what it means |
| ----- | ------------- |
| `1.0` | Every window agreed. A fixed grid fits, as far as twenty seconds of audio can tell. |
| `0.3`–`0.6` | The tempo moves. A drift of a few per cent across the track, which is what a performance does. |
| `0.0`–`0.2` | No fixed tempo worth the name, whatever the headline number says. |
| `null` | Under about forty seconds of audio — fewer than three windows, and nothing to compare. |

This is the number to check before lining two tracks up, and it is a separate question
from confidence. A confident tempo says the onsets are periodic *on average*; a track that
sped up from 105 to 145 still reports a confident 135, and its stability is 0.0. Measured
against ramps: 120→121 reports 1.0, 120→122 reports 0.62, 120→125 reports 0.31, 105→145
reports 0.0.

Half a per cent is loose for gridding — it is what twenty seconds of audio can resolve,
not what a grid needs. A tempo half a per cent out drifts most of a second across a
three-minute track. Treat `1.0` as "nothing here rules a grid out", not as a guarantee.

### `POST` — a client sharing what it measured

Optional, and the same shape as the response. `loudnessLufs` and `truePeakDbfs` go
together: send both or neither. `bpm` without a `bpmConfidence` is taken at face value,
and `bpmStability` may be omitted. A posted tempo is stored at whatever confidence it
carries and filtered on the way out like any other, so posting one below 0.5 stores it and
serves nothing.

### Not a sync entity

Nothing here is user data. These are facts about a file, recomputable at any time from
the file itself, so there is no op log, no export, no retention rule and nothing to
resolve between devices. Do not push them as ops — `audio_analysis` is not an accepted
entity and never will be.

## Arrangement

Added in 1.15.0.0. What a track is made of and in what order, so a transition can happen
somewhere the music has room for it.

```
GET /aoide/arrangement?ids=<jellyfinId>,…      (≤ 200)
```

```json
{ "arrangements": {
    "3b1c…": {
      "sections": [
        { "startMs": 0,      "endMs": 29993,  "kind": "intro",     "energy": 0.0 },
        { "startMs": 29993,  "endMs": 119993, "kind": "drop",      "energy": 0.91 },
        { "startMs": 119993, "endMs": 149993, "kind": "breakdown", "energy": 0.46 },
        { "startMs": 149993, "endMs": 179993, "kind": "drop",      "energy": 1.0 },
        { "startMs": 179993, "endMs": 210000, "kind": "outro",     "energy": 0.31 }
      ],
      "phraseBars": 16,
      "phraseAnchorMs": 23.4,
      "vocals": [ { "startMs": 39636, "endMs": 80062 } ] },
    "a71f…": null },
  "pending": ["9c0e…"] }
```

Same decode as everything else. Sections are contiguous and cover the track end to end.

### Sections

Boundaries come from self-similarity: each beat is described by what the spectrum was made
of over it, every beat is compared with every other, and a checkerboard kernel is dragged
down the diagonal — it reads high where the beats before a point resemble each other, the
beats after resemble each other, and the two halves do not resemble one another. That is a
section boundary expressed as arithmetic, and it needs no model.

Per beat, on the grid that was already fitted, so every boundary is on a beat for free.
Each is then moved onto a **phrase line if one is within two bars**, and onto the nearest
**bar line** otherwise. Dragging a real change half a phrase to fit the grid would be
inventing structure rather than finding it.

**`energy` is normalised within the track**, so a quiet record's loudest part still reads
as 1.0. It is level and onset density together, which is why a busy quiet passage can
out-rank a sparse loud one.

### The kinds, and the two that are never returned

| kind | what it means |
| ---- | ------------- |
| `intro` | The opening, and quiet relative to the track |
| `build` | Climbs across itself into something louder |
| `drop` | The loudest sections, with the weight in the bottom third to match |
| `breakdown` | Clearly quieter than the section either side |
| `outro` | The ending, and quiet |
| `unknown` | Measured, and nothing about it is distinctive enough to name |

**`verse` and `chorus` are in the agreed set and are never returned.** Telling one from the
other is a judgement about song form, not a property of the signal — they are frequently
identical in level, spectrum and density, and separating them needs a model trained on what
people call things. Everything in the table above is measurable. Expect `unknown` often; it
is a real answer.

A build has to climb across its own span, not merely be quieter than what follows. Without
that rule every breakdown before a drop came back as a build — and a breakdown is precisely
where a client would most want to bring a record in.

### The phrase grid

`phraseBars` is 8, 16 or 32, and `phraseAnchorMs` is a downbeat that begins a phrase; both
are null where the structure would not commit, which is the honest answer for a live
recording or anything through-composed.

Found by scoring each candidate length and offset on how much of the track's novelty lands
on the bar lines it predicts. **Shorter lengths are preferred** unless a longer one is
clearly better: a track built in eights also changes on every sixteenth and thirty-second
bar line, so without that bias the longest candidate wins by having fewer lines to average
over — and it drags every section boundary out of place with it.

### Vocals — read this part carefully

`vocals` has **three** states and they are not interchangeable:

| value | meaning |
| ----- | ------- |
| `[ {startMs, endMs}, … ]` | Singing was found in these stretches |
| `[]` | Looked, and found none |
| `null` | **Could not be told.** Not an instrumental |

A `null` must be treated as "this record may be singing throughout". It happens on mono
files, and on stereo files mixed too narrowly to read, and on tracks whose whole length
scores the same — either sung throughout or not at all, and this cannot say which.

**This is the roughest measurement in the plugin and it is the one to trust least.** With
no stems there is no way to isolate a voice, so it leans on two things true of most
produced records and not of all of them: a lead vocal is mixed to the centre, so it
survives in the mid channel and largely cancels in the side; and a voice does not hold a
pitch the way an instrument does, so the spectrum in the vocal band keeps moving when a
pad's does not. A centred lead synth will read as singing. A hard-panned vocal will not.

**It is biased towards saying yes**, deliberately. A false positive costs a mix that would
have been fine; a false negative puts two lead vocals on top of each other, which is the
mistake the measurement exists to prevent. Errors belong on the cautious side.

**Untested on real music**, like the key. It finds a centred vibrato lead over a wide pad
to within half a second on a synthesised fixture. That proves the mechanism, not the
result.

### Where this is weakest

**Section boundaries inherit the grid's segments.** They are snapped to bar lines of the
published grid, which means they are only as good as the segment they land in. Check the
`residualMs` of the segment covering a boundary before treating it as a true bar line: on a
segment reading 50 ms, a boundary can sit a beat or so from where the music actually
changed.

**A build that ramps continuously into a drop tends to merge with it.** Self-similarity
finds a boundary where the music stops resembling itself, and a build that slides into the
drop never does. Expect the two to come back as one section, usually named `drop`.

**Energy is relative, so an unusual section can pull the scale.** On a track whose second
drop carries a vocal the first drop can read at 0.5 rather than 1.0, and drop below the
threshold for being named `drop` at all. The ordering is trustworthy; the absolute figures
are only meaningful against the same track.

### What is deliberately not here

No stems, no separation, no model of taste, and no opinion about what to play next.

## How much has been measured

Added in 1.14.0.0. Measurement is lazy — a track is decoded the first time something asks
about it — so nothing else here answers "how far along is it".

```
GET /aoide/analysis/coverage
```

```json
{ "tracks": 4210, "soundBounds": 4198, "audioAnalysis": 4198, "beatGrids": 4198,
  "gridded": 3806, "arrangements": 4198, "described": 3790, "measuring": 1, "sweepEnabled": false,
  "summary": "4198 of 4210 tracks measured (100%), 3806 with a beat grid. Nothing outstanding." }
```

- **`beatGrids`** is how many have been decoded; **`gridded`** is how many of those came
  out with a grid. The difference is not a backlog — a spoken-word recording decoded and
  found to have no beat is finished. A progress bar wants `beatGrids` against `tracks`.
- **`measuring`** is what is queued or decoding at this instant. Zero with work
  outstanding means nothing is asking, not that anything is stuck.
- **`summary`** says the same thing in a sentence, for a person reading the response.

`tracks` is every audio item in the library; the caches are global rather than per user, so
the counts are too.

## Beat grid

Added in 1.13.0.0. Where the beats actually fall, which is a different measurement from
how far apart they are and the one a mixed transition needs.

```
GET /aoide/beat-grid?ids=<jellyfinId>,…      (≤ 200)
```

```json
{ "grids": {
    "3b1c…": {
      "segments": [
        { "startMs": 0, "endMs": 214000, "anchorMs": 495.3, "bpm": 128.0, "residualMs": 3.9, "beats": 456 }
      ],
      "beatsPerBar": 4, "downbeatIndex": 0,
      "mixInMs": 15431, "mixOutMs": 187431,
      "key": "8A", "keyConfidence": 0.71 },
    "a71f…": null },
  "pending": ["9c0e…"] }
```

`pending`, `null` and a missing id mean what they mean everywhere else in this family:
queued, measured with no grid worth having, and unknown-or-invisible. Same decode as the
other two, so a track already measured for its loudness has a grid waiting.

There is **no `POST`**. The other two accept a client's own measurement because a client
holding the file can take one; a client that could fit its own beat grid would not have
needed this endpoint. Ask if that turns out to be wrong.

### The fit

`beat(n) = anchorMs + n × 60000 / bpm`, within a segment.

**`residualMs` is the field that decides whether a transition runs at all.** It is the RMS
distance between the fitted beats and the beats actually detected. Sixteen bars at 128 BPM
is thirty seconds; holding a twentieth of a beat across that wants a grid good to about
20 ms, and a track whose own onsets sit 60 ms off its own fit is not one anything should
try to lock to. Refusing on it is the intended use.

On sequenced material the fit comes back at **3–5 ms** residual with the anchor within
5 ms of the true first beat. Expect worse on anything played by people; that is what the
number is for.

`bpm` here is fitted across the whole segment and is more precise than the `bpm` from
`/aoide/audio-analysis`, which is measured a different way. Where they disagree slightly,
this one is the one to mix on.

### Segments

Most tracks are one. A track that genuinely changes tempo comes back as several, each
with its own anchor, tempo and residual, rather than one fit smeared across the change
describing neither half. Segments are contiguous and cover the track end to end, so a
lookup for "which segment holds this moment" always has an answer.

**Mix within a segment.** A blend that crosses a boundary is crossing a tempo change.

### The meter

`beatsPerBar` is 4 for nearly everything and 3 for a waltz; both it and `downbeatIndex`
are `null` when the meter could not be established, and a client should not offer a
bar-aligned transition for that track.

Downbeats are found in the bottom 250 Hz, where a kick lives — far more reliable than
whatever is loudest overall. Four is preferred; three has to beat it by a clear margin
before a waltz is believed.

`downbeatIndex` is **0 whenever the meter is known.** Every segment's beat zero is put on
a downbeat, so `bar(n) = anchorMs + n × beatsPerBar × 60000 / bpm` holds for every segment
without tracking phase across a tempo change. The field is kept because it is in the
contract and because a future grid might not be able to promise that.

### Mix points

`mixInMs` and `mixOutMs` are downbeats of the fit, and are `null` together with the meter.

They are measured on onset strength rather than loudness, which is what makes an intro of
pure atmosphere read as *not yet playing*: a sustained pad can be as loud as the chorus
with nothing starting in it. Every bar of a four-bar phrase has to carry its weight, not
the phrase on average — averaging let a strong second half drag a silent first half over
the line and put the start of a blend four seconds inside a twenty-second pad.

### The key

`key` is on the **Camelot wheel** — `8A` is A minor, `8B` is C major — because the stated
use is preferring one pair of tracks over another, and adjacency on that wheel is the
whole point of it. Letter `A` is minor, `B` is major; neighbouring numbers and the same
number in the other letter are the compatible moves.

| | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **A** (minor) | G♯m | E♭m | B♭m | Fm | Cm | Gm | Dm | Am | Em | Bm | F♯m | C♯m |
| **B** (major) | B | F♯ | D♭ | A♭ | E♭ | B♭ | F | C | G | D | A | E |

It is Krumhansl–Kessler profile matching over a chroma taken with a longer transform than
the onsets use — 186 ms rather than 23 ms, because pitch wants frequency resolution where
onsets want timing. It hears which notes a track leans on and nothing else, so it has no
idea about modulation and is weakest between a key and its relative major or minor, which
share the same seven notes. `keyConfidence` is the margin over the runner-up, which is
exactly where that weakness shows up. Use it to prefer a pair, never to refuse one.

**Untested on real music.** The estimator is right on synthesised progressions, including
the C major / A minor pair. Nobody has run it over a real library yet. Treat a low
confidence as meaning what it says.

### What is deliberately not here

No stems, no per-instrument separation, no neural model. Aligning two clocks, running one
deck at the other's tempo, and taking the bass out of the outgoing track on a downbeat are
the client's job, and the grid is what makes them possible.

## Invariants only the client can enforce

The server cannot check these, and nothing will complain if you get them wrong:

- **`entityId` must match the `id` inside `payload`.** The server never parses payloads,
  so a disagreement here will sync happily and then confuse every reader.
- **`queue_state` uses `device_id` as its `entityId`**, not a UUID — that table is keyed
  by device.
- **`deviceId` in the push envelope should match `origin_device` in the payloads.**
- **Deletes are soft.** Send `operation: "delete"` *and* a full payload with
  `deleted = 1`. The row still has to travel; a hard delete cannot be synced.

## What the server guarantees

- **Idempotency** — `(user_id, op_id)` is unique. Scoped per user, so op ids only need
  to be unique within an account.
- **Ordering** — SQLite serialises writers, so `seq` is commit-ordered. If you see
  sequence N you have already seen everything below it. A cursor can never skip an op
  that was in flight.
- **Gaps are normal** — a rolled-back transaction burns a sequence. The cursor means
  "everything up to here", not "the next number". Do not treat a gap as data loss.
- **Durability** — one fsync per push batch. When `accepted` comes back, it is on disk.

## Not done

- **No `downloads` table.** "Per-device downloads with shared state" is on the original
  design's Phase 2 list but has no schema. If that state needs to sync it needs a client
  table and a server allow-list entry; the server will advertise the entity in
  `acceptedEntities` once it exists, and clients should gate on that as with any new
  entity.
- **Likes are relayed, not written through.** The design has likes written through to
  Jellyfin when online. The server does nothing with a `likes` op beyond storing and
  relaying it, so write-through, if wanted, is a client call against Jellyfin's own API —
  idempotent, so two devices doing it is harmless.
- **No public playlists.** Sharing is an explicit per-user grant. A playlist visible to
  every user on the server is not implemented.
- **Nothing destructive is scheduled.** Play-history pruning and artwork reclamation are
  manual and report before they act, by design; see their sections above.
