#!/usr/bin/env bash
# Prints a git TREE id for the current working tree (tracked + untracked, honouring .gitignore).
# Uses a throwaway index: creates no commit, moves no ref, leaves the real index untouched.
set -euo pipefail
root=$(git rev-parse --show-toplevel)
cd "$root"
idx="$root/.superpowers/tmp/snap-index-$$"
cp "$(git rev-parse --git-dir)/index" "$idx" 2>/dev/null || true
GIT_INDEX_FILE="$idx" git add -A . >/dev/null 2>&1
GIT_INDEX_FILE="$idx" git write-tree
rm -f "$idx"
