#!/usr/bin/env bash
#
# install.sh: point this repo's git at the tracked hooks in .githooks/.
# Run once per clone: bash .githooks/install.sh
#
# Sets core.hooksPath so git runs .githooks/pre-commit on every commit (the
# hooks are committed; .git/hooks/ is not, so this is how a fresh clone opts in).

set -euo pipefail
repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"
git config core.hooksPath .githooks
chmod +x .githooks/pre-commit .githooks/content-audit.sh .githooks/release-check.sh 2>/dev/null || true
if [ ! -f .githooks/denylist.local ] && [ -f .githooks/denylist.example ]; then
  cp .githooks/denylist.example .githooks/denylist.local
  echo "Created .githooks/denylist.local from the example. Fill in real terms."
fi
echo "Installed: git will run .githooks/pre-commit on every commit."
