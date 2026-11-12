#!/usr/bin/env bash
# Fetches the W3C XSLT 3.0 conformance suite this repo measures against. QT3 is measured
# in the XQuery repo, so it is not fetched here.
#
# The suite is pinned to an exact revision. That pin is the point: a conformance
# percentage is not reproducible, and two runs are not comparable, unless the suite
# revision is known. Bump a SHA deliberately, in its own commit, and re-baseline.
#
# The suite is never committed (~470 MB, ~30,000 files) and never copied into the
# test output — see ConformanceSuites.cs. Point the tests at it with:
#
#   export XSLT30_TEST_SUITE=<repo>/tests/PhoenixmlDb.Conformance.Tests/TestData/xslt30-test
#
# Without it, the fixtures fall back to the same path beside the test assembly,
# and if neither exists the suite reports as unavailable and the tests skip quietly.
set -euo pipefail

XSLT30_SHA=fddf1cf920087e791f13315d68dfbe874d97dc56

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$ROOT/tests/PhoenixmlDb.Conformance.Tests/TestData"

fetch() {
  local name=$1 url=$2 sha=$3
  local dir="$DEST/$name"

  if [ -d "$dir/.git" ] && [ "$(git -C "$dir" rev-parse HEAD 2>/dev/null)" = "$sha" ]; then
    echo "$name: already at $sha"
    return
  fi

  echo "$name: fetching $sha"
  mkdir -p "$dir"
  git -C "$dir" init -q
  git -C "$dir" remote add origin "$url" 2>/dev/null || git -C "$dir" remote set-url origin "$url"
  # Fetch the one commit rather than cloning history: these repos are large and no
  # test needs their past.
  git -C "$dir" fetch --depth 1 origin "$sha" -q
  git -C "$dir" checkout -q FETCH_HEAD
  echo "$name: at $(git -C "$dir" rev-parse --short HEAD)"
}

mkdir -p "$DEST"
fetch xslt30-test https://github.com/w3c/xslt30-test.git "$XSLT30_SHA"

cat <<EOF

Suite ready under $DEST
Run conformance with:
  XSLT30_TEST_SUITE="$DEST/xslt30-test" \\
  dotnet test tests/PhoenixmlDb.Conformance.Tests
EOF
