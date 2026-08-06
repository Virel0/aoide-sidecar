#!/usr/bin/env bash
# Proves the sidecar is actually working, end to end, against a live server.
#
#   JELLYFIN_URL=https://jellyfin.example.com \
#   JELLYFIN_TOKEN=<a user access token> \
#   ./scripts/verify.sh
#
# This writes real ops to the log. They are append-only and cannot be deleted, so run
# it before any device has synced, then wipe the database (see the end of the output).
set -euo pipefail

: "${JELLYFIN_URL:?set JELLYFIN_URL, e.g. https://jellyfin.example.com}"
: "${JELLYFIN_TOKEN:?set JELLYFIN_TOKEN to a *user* access token, not an API key}"

URL="${JELLYFIN_URL%/}"
AUTH="Authorization: MediaBrowser Token=\"$JELLYFIN_TOKEN\""
DEVICE="aoide-verify-script"
STAMP="$(date +%s)"
OP_ID="verify-$STAMP"

pass() { printf '  \033[32mok\033[0m  %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$1"; exit 1; }

req() { # method path [body] -> sets STATUS and BODY
    local method="$1" path="$2" body="${3:-}"
    local out
    if [[ -n "$body" ]]; then
        out=$(curl -sS -X "$method" "$URL$path" -H "$AUTH" \
            -H 'Content-Type: application/json' -d "$body" -w '\n%{http_code}')
    else
        out=$(curl -sS -X "$method" "$URL$path" -H "$AUTH" -w '\n%{http_code}')
    fi
    STATUS="${out##*$'\n'}"
    BODY="${out%$'\n'*}"
}

jq_get() { python3 -c "import json,sys;print(json.loads(sys.stdin.read())$1)"; }

echo
echo "1. Server reachable"
req GET "/System/Info/Public"
[[ "$STATUS" == "200" ]] || fail "GET /System/Info/Public returned $STATUS"
pass "Jellyfin $(echo "$BODY" | jq_get "['Version']")"

echo
echo "2. Plugin loaded and token accepted"
req GET "/aoide/sync/pull?since=0&limit=1"
case "$STATUS" in
    200) pass "GET /aoide/sync/pull -> 200" ;;
    404) fail "404 — the plugin did not load. Check Dashboard > Plugins, and the server log." ;;
    401) fail "401 — the token was rejected. An API key will not work here; use a user access token." ;;
    *)   fail "unexpected status $STATUS: $BODY" ;;
esac

START_CURSOR=$(echo "$BODY" | jq_get "['cursor']")
pass "starting cursor: $START_CURSOR"

echo
echo "3. Push an op"
PAYLOAD=$(cat <<EOF
{"deviceId":"$DEVICE","ops":[{
  "opId":"$OP_ID",
  "entity":"playlists",
  "entityId":"verify-playlist-$STAMP",
  "operation":"upsert",
  "payload":{"id":"verify-playlist-$STAMP","name":"Sidecar verification","deleted":0,
             "updated_at":${STAMP}000,"origin_device":"$DEVICE","sort_index":"a0"},
  "createdAt":${STAMP}000
}]}
EOF
)
req POST "/aoide/sync/push" "$PAYLOAD"
[[ "$STATUS" == "200" ]] || fail "push returned $STATUS: $BODY"
ACCEPTED=$(echo "$BODY" | jq_get "['accepted']")
[[ "$ACCEPTED" == "['$OP_ID']" ]] || fail "expected the op to be accepted, got: $BODY"
CURSOR_AFTER_PUSH=$(echo "$BODY" | jq_get "['cursor']")
pass "accepted, cursor now $CURSOR_AFTER_PUSH"

echo
echo "4. Pull it back"
req GET "/aoide/sync/pull?since=$START_CURSOR&limit=100"
[[ "$STATUS" == "200" ]] || fail "pull returned $STATUS"
COUNT=$(echo "$BODY" | jq_get "['ops'].__len__()")
[[ "$COUNT" == "1" ]] || fail "expected 1 op, got $COUNT: $BODY"
pass "op came back with seq $(echo "$BODY" | jq_get "['ops'][0]['seq']"), \
receivedAt $(echo "$BODY" | jq_get "['ops'][0]['receivedAt']")"
pass "payload survived: $(echo "$BODY" | jq_get "['ops'][0]['payload']['name']")"

echo
echo "5. Re-push is idempotent (this is what makes retry-after-timeout safe)"
req POST "/aoide/sync/push" "$PAYLOAD"
CURSOR_AGAIN=$(echo "$BODY" | jq_get "['cursor']")
[[ "$CURSOR_AGAIN" == "$CURSOR_AFTER_PUSH" ]] \
    || fail "re-push moved the cursor $CURSOR_AFTER_PUSH -> $CURSOR_AGAIN; it should not have"
pass "duplicate accepted and ignored, cursor still $CURSOR_AGAIN"

echo
echo "6. A bad op is rejected without wedging the batch"
req POST "/aoide/sync/push" "$(cat <<EOF
{"deviceId":"$DEVICE","ops":[
  {"opId":"verify-bad-$STAMP","entity":"tracks","entityId":"x","operation":"upsert",
   "payload":{"jellyfin_id":"x"},"createdAt":${STAMP}000},
  {"opId":"verify-good-$STAMP","entity":"likes","entityId":"like-$STAMP","operation":"upsert",
   "payload":{"id":"like-$STAMP","jellyfin_id":"abc","liked":1,"deleted":0,
              "updated_at":${STAMP}000,"origin_device":"$DEVICE"},"createdAt":${STAMP}000}
]}
EOF
)"
[[ "$STATUS" == "200" ]] || fail "push returned $STATUS: $BODY"
echo "$BODY" | grep -q "verify-good-$STAMP" || fail "the valid op should still have been accepted"
echo "$BODY" | grep -q "per-device cache" || fail "expected the tracks rejection reason"
pass "tracks rejected, the valid op alongside it still landed"

echo
echo "7. Unauthenticated requests are refused"
UNAUTH=$(curl -sS -o /dev/null -w '%{http_code}' "$URL/aoide/sync/pull?since=0")
[[ "$UNAUTH" == "401" ]] || fail "expected 401 without a token, got $UNAUTH"
pass "401 without a token"

cat <<EOF

All checks passed. The sidecar is live.

This left verification ops in the log. Nothing consumes them yet, but the first device
to sync would replay them, so clear the database now:

  ssh <server> 'rm -f /path/to/config/data/aoide-sidecar/aoide-sync.db*'
  ssh <server> 'docker compose -f /path/to/docker-compose.yml restart jellyfin'

The schema is recreated empty on the next request.
EOF
