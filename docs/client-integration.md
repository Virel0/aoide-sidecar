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
