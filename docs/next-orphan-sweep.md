# Next: the artwork orphan sweep

Agreed with the app chat, designed, not built. Everything needed to implement it in one
pass is here. The repo is clean at 1.3.1.0.

## Why it exists

Changing a playlist's cover ten times leaves ten blobs in `playlist_images`. Nothing
reclaims them. Separately, there is currently **no way to see what is in the blob store
at all**, which cost a diagnosis in practice: when a cover failed to appear, "were the
bytes uploaded but the op never pushed?" could not be answered from the server.

So this endpoint earns its place twice — as storage hygiene, and as the missing window
into the store.

## The safety rule

**Report by default. Never reclaim automatically.**

The app chat arrived at the same design independently, and their reasoning is the one to
preserve: a blob that looks unreferenced may still be needed.

The sharp version of the race is not a device that has not *pulled* — it is a device that
uploaded a blob and has not *pushed the op yet*. The contract tells clients to upload
bytes before pushing the row that references them, so between those two steps the blob is
genuinely unreferenced and genuinely still needed. Deleting there breaks a cover on every
device, permanently.

**Grace period: 30 days**, measured from `playlist_images.created_at`. The app chat is
matching this number; if it changes, tell them. The window is normally seconds, so 30 days
is not about the common case — it covers a device that uploaded, went offline, and returns
weeks later with the op still queued. The asymmetry justifies the generosity: a wrongly
reclaimed blob is a permanently broken cover, a retained one is a few hundred KB.

## Shape

```
GET  /aoide/images/orphans                          → report only, never deletes
POST /aoide/images/orphans/reclaim?olderThanDays=30 → explicit, opt-in
```

Both `[Authorize]`, user-scoped, camelCase wire names via `[JsonPropertyName]` — see the
1.0.2.0 casing bug in the README before assuming the default serialiser is fine.

Suggested report body:

```json
{ "totalBlobs": 14, "totalBytes": 3221225,
  "orphans": [ { "imageHash": "8581e780…", "sizeBytes": 20480, "ageDays": 41,
                 "reclaimable": true } ],
  "orphanBytes": 184320, "graceDays": 30 }
```

`reclaimable` is `ageDays > graceDays`. Reporting orphans inside the grace window too is
deliberate: it answers "did the bytes arrive?" for a cover uploaded minutes ago, which is
the diagnostic half of this feature.

## Referenced set

Build it from current playlist state, not raw ops:

```csharp
var ops = await _repository.ReadPlaylistOpsAsync(userId, ct);
var referenced = PlaylistProjection.Build(ops)
    .Where(p => !p.Deleted)
    .Select(p => p.ImageHash)
    .Where(h => !string.IsNullOrEmpty(h))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
```

`PlaylistProjection` already reads `image_hash` and `imageHash`, and already collapses to
the winning row per playlist. Use it rather than re-deriving — the naming-convention trap
that broke export in 1.3.1.0 lives in exactly this kind of hand-rolled payload read.

Take hashes from live playlists only. A soft-deleted playlist's cover is genuinely
unreferenced; the grace period is what protects it, not the tombstone.

## Repository work

Add to `PlaylistImageRepository`:

- `ListAsync(userId, ct)` → hash, size, created_at for every blob of that user.
- `DeleteAsync(userId, IReadOnlyCollection<string> hashes, ct)` → one `BEGIN IMMEDIATE`
  transaction, parameterised, scoped by `user_id` in the WHERE clause so a hash cannot
  reach across accounts.

No schema change. `playlist_images` already carries `size` and `created_at`.

## Tests worth having

- A blob referenced by a live playlist is never an orphan.
- A blob referenced only by a soft-deleted playlist *is* an orphan.
- A blob younger than the grace period reports `reclaimable: false`.
- Two playlists sharing one hash: still referenced when only one is deleted. This is the
  case content addressing creates and a naive per-playlist sweep would get wrong.
- `reclaim` deletes only what the report marked reclaimable.
- Reclaim is scoped by user: another account's identical hash survives.

## Verify against the rig

The pattern used throughout this project, and it has caught what unit tests could not —
the PascalCase responses, the vanishing provider stamps, the camelCase payloads:

```bash
docker run -d --name jf11 -p 8098:8096 \
  -v "$PWD/rig/config:/config" -v "$PWD/rig/cache:/cache" jellyfin/jellyfin:10.11.11
```

Complete the startup wizard over the API, authenticate with `POST /Users/AuthenticateByName`
for a **user** token — an admin API key returns 401, since ops are per-user — then upload a
blob, push a playlist referencing it, and confirm it is not reported. Drop the reference and
confirm it is.

## Then

Update `docs/client-integration.md` and the README endpoint table, and release as a minor
bump. Tell the app chat the grace period landed at 30 days so their `orphanedImages` matches.
