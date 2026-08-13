#!/usr/bin/env bash
# Build a runnable DocBook xslTNG stylesheet tree without Java, Gradle, or network.
#
# xslTNG's src/main/xslt is NOT directly runnable: main.xsl imports three modules that
# Gradle generates at build time (param.xsl, VERSION.xsl, locale/*.xml). Each is produced
# by an XSLT transform, so our own engine can generate them — which doubles as a
# self-hosting smoke test of the engine.
#
# Usage: bootstrap-xsltng.sh [--all-locales]
set -euo pipefail

TNG="${TNG:-/repos/phoenixml/docbook/xslTNG}"
# Repo root, derived from this script's location — no hard-coded absolute path.
REPO="$(cd -P "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/../../../.." && pwd)"
XSLT_BIN="${XSLT_BIN:-$REPO/src/PhoenixmlDb.Xslt.Cli/bin/Debug/net10.0/xslt.dll}"
ALL_LOCALES=0
[[ "${1:-}" == "--all-locales" ]] && ALL_LOCALES=1

[[ -f "$XSLT_BIN" ]] || { echo "ERROR: engine not built: $XSLT_BIN" >&2
                          echo "  dotnet build $REPO/src/PhoenixmlDb.Xslt.Cli/PhoenixmlDb.Xslt.Cli.csproj -f net10.0" >&2
                          exit 1; }
cd "$TNG"
xslt() { dotnet "$XSLT_BIN" "$@"; }

echo "==> staging src/main/xslt -> build/xslt"
rm -rf build/xslt
mkdir -p build/xslt/locale
cp -r src/main/xslt/* build/xslt/
rm -f build/xslt/xspec-driver.xsl build/xslt/alt-*.xsl   # excluded by Gradle's copyXslt

echo "==> param.xsl  (tools/generate-parameters.xsl <- src/guide/xml/ref-params.xml)"
xslt tools/generate-parameters.xsl src/guide/xml/ref-params.xml -o build/xslt/param.xsl

# REGRESSION CHECK. generate-parameters.xsl is a stress test for namespace serialization: it
# uses xsl:namespace-alias to put the result root in the XSL namespace, then re-declares that
# same binding with <xsl:namespace name="xsl">. The engine used to emit BOTH, producing a
# duplicate xmlns:xsl and non-well-formed output (fixed in CreateNamespaceAsync — an identical
# prefix->URI rebinding is a no-op; only a DIFFERING uri is XTDE0430). Parse what we generated
# so a regression fails here, loudly, instead of surfacing as a confusing import error later.
# Scanned with a regex rather than an XML parser: the failure mode is precisely "same
# attribute name twice in one start tag", and this keeps the check dependency-free with no
# parser attack surface.
python3 - "$PWD/build/xslt/param.xsl" <<'PY'
import re, sys
src = open(sys.argv[1], encoding='utf-8').read()
for tag in re.findall(r'<[^!?/][^>]*>', src):
    names = re.findall(r'(?:^|\s)([\w:.-]+)\s*=\s*"', tag)
    dupes = {n for n in names if names.count(n) > 1}
    if dupes:
        sys.exit(f"    ERROR: duplicate attribute(s) {sorted(dupes)} in generated param.xsl\n"
                 f"    near: {tag[:120]}\n"
                 "    This means the namespace-dedup fix in CreateNamespaceAsync has regressed.")
print("    no duplicate attributes")
PY

echo "==> VERSION.xsl"
VER=$(sed -n 's/^xslTNGversion=//p' gradle.properties)
REF=$(git rev-parse --short HEAD 2>/dev/null || echo unknown)
xslt tools/version.xsl tools/version.xsl -o build/xslt/VERSION.xsl -p "version=$VER" -p "gitref=$REF" >/dev/null

echo "==> locales (modules/xform-locale.xsl)"
if [[ $ALL_LOCALES -eq 1 ]]; then
  for f in src/main/locale/*.xml; do
    xslt src/main/xslt/modules/xform-locale.xsl "$f" -o "build/xslt/locale/$(basename "$f")"
  done
  echo "    $(ls build/xslt/locale | wc -l) locales"
else
  xslt src/main/xslt/modules/xform-locale.xsl src/main/locale/en.xml -o build/xslt/locale/en.xml
  echo "    en only (pass --all-locales for all 75)"
fi

echo "==> ready: $TNG/build/xslt/docbook.xsl  (xslTNG $VER / $REF)"
