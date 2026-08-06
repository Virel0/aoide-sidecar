#!/usr/bin/env bash
# Verifies a live sidecar install end to end, without writing anything to the op log.
#
#   export JELLYFIN_URL=http://your-server:8096
#   export JELLYFIN_TOKEN=...           # Dashboard -> API Keys, or a client's token
#   ./scripts/smoke-test.sh
#
# The write path is exercised with an op the server is built to reject (entity
# "tracks"), so this proves routing, auth, JSON parsing, validation and the response
# shape while storing zero ops. A test that pushed a real op would leave junk in the
# log that every client would then dutifully apply.
set -euo pipefail

URL="${JELLYFIN_URL:?set JELLYFIN_URL, e.g. http://your-server:8096}"
URL="${URL%/}"
TOKEN="${JELLYFIN_TOKEN:-}"

pass() { printf '  \033[32mok\033[0m   %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$1"; FAILED=1; }
FAILED=0

echo
echo "Sidecar smoke test against $URL"
echo

# 1. Route is registered and authentication is enforced.
echo "1. unauthenticated pull should be refused"
CODE=$(curl -s -o /dev/null -w '%{http_code}' "$URL/aoide/sync/pull?since=0" || true)
case "$CODE" in
    401) pass "401 — route is registered and auth is enforced" ;;
    404) fail "404 — Jellyfin did not register the plugin's routes. Is the plugin loaded and the server restarted?" ;;
    200) fail "200 — the endpoint answered without a token, which it must never do" ;;
    000) fail "no response — is $URL reachable from here?" ;;
    *)   fail "unexpected status $CODE" ;;
esac

if [[ -z "$TOKEN" ]]; then
    echo
    echo "JELLYFIN_TOKEN not set — stopping after the unauthenticated check."
    echo "Set it to also verify pull and push."
    exit $FAILED
fi

AUTH="Authorization: MediaBrowser Token=\"$TOKEN\""

# 2. Authenticated pull returns the documented shape.
echo
echo "2. authenticated pull"
BODY=$(curl -s -H "$AUTH" "$URL/aoide/sync/pull?since=0&limit=1" || true)
if python3 -c "
import json,sys
d = json.loads(sys.argv[1])
assert isinstance(d['ops'], list), 'ops is not a list'
assert isinstance(d['cursor'], int), 'cursor is not an integer'
assert isinstance(d['hasMore'], bool), 'hasMore is not a boolean'
print('  cursor=%d ops=%d hasMore=%s' % (d['cursor'], len(d['ops']), d['hasMore']))
" "$BODY" 2>/dev/null; then
    pass "200 with a well-formed body"
else
    fail "unexpected body: $BODY"
fi

CURSOR_BEFORE=$(python3 -c "import json,sys;print(json.loads(sys.argv[1])['cursor'])" "$BODY" 2>/dev/null || echo "?")

# 3. Push, using an op the validator is built to refuse. Nothing is stored.
echo
echo "3. push validation (pushes a deliberately invalid op; nothing is stored)"
REQ='{"deviceId":"smoke-test","ops":[{"opId":"00000000-0000-0000-0000-00000000dead","entity":"tracks","entityId":"x","operation":"upsert","payload":{"id":"x"},"createdAt":1}]}'
BODY=$(curl -s -X POST -H "$AUTH" -H 'Content-Type: application/json' -d "$REQ" "$URL/aoide/sync/push" || true)
if python3 -c "
import json,sys
d = json.loads(sys.argv[1])
assert d['accepted'] == [], 'the invalid op was accepted: %r' % (d['accepted'],)
assert len(d['rejected']) == 1, 'expected exactly one rejection, got %r' % (d['rejected'],)
print('  rejected: %s' % d['rejected'][0]['reason'])
" "$BODY" 2>/dev/null; then
    pass "the op was refused with a reason, as designed"
else
    fail "unexpected body: $BODY"
fi

# 4. The refused push must not have advanced the log.
echo
echo "4. the refused push left the log untouched"
BODY=$(curl -s -H "$AUTH" "$URL/aoide/sync/pull?since=0&limit=1" || true)
CURSOR_AFTER=$(python3 -c "import json,sys;print(json.loads(sys.argv[1])['cursor'])" "$BODY" 2>/dev/null || echo "?")
if [[ "$CURSOR_BEFORE" == "$CURSOR_AFTER" && "$CURSOR_AFTER" != "?" ]]; then
    pass "cursor unchanged at $CURSOR_AFTER"
else
    fail "cursor moved from $CURSOR_BEFORE to $CURSOR_AFTER"
fi

echo
if [[ "$FAILED" == "0" ]]; then
    echo "All checks passed. The sidecar is serving the sync contract."
else
    echo "Some checks failed."
fi
exit $FAILED
