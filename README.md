# Aoide Sidecar

A Jellyfin plugin that relays the Aoide curation store's op log between a user's devices.

Jellyfin stays the source of truth for files, tags, artwork and which music exists at
all. The curation store owns playlists, folders, likes, play history, queue state and
smart-playlist rules. This plugin is the Phase 2 half of that split: it moves ops
between devices and does nothing else.

It is deliberately dumb. It does not merge, resolve conflicts, evaluate smart rules, or
understand what a playlist is. All of that happens on the client, against local SQLite.

## Status

Implemented: the sync contract — `push`, `pull`, auth, storage, validation.

Not yet implemented: one-way export of playlists as native Jellyfin playlists, and
retention for `play_events`. Both are noted under [Next](#next).

## Build

```bash
dotnet test
```

```bash
./scripts/package.sh 1.0.0.0
```

The build targets .NET 8 and `Jellyfin.Controller` 10.10.7. The plugin ships as a single
DLL: `Microsoft.Data.Sqlite` is referenced with `ExcludeAssets="runtime"` because the
server already loads it via EF Core, and a second copy would arrive with its own
`SQLitePCLRaw` that has no native provider registered against it.

## Install

This repo doubles as a Jellyfin plugin repository. Add it once, in
Dashboard → Plugins → Repositories → **+**:

```
https://raw.githubusercontent.com/Virel0/aoide-sidecar/main/manifest.json
```

The plugin then appears under Dashboard → Plugins → Catalog. Install it and restart
Jellyfin. Every later version is a click in the same place — nothing to copy to the
server.

Confirm it loaded under Dashboard → Plugins, and check the log for
`Aoide sync database ready at /config/data/aoide-sidecar/aoide-sync.db`.

### Releasing a new version

```bash
./scripts/release.sh 1.0.1.0 "What changed"
```

That runs the tests, builds, tags, uploads the zip to GitHub Releases, and rewrites
`manifest.json` with the new version and its MD5. Jellyfin verifies that checksum on
download, so the manifest must always be updated by this script rather than by hand.

Versions are four-part because Jellyfin parses them as `System.Version`.

### Installing by hand instead

If you would rather not wait for Jellyfin to re-read the manifest, copy the built
folder into the config volume's `plugins/` directory over ssh:

```bash
./scripts/package.sh 1.0.0.0 && scp -r "artifacts/Aoide Sidecar_1.0.0.0" user@server:/path/to/jellyfin/config/plugins/
```

```bash
ssh user@server 'docker compose -f /path/to/docker-compose.yml restart jellyfin'
```

The plugin folder must keep the `Name_Version` shape, and the DLL must sit directly
inside it — that is the layout Jellyfin's own installer produces.

The database lives in the plugin's data folder inside the config volume, so it is
covered by whatever already backs that volume up. It is the only copy of a user's
curation history that is not on a device.

## The contract

Both endpoints require a normal Jellyfin token:

```
Authorization: MediaBrowser Token="<the user's Jellyfin access token>"
```

Running in-process is what makes this cheap: the authenticated user arrives with the
request instead of costing a round-trip to `/Users/Me`. The plugin runs no account
system of its own — a second set of credentials would mean two logins per friend and a
password database this has no business owning.

Ops are scoped to the authenticated user. There is currently no sharing between users;
collaborative playlists will need an explicit grant rather than a widened query.

### `POST /aoide/sync/push`

```json
{
  "deviceId": "…",
  "ops": [
    { "opId": "uuid", "entity": "playlists", "entityId": "uuid",
      "operation": "upsert", "payload": { }, "createdAt": 1754500000000 }
  ]
}
```

```json
{ "accepted": ["uuid"], "cursor": 12345 }
```

`accepted` lists the op ids the server durably holds, **including ones it had already
seen** — re-pushing is accepted and ignored, which is what makes a retry after a
timeout safe.

`cursor` is the server's head sequence for this user. It is informational, **not a pull
cursor**: ops from other devices may sit below it unseen. Only a pull advances the pull
cursor.

An op that fails validation is reported in a `rejected` array with a reason and is
omitted from `accepted`. Valid ops in the same batch still land. This is deliberate —
failing the whole batch would let one malformed op wedge a client's queue forever, and
dropping it silently would lose it without a trace. An op that appears in `rejected`
will never be accepted, so quarantine it rather than retrying.

A batch larger than the configured maximum is rejected outright with 400, since the
client can simply split it.

### `GET /aoide/sync/pull?since=<cursor>&limit=500`

```json
{ "ops": [], "cursor": 12400, "hasMore": true }
```

Ops come back in sequence order, each carrying `seq`, `deviceId` and `receivedAt`
alongside the client's own fields. Store `cursor` only after applying the whole batch,
so an interrupted sync replays rather than skips.

Push before pull. A client's own ops come back with a sequence number, which is how it
learns they were durably accepted.

`hasMore` is exact — the query reads one row past the limit — so a client is never sent
back for a page that turns out to be empty.

## What the server guarantees

**Ordering.** SQLite permits one write transaction at a time, so commits are totally
ordered, and `seq` is assigned by AUTOINCREMENT inside the writing transaction. A
reader that observes sequence N is therefore guaranteed to already see everything below
it, which is what makes a monotonic cursor safe: a puller can never skip an op that was
still in flight. Rolled-back transactions burn a sequence, so the log has gaps; that is
harmless, because the cursor means "everything up to here", not "the next number".

**Idempotency.** `(user_id, op_id)` is unique. The pair is scoped by user rather than
global because op ids are client-generated, and a shared namespace would let one
account silently void another's op by pushing the same id first.

**Durability.** `synchronous=FULL`, one fsync per push batch. A client marks its ops
synced on the strength of a push response and will not send them again, so a commit
lost to power failure is lost user data.

**Opacity.** Payloads are stored verbatim and never parsed. Only the envelope is
validated. This is what lets the curation-store schema gain a column without a
coordinated server deploy.

**Clock skew.** Every op is stamped with a server `receivedAt` next to the client's
`createdAt`, so a device with a badly wrong clock cannot win every field-level conflict
forever. Conflict resolution itself is the client's job.

`tracks` is rejected with an explanatory message. It is a per-device cache rebuilt from
each client's own Jellyfin connection, and keeping it out of the log is what keeps a
full history sync small.

## Next

**Playlist export.** For playlists to show up in Jellyfin's own UI they need exporting
as native Jellyfin playlists. That export must stay strictly one-way. If Jellyfin
becomes a second writer, an exported edit returns as an inbound change and exports
again — playlists that grow duplicates or oscillate. Mark exported playlists with a
provider id, and treat an edit made in Jellyfin's UI as an explicit user-initiated
import, never an automatic read-back. Smart playlists export as a snapshot of their
current contents; Jellyfin has no concept of a rule.

**Retention.** `play_events` is append-only and grows without bound. Nothing prunes it
yet. A device syncing from scratch replays the entire history, so this wants a decision
before the log gets large — most likely a scheduled task, since deleting ops the client
still needs would silently truncate someone's listening history.

**Sharing.** Collaborative and public playlists need ops readable across users. The
per-user scope in `ReadAsync` is the single place that has to change, and it should
become an explicit grant table rather than a widened query.
