# Client integration

What the app needs to know to talk to the sidecar. This documents the server as built,
including the places where it differs from `sync-design.md`.

Read the numbered traps first — each one is something that fails silently rather than
loudly if you get it wrong.

## Endpoints

Plugin routes live at the Jellyfin server root, alongside Jellyfin's own API:

```
POST   {server}/aoide/sync/push
GET    {server}/aoide/sync/pull?since=<cursor>&limit=<n>
```

Auth is the header the app already sends for every other Jellyfin call:

```
Authorization: MediaBrowser Token="<the user's access token>"
```

It must be a **user access token**, not a Dashboard API key. An API key authenticates a
service, not a person, so it carries no user id — and every op is scoped to a user. An
API key gets a 401.

Status codes worth distinguishing while wiring this up: `404` means the plugin is not
loaded, `401` means the token was not accepted, `200` means you are talking to the
sidecar.

## The sync loop

Push first, then pull, then store the cursor.

```
1. push everything in `ops` where synced = 0   (in chunks, see trap 3)
2. mark those ops synced, using the `accepted` list
3. pull from the stored cursor, repeatedly, while hasMore
4. apply each batch in a transaction, then store the new cursor
```

Push before pull because your own ops come back on the pull with a sequence number,
which is how you learn they were durably accepted.

---

## Trap 1 — the push cursor is not the pull cursor

`push` returns a `cursor`. It is the server's **head** sequence for the user. It is
informational only.

**Do not store it as your pull cursor.** Ops from other devices may sit *below* that
head that you have never seen. Storing it skips them permanently — the playlists just
never arrive, with no error anywhere.

Only a `pull` response advances the pull cursor.

## Trap 2 — an op can be rejected, and retrying it forever will not help

This is an addition to the original contract. `push` returns:

```json
{
  "accepted": ["op-1", "op-3"],
  "rejected": [{ "opId": "op-2", "reason": "Unknown entity 'albums'." }],
  "cursor": 12345
}
```

`accepted` lists what the server durably holds, **including ops it had already seen** —
so a retry after a timeout is safe and idempotent.

Anything in `rejected` was refused and always will be. Valid ops in the same batch still
landed; the batch is not all-or-nothing, deliberately, so one malformed op cannot stall
every good op behind it forever.

Handle it: mark rejected ops as quarantined (a `rejected_reason` column, or similar) and
stop sending them. If you treat "not accepted" as "retry later", that op is re-sent on
every sync for the rest of time.

Any op id absent from **both** lists was not stored — treat it as still pending.

## Trap 3 — batches are capped at 1000 ops

A push larger than that is refused outright with `400`, not partially accepted.

This matters most on the first sync after a long offline stretch, which is exactly when
the backlog is largest. Chunk the outbound queue in ordered slices and push them in
sequence — do not push chunks concurrently, or ops can land out of causal order.

## Trap 4 — `entityId` must match the row's own id

The server never parses payloads. It cannot check this, and it will happily store an op
whose envelope points at one row and whose payload is a different one.

That mismatch only surfaces on another device, as a row written under the wrong key.
Assert it on the way out.

For `queue_state`, the id is the **device id** — that table is keyed by device, not by a
row uuid.

## Trap 5 — your own ops come back

`pull` returns everything past the cursor, including ops this device pushed. Each op
carries `deviceId`, so skip your own if applying them is not perfectly idempotent.

Still advance the cursor past them regardless of whether you apply them.

## Trap 6 — the cursor moves only after the batch is applied

Apply the whole batch and store the cursor in the **same** transaction. If you store the
cursor first and then crash mid-apply, those ops are skipped forever.

Storing it after means an interrupted sync replays — which is harmless, because applying
an op twice is a no-op.

---

## Envelope rules

Every op must satisfy these or it lands in `rejected`:

| field | rule |
|---|---|
| `opId` | non-empty, ≤128 chars. A UUID. This is the idempotency key. |
| `entity` | one of `playlists`, `playlist_items`, `folders`, `likes`, `play_events`, `queue_state` |
| `entityId` | non-empty, ≤256 chars |
| `operation` | `upsert` or `delete`, lowercase |
| `payload` | a JSON **object**, ≤256 KB |
| `createdAt` | positive milliseconds since epoch |

`tracks` is rejected with an explicit message. It is a per-device cache rebuilt from each
client's own Jellyfin connection; keeping it out of the log is what keeps a full history
sync small.

Deletes are soft: send `operation: "delete"` **with the full row** and `deleted: 1`. The
server does not synthesise a payload, and the receiving device needs the row's fields to
apply last-writer-wins against what it already has.

Payloads are stored verbatim and never inspected, so you can add a column to the
curation store without touching or redeploying the server.

## Conflict resolution is yours

The server orders ops and nothing more. Last-writer-wins per field, comparing
`updated_at` with `origin_device` as the tiebreak, happens on the client.

Every pulled op carries a server-stamped `receivedAt` alongside your `createdAt`. Use it
as a sanity bound: a device whose clock is badly wrong would otherwise win every field
conflict forever. A reasonable rule is to distrust `updated_at` when it sits far ahead of
`receivedAt`.

Playlist ordering never enters this path — `position` is a fractional index, so two
concurrent inserts at the same spot both survive.

## Worked example

Push:

```json
POST /aoide/sync/push
{
  "deviceId": "iphone-15-pro-abc123",
  "ops": [{
    "opId": "3f1c9a2e-0b7d-4c1a-9e5f-2d8b6a4c1e70",
    "entity": "playlists",
    "entityId": "9c2b1f04-5e3a-4d7b-8a11-6f0e2c9d4b58",
    "operation": "upsert",
    "payload": {
      "id": "9c2b1f04-5e3a-4d7b-8a11-6f0e2c9d4b58",
      "name": "Late Night", "description": null, "folder_id": null,
      "is_smart": 0, "smart_rules": null, "sort_index": "a0",
      "updated_at": 1754500000000, "deleted": 0,
      "origin_device": "iphone-15-pro-abc123"
    },
    "createdAt": 1754500000000
  }]
}
```

Pull:

```json
GET /aoide/sync/pull?since=0&limit=500
{
  "ops": [{
    "opId": "3f1c9a2e-0b7d-4c1a-9e5f-2d8b6a4c1e70",
    "entity": "playlists",
    "entityId": "9c2b1f04-5e3a-4d7b-8a11-6f0e2c9d4b58",
    "operation": "upsert",
    "payload": { },
    "createdAt": 1754500000000,
    "seq": 41,
    "deviceId": "iphone-15-pro-abc123",
    "receivedAt": 1754500000412
  }],
  "cursor": 41,
  "hasMore": false
}
```

`seq`, `deviceId` and `receivedAt` are server-assigned; they are ignored on input.

## Not built yet

**Sharing.** Ops are strictly per-user. Collaborative and public playlists need a grant
model that does not exist — do not design the client as though another user's ops can
arrive today.

**Retention.** `play_events` is append-only and nothing prunes it. A new device pulling
from `since=0` replays the entire listening history. Fine now; worth revisiting before
the log is large.

**Playlist export.** Aoide playlists do not appear in Jellyfin's own UI yet. When that
lands it will be strictly one-way, and an edit made in Jellyfin's UI will be an explicit
user-initiated import, never an automatic read-back.
