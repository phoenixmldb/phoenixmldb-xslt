#!/usr/bin/env bash
# Refuses to pack a release whose engine pins are not the release version.
#
# This is the check that would have stopped the artifact Martin Honnen was given. The published
# PhoenixmlDb.Xslt 1.6.13 depends on PhoenixmlDb.XQuery 1.6.12, because the XSLT tag was cut
# before the XQuery 1.6.13 push. Nothing was "wrong" at pack time — the pin was the latest
# published version — so a staleness check cannot catch it. Only an equality check can:
#
#   if this train is 1.6.14, every train-locked pin must read 1.6.14.
#
# The deeper reason it matters here: this repo's conformance suite runs against XQuery SOURCE
# via the src/PhoenixmlDb.XQuery symlink, while the shipped tool embeds the XQuery PACKAGE. Test
# one thing, ship another. Locking the train is what makes the tested and shipped engine the
# same engine.
#
# Mark a pin with "check-pins: train-locked" in the comment above it to opt in. Packages on
# their own cadence (Core) are deliberately not locked; scripts/check-pins.sh still stops those
# from falling behind what is published.
set -uo pipefail

version="${1:-}"
props="${2:-Directory.Packages.props}"
[ -n "$version" ] || { echo "usage: check-release-train.sh <release-version> [props]"; exit 2; }
version="${version#v}"
[ -f "$props" ] || { echo "check-release-train: no $props here"; exit 2; }

fail=0 locked=0
while read -r id ver; do
  if ! grep -B8 "Include=\"$id\"" "$props" | grep -q 'check-pins: train-locked'; then
    echo "free  $id $ver (not train-locked)"
    continue
  fi
  locked=$((locked+1))
  if [ "$ver" = "$version" ]; then
    echo "ok    $id $ver == train $version"
  else
    echo "FAIL  $id is pinned $ver but this train is $version"
    echo "      Packing now ships a tool whose engine is not the one this release tested."
    fail=1
  fi
done < <(grep -oE '<PackageVersion Include="(PhoenixmlDb[^"]*)" Version="([^"]+)"' "$props" \
         | sed -E 's/.*Include="([^"]+)" Version="([^"]+)".*/\1 \2/')

if [ "$locked" -eq 0 ]; then
  echo "check-release-train: no train-locked pins found — mark them with 'check-pins: train-locked'"
  exit 2
fi
exit $fail
