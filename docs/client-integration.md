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
| `entity` must be one of | `playlists`, `playlist_items`, `folders`, `likes`, `play_events`, `queue_state` |
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

## Not there yet

Worth knowing before you build against something that does not exist:

- **No sharing between users.** Ops are strictly scoped to the authenticated user, so
  collaborative and public playlists have no server support yet. This needs an explicit
  grant table, not a widened query.
- **No `play_events` retention.** The log grows without bound and a fresh device
  replays all of it. Fine now, needs a decision before it gets large.
- **No playlist export to Jellyfin's UI.** When it lands it will be strictly one-way;
  edits made in Jellyfin will need to be an explicit user-initiated import, never an
  automatic read-back, or playlists oscillate.
- **No `downloads` table.** "Per-device downloads with shared state" is on the Phase 2
  list but has no schema in the design doc. If it needs to sync, it needs a table and an
  entry in the server's entity allow-list — tell me and I will add it.
- **Likes write-through.** The design has likes written through to Jellyfin when online.
  That is currently unimplemented on the server side, so if the client is doing it
  directly against Jellyfin's API, that is the behaviour — and it is idempotent, so
  two devices doing it is harmless.
