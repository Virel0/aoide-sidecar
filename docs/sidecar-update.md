# Sidecar update for the app chat

Everything that changed on the server since the last sync-up, and the three things that
need action on the client. Current release: **1.7.0.0**. Full contract is in
[client-integration.md](client-integration.md).

---

## 1. Your open question is already answered: `receivedAt` is on every op

`correctForSkew` can be switched on today. **No server change is needed and none ever
was** — the sidecar has attached its own receipt time to every pulled op since 1.0.0.0.

```json
{ "opId": "…", "createdAt": 1786052177201, "receivedAt": 1786065442334, "seq": 1, … }
```

`createdAt` is the writing device's clock. `receivedAt` is the server's, stamped on
arrival. A `createdAt` far ahead of `receivedAt` is a broken clock rather than a genuinely
newer write.

One detail: ops pushed in the same batch share a receipt time, because they arrived
together. Judge skew per batch, not per op.

---

## 2. Action needed

### `authorUserId` on every op

Pull can now return ops **authored by another user** — collaborative playlists. Every op
carries `authorUserId`, which differs from your user only on a shared playlist. Use it to
attribute an edit; do not treat a foreign op as your own device's.

### One rejection is no longer permanent

Everywhere else in the contract, an op in `rejected` will never be accepted, so you
quarantine it. There is now exactly one exception:

```
Playlist 'p1' belongs to another user and is not shared with you for editing.
```

That can become valid if access is granted, and it usually means the UI let someone edit
a playlist whose share was revoked while their change sat queued. **Do not retry blindly,
and do not silently discard.** Refresh `/aoide/shares`; if access really is gone, surface
it rather than dropping the user's edit without a word.

### Retention now depends on clients pulling

The server records a per-device cursor from `GET /pull`, and pruning is bounded by the
lowest cursor among devices seen recently. A device that pushes but never pulls is
invisible to that guard, and pruning would not wait for it.

---

## 3. New: handing playback between a user's devices

```
GET /aoide/queue
```

One entry per device, most recently updated first. Offer the first that is not
`isCurrentDevice`.

**Nothing new to write** — keep pushing `queue_state` exactly as now. Two things to know:

- **Superseded queue rows are compacted away on push.** `queue_state` is one row per
  device, replaced whole, so all but the latest are overwritten values rather than
  history. Pull will not necessarily show every intermediate state; the current one always
  survives. Push as often as playback genuinely changes — the log will not grow.
- **Judge freshness on `ageSeconds`/`receivedAt`, not `updatedAt`.** Those come from the
  server's clock on both sides, so a device with a wrong one cannot claim to be the most
  recently used and win every handover.

---

## 4. New: collaborative playlists

```
GET    /aoide/shares
POST   /aoide/shares  { playlistId, granteeUserId, canEdit }
DELETE /aoide/shares/{playlistId}/{granteeUserId}
```

A playlist belongs to whoever first pushed it. The owner invites another Jellyfin user;
from then on both push edits and both receive the other's **through the pull they already
make** — one loop, one cursor, no second sync path. Only the owner can share; either side
can revoke.

Verified end to end with two real Jellyfin accounts: unshared edits refused, shared edits
accepted and visible to the owner, revoke cutting off future changes.

- **Only playlists travel.** `play_events`, `likes` and `queue_state` carry no playlist
  id, so nothing routes them across. Sharing a playlist shares the playlist, not the
  account.
- **Revoking is not retroactive.** It stops the owner's future changes reaching the
  collaborator. It does not withdraw ops the collaborator already wrote, and cannot reach
  into their device. Drop the playlist locally when it leaves `/aoide/shares`.
- Conflicts behave as they always did — fractional indices, last-writer-wins per field.
  Two people reordering at once is the case fractional indexing already handles.

---

## 5. New: artwork housekeeping

```
GET  /aoide/images/orphans
POST /aoide/images/orphans/reclaim?olderThanDays=30
```

Report-then-reclaim, matching your `orphanedImages` design. **Grace period is 30 days** —
match it.

Reclaiming never happens on its own, and `olderThanDays` can only make a sweep *more*
cautious; a smaller value is raised to the configured grace. The reason is the one you
identified: clients upload bytes before pushing the row that names them, so in between, a
blob is genuinely unreferenced and genuinely still needed.

The report doubles as **"did my upload arrive?"** — a blob pushed moments ago appears with
`ageDays: 0`. That is why orphans inside the grace window are listed rather than hidden.

Artwork shared by two playlists stays alive while either does; content addressing makes
sharing normal, and a per-playlist sweep would get it wrong.

---

## 6. New: play-history retention

```
GET  /aoide/retention
POST /aoide/retention/prune?olderThanDays=90
```

Only `play_events` is prunable. Every other entity is current state — a playlist's row is
the sole description of that playlist, so removing it would not trim history, it would
delete the playlist for anyone syncing fresh.

Manual only. The honest trade: a device that has **never** synced still receives a
shortened history after a prune. No cursor can protect a device the server has never met.

---

## 7. The server now targets Jellyfin 10.11

`TaskTriggerInfo.Type` is a string in 10.10 and an enum in 10.11, so a scheduled task
built against 10.10 would have thrown at runtime on the real server. The plugin now
targets 10.11 / .NET 9 — what actually runs — which turns that class of latent breakage
into compile errors. No client-visible change.

---

## 8. One thing for you, from real data

**Import is not idempotent.** On the live server, "Beach" and "Bon Sons" each became *two*
Aoide playlists with different ids after a re-import — one later soft-deleted by hand.
Deduping on `sourceJellyfinId` fixes it, and it is the same key you already store.

Credit where due: `sourceJellyfinId` was the right call and it is why nine playlists —
including two pairs sharing a name — adopted cleanly with zero duplicates. Name matching
would have been ambiguous on exactly those.

---

## Endpoint summary

| endpoint | since | what it is for |
| -------- | ----- | -------------- |
| `POST /aoide/sync/push` | 1.0.0.0 | append ops |
| `GET /aoide/sync/pull` | 1.0.0.0 | read ops; also records this device's cursor |
| `GET /aoide/sync/status` | 1.0.2.0 | storage diagnostics |
| `PUT`/`GET`/`HEAD /aoide/images/{sha256}` | 1.1.0.0 | artwork blobs |
| `POST /aoide/export/playlists` | 1.2.0.0 | mirror playlists into Jellyfin |
| `GET`/`POST /aoide/images/orphans…` | 1.4.0.0 | artwork housekeeping |
| `GET`/`POST /aoide/retention…` | 1.5.0.0 | play-history retention |
| `GET`/`POST`/`DELETE /aoide/shares` | 1.6.0.0 | collaborative playlists |
| `GET /aoide/queue` | 1.7.0.0 | resume across devices |

All take the same Jellyfin user token. An admin API key returns 401 — it carries no user
id, and every op is scoped to a user.
