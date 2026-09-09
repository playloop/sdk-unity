#!/usr/bin/env bash
#
# content-audit.sh: full-TREE scan for content that should not appear in a
# public repo. The pre-commit hook only checks each commit's additions; this
# checks the WHOLE tracked tree at once, which is what you want before a batch
# push or a release. It greps every tracked file for the denylist plus email
# addresses and absolute home paths, and exits non-zero with a report if
# anything matches. Run from anywhere in the repo: bash .githooks/content-audit.sh
#
# The denylist lives in .githooks/denylist.local (gitignored). Copy
# .githooks/denylist.example to .githooks/denylist.local and fill in your terms.

set -euo pipefail
repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

denylist_file="$repo_root/.githooks/denylist.local"
if [ ! -f "$denylist_file" ]; then
  echo "REFUSED: $denylist_file is missing. Copy .githooks/denylist.example to"
  echo ".githooks/denylist.local and fill in your terms (it is gitignored)."
  exit 2
fi
DENYLIST="$(grep -v '^\s*#' "$denylist_file" | grep -v '^\s*$' | paste -sd'|' -)"
[ -z "$DENYLIST" ] && { echo "REFUSED: $denylist_file has no terms."; exit 2; }

EMAIL='[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'
HOMEPATH='/Users/[A-Za-z0-9._-]+'
# Reserved documentation domains (RFC 2606/6761): a stand-in in a test, not real.
EXAMPLE_DOMAINS='@(example|test|invalid|localhost)\.(com|org|net)|@example$'
# Machine-generated lockfiles carry upstream author emails; exclude from email scan.
LOCK_EXCLUDES=(':(exclude)*package-lock.json' ':(exclude)*yarn.lock' ':(exclude)*pnpm-lock.yaml' ':(exclude)*Cargo.lock' ':(exclude)*poetry.lock' ':(exclude)*uv.lock')

hits=0
report=""
scan() {
  local label="$1" pattern="$2" exclude="${3:-}"; shift $(( $# > 3 ? 3 : $# ))
  local out
  out="$(git grep -I -n -i -E "$pattern" -- . "$@" 2>/dev/null || true)"
  if [ -n "$exclude" ]; then out="$(printf '%s\n' "$out" | grep -ivE "$exclude" || true)"; fi
  if [ -n "$out" ]; then report="${report}
== ${label} ==
${out}
"; hits=1; fi
}

scan "denylisted term" "$DENYLIST"
scan "email address" "$EMAIL" "$EXAMPLE_DOMAINS" "${LOCK_EXCLUDES[@]}"
scan "absolute home path" "$HOMEPATH"

if [ "$hits" -ne 0 ]; then
  echo "CONTENT AUDIT FAILED. The tracked tree contains:"
  printf '%s\n' "$report"
  echo "Remove the matches above before publishing."
  exit 1
fi
echo "Content audit clean: no denylisted terms, emails, or home paths in the tracked tree."
exit 0
