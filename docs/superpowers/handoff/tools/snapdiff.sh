#!/usr/bin/env bash
# Usage: snapdiff.sh BASE_TREE HEAD_TREE OUTFILE  -> stat + full diff (-U10) between two trees
set -euo pipefail
base=$1; head=$2; out=$3
root=$(git rev-parse --show-toplevel)
cd "$root"
{ echo "# Diff $base..$head"; echo; git diff --stat "$base" "$head"; echo; git diff -U10 "$base" "$head"; } > "$out"
echo "wrote $out: $(wc -c < "$out") bytes"
