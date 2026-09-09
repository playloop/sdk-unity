#!/usr/bin/env bash
#
# release-check.sh: the one command to run before this repo ships anywhere
# public (a batch push, a release). It answers two questions the per-commit
# hook cannot answer alone:
#
#   SAFE:     does the whole tracked tree contain anything it should not
#             (denylisted terms, emails, home paths)?
#   ACCURATE: do the docs still point at files that exist?
#
# Run from anywhere in the repo: bash .githooks/release-check.sh

set -euo pipefail
repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

fail=0
say_fail() { echo "FAIL: $*"; fail=1; }

# --- 1. Content audit --------------------------------------------------------
if bash .githooks/content-audit.sh; then :; else say_fail "content audit found matches (see above)"; fi

# --- 2. Whole-tree em-dash scan ----------------------------------------------
# Em dash only (en dash is legit typography for ranges). Byte escape so this
# file never contains the char it bans.
EMDASH="$(printf '\342\200\224')"
dash_hits="$(git grep -I -n -F -e "$EMDASH" -- . 2>/dev/null || true)"
if [ -n "$dash_hits" ]; then
  say_fail "em dash in the tracked tree:"
  printf '%s\n' "$dash_hits" | sed 's/^/  /'
fi

# --- 3. Dead relative markdown links ----------------------------------------
# Links inside fenced code blocks are examples, not links; skip those lines.
dead_links=""
while IFS= read -r md; do
  targets="$(awk '/^```/ { fence = !fence; next } !fence { print }' "$md" \
    | grep -oE '\]\([^)#]+[^)]*\)' | sed 's/^](//; s/)$//; s/#.*//' || true)"
  while IFS= read -r t; do
    [ -z "$t" ] && continue
    case "$t" in http://*|https://*|mailto:*) continue ;; esac
    dir="$(dirname "$md")"
    if [ ! -e "$dir/$t" ] && [ ! -e "$t" ]; then dead_links="${dead_links}
  $md -> $t"; fi
  done <<< "$targets"
done < <(git ls-files '*.md')
if [ -n "$dead_links" ]; then say_fail "dead relative links:${dead_links}"; fi

echo ""
if [ "$fail" -ne 0 ]; then
  echo "RELEASE CHECK FAILED. Fix the items above before anything ships."
  exit 1
fi
echo "Release check clean: no denylisted terms, no em dashes, links resolve."
exit 0
