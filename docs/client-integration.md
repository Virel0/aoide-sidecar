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
| `opId` | non-empty, ≤ 128 characters |
| `entityId` | non-empty, ≤ 256 characters |
| `createdAt` | a positive millisecond timestamp |
| batch size | ≤ 1000 ops, else the whole push is 400. Split it |

A batch over the size limit is a hard 400 rather than a partial accept, because you can
just split it — no data is at risk.

The payload's *contents* are never inspected. The sidecar stores the JSON verbatim, so
you can add a column to the curation store without a server deploy.

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
