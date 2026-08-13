#!/usr/bin/env bash
# Transform the xslTNG sample article with the locally built engine and report pass/fail.
#
# Usage: run-docbook.sh [source.xml] [-- <extra xslt args>]
#   PHXDIAG_XTDE0420=1  dump construction state at every XTDE0420/0440 raise site
set -euo pipefail

TNG="${TNG:-/repos/phoenixml/docbook/xslTNG}"
# Repo root, derived from this script's location — no hard-coded absolute path.
REPO="$(cd -P "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/../../../.." && pwd)"
XSLT_BIN="${XSLT_BIN:-$REPO/src/PhoenixmlDb.Xslt.Cli/bin/Debug/net10.0/xslt.dll}"
OUT="${OUT:-${TMPDIR:-/tmp}/xsltng-article.html}"
SRC="${1:-$TNG/src/main/samples/article.xml}"
[[ "${1:-}" == --* ]] && SRC="$TNG/src/main/samples/article.xml"
shift || true
[[ "${1:-}" == "--" ]] && shift

[[ -f "$TNG/build/xslt/docbook.xsl" ]] || { echo "ERROR: run bootstrap-xsltng.sh first" >&2; exit 1; }

cd "$TNG"
set +e
ERR=$(dotnet "$XSLT_BIN" build/xslt/docbook.xsl "$SRC" -o "$OUT" "$@" 2>&1 >/dev/null)
RC=$?
set -e

[[ -n "$ERR" ]] && echo "$ERR"

if [[ $RC -ne 0 ]]; then
  echo "FAIL: transform exited $RC"
  exit $RC
fi

# A silent exit 0 is not success: the transform can emit a truncated document. Assert the
# sample's known landmarks — html root, generated <title>, and the body prose.
python3 - "$OUT" <<'PY'
import sys
html = open(sys.argv[1], encoding='utf-8').read()
checks = [
    ('doctype',        html.lstrip().lower().startswith('<!doctype html>')),
    ('closing </html>','</html>' in html),
    ('<title>',        '<title>Sample Article</title>' in html),
    ('article header', '<h1>Sample Article</h1>' in html),
    ('body prose',     'smoke' in html),
    ('generator meta', 'PhoenixmlDb XSLT' in html),
]
bad = [n for n, ok in checks if not ok]
print(f"    {len(html)} bytes -> {sys.argv[1]}")
if bad:
    print("FAIL: output missing " + ", ".join(bad))
    sys.exit(1)
print("PASS: all landmarks present")
PY
