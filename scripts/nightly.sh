#!/usr/bin/env bash
# Nightly regression run: measures main of Core, XQuery and Xslt, together, every night, so a
# regression shows up the day after it merges instead of at release time.
#
# Why this exists. Every release from 2.5 to 2.8 found at least one regression or harness
# mistake late: a Windows stack overflow after a security merge, a fn:transform defect that
# sat five weeks until an XSpec census ran, a 5.4% slowdown measured by a customer, an
# xsl:import change that broke the database. Each was visible to a suite we already had; none
# of those suites ran until someone prepared a release.
#
# What it runs, in its own workspace (never the developer checkouts):
#   1. sync      fresh clones of core, xquery, xslt (main) and the xspec fork (phxspec)
#   2. qt3       xquery's scripts/conformance.sh  (QT3, per-set baseline gate)
#   3. xslt      xslt's scripts/conformance.sh     (W3C XSLT chunks, per-set baseline gate)
#   4. gate      xslt's scripts/release-gate.sh --cand-dev against the latest published Xslt
#                (Martin's repros, XSpec tutorial, DocBook sample, wasm, workbench)
#   5. census    the XSpec census, compared suite by suite with the previous night
# Every step runs against the main source of all three engines (PHOENIXML_DEV=1).
#
# Output: $NIGHTLY_HOME/reports/<date>/ (logs + summary.md). On any regression it opens (or
# comments on) a GitHub issue labelled "nightly" in phoenixmldb/phoenixmldb-xslt.
#
#   scripts/nightly.sh            # full run
#   scripts/nightly.sh --setup    # create the workspace and fetch the pinned corpora, then exit
#   NIGHTLY_STEPS="qt3 gate" scripts/nightly.sh   # a subset
#   NIGHTLY_NO_ISSUE=1 ...        # report only, never touch GitHub
#
# Env: NIGHTLY_HOME (default ~/.local/share/phoenixml-nightly), CORPORA (default /repos/phoenixml:
# where martin/ and docbook/ live; they are linked, not copied).
set -uo pipefail

NIGHTLY_HOME="${NIGHTLY_HOME:-$HOME/.local/share/phoenixml-nightly}"
WS="$NIGHTLY_HOME/ws"
CORPORA="${CORPORA:-/repos/phoenixml}"
DATE="$(date +%F)"
REP="$NIGHTLY_HOME/reports/$DATE"
STEPS="${NIGHTLY_STEPS:-qt3 xslt gate census}"
export PHOENIXML_DEV=1
unset DOTNET_SYSTEM_GLOBALIZATION_INVARIANT   # the engines need ICU; never run invariant

mkdir -p "$WS" "$REP"
SUMMARY="$REP/summary.md"
: > "$SUMMARY"
REGRESSED=0
note() { echo "$*" | tee -a "$SUMMARY"; }

clone_or_reset() {   # repo branch
  local dir="$WS/$1"
  if [ ! -d "$dir/.git" ]; then
    git clone -q --branch "$2" "https://github.com/phoenixmldb/$1.git" "$dir" || return 1
  fi
  git -C "$dir" fetch -q origin "$2" && git -C "$dir" checkout -q -B "$2" "origin/$2" \
    && git -C "$dir" reset -q --hard "origin/$2" && git -C "$dir" clean -qfdx -e TestData -e '*/TestData'
}

# ── 1. sync ─────────────────────────────────────────────────────────────────────────────
for r in phoenixmldb-core:main phoenixmldb-xquery:main phoenixmldb-xslt:main xspec:phxspec; do
  clone_or_reset "${r%%:*}" "${r##*:}" || { note "- **sync FAILED** for ${r%%:*}"; exit 1; }
done
for c in martin docbook; do [ -e "$WS/$c" ] || ln -s "$CORPORA/$c" "$WS/$c"; done
touch "$WS/.phoenixml-dev"
for r in core xquery xslt; do
  printf -v "SHA_$r" '%s' "$(git -C "$WS/phoenixmldb-$r" rev-parse --short HEAD)"
done
note "# Nightly $DATE"
note ""
note "core \`$SHA_core\` · xquery \`$SHA_xquery\` · xslt \`$SHA_xslt\` (main, built from source)"
note ""

if [ "${1:-}" = "--setup" ]; then
  (cd "$WS/phoenixmldb-xquery" && ./scripts/fetch-conformance-suites.sh)
  (cd "$WS/phoenixmldb-xslt" && ./scripts/fetch-conformance-suites.sh)
  echo "workspace ready at $WS"; exit 0
fi

# Machine deficits. The committed baselines are recorded on the developer's machine; a slower or
# differently configured host fails some cases there for reasons that are not code (per-test
# timeouts, corpus revision). $NIGHTLY_HOME/deficits/<suite>.tsv lists "set<TAB>n" with a reason
# on '#' lines; the committed baseline for that set is lowered by n before the run. The report
# prints every adjustment, so a deficit is never silent, and the committed baseline is still
# followed when the developers raise it.
apply_deficits() {   # suite-name repo-dir
  local f="$NIGHTLY_HOME/deficits/$1.tsv" b="$2/scripts/conformance-baseline.tsv"
  [ -f "$f" ] && [ -f "$b" ] || return 0
  python3 - "$f" "$b" <<'PY' | tee -a "$SUMMARY"
import sys
deficit = {}
for line in open(sys.argv[1]):
    line = line.strip()
    if line and not line.startswith('#'):
        k, n = line.split('\t'); deficit[k] = int(n)
rows, out = open(sys.argv[2]).read().splitlines(), []
for r in rows:
    parts = r.split('\t')
    if parts and parts[0] in deficit and len(parts) >= 2 and parts[1].isdigit():
        new = max(0, int(parts[1]) - deficit[parts[0]])
        print(f"    machine deficit: {parts[0]} {parts[1]} -> {new}")
        parts[1] = str(new)
    out.append('\t'.join(parts))
open(sys.argv[2], 'w').write('\n'.join(out) + '\n')
PY
}

step() {   # name, command...
  local name="$1"; shift
  local t0=$SECONDS
  "$@" > "$REP/$name.log" 2>&1
  local rc=$?
  local mins=$(( (SECONDS - t0) / 60 ))
  if [ $rc -eq 0 ]; then note "- **$name**: ok (${mins} min)"; else note "- **$name**: **REGRESSED** (exit $rc, ${mins} min) — \`$REP/$name.log\`"; REGRESSED=1; fi
  return $rc
}
want() { case " $STEPS " in *" $1 "*) return 0 ;; esac; return 1; }

# ── 2. QT3 ──────────────────────────────────────────────────────────────────────────────
if want qt3; then
  apply_deficits qt3 "$WS/phoenixmldb-xquery"
  step qt3 bash -c "cd '$WS/phoenixmldb-xquery' && ./scripts/conformance.sh"
  grep -E "failed|FAILED set|below baseline|TOTAL" "$REP/qt3.log" | tail -5 | sed 's/^/    /' >> "$SUMMARY"
fi

# ── 3. W3C XSLT ─────────────────────────────────────────────────────────────────────────
if want xslt; then
  apply_deficits xslt "$WS/phoenixmldb-xslt"
  step xslt bash -c "cd '$WS/phoenixmldb-xslt' && ./scripts/conformance.sh"
  grep -E "failed|below baseline|TOTAL" "$REP/xslt.log" | tail -5 | sed 's/^/    /' >> "$SUMMARY"
fi

# ── 4. release gate against the latest published Xslt ──────────────────────────────────
if want gate; then
  BASE="$(curl -s https://api.nuget.org/v3-flatcontainer/phoenixmldb.xslt/index.json \
    | python3 -c 'import sys,json;print(json.load(sys.stdin)["versions"][-1])')"
  step gate bash -c "cd '$WS/phoenixmldb-xslt' && ./scripts/release-gate.sh --base '$BASE' --cand-dev --out '$REP/gate'"
  grep -E "=== release gate|DIFFERS=|SAME=|GATE|BLOCKED|^\s+(DIFFERS|REGRESSED|IMPROVED)" "$REP/gate.log" \
    | head -25 | sed 's/^/    /' >> "$SUMMARY"
fi

# ── 5. XSpec census, suite by suite against the previous night ──────────────────────────
if want census; then
  census() {
    cd "$WS/xspec" || return 1
    dotnet build dotnet/PhoenixmlDb.XSpec.Cli -v q || return 1
    PHXSPEC_SUITE_TIMEOUT_SECONDS=120 dotnet run --project dotnet/PhoenixmlDb.XSpec.Cli --no-build \
      -- --census test/*.xspec > "$REP/census.md" </dev/null
    local prev; prev="$(ls -1d "$NIGHTLY_HOME"/reports/*/census.md 2>/dev/null | grep -v "/$DATE/" | tail -1)"
    [ -n "$prev" ] || { echo "no previous census; this run is the baseline"; return 0; }
    python3 - "$prev" "$REP/census.md" <<'PY'
import re, sys
def parse(p):
    t = open(p).read().split("## Per-suite detail", 1)[1]
    d = {}
    for blk in re.split(r"\n### ", t)[1:]:
        name = blk.split("\n", 1)[0].strip().strip('`').split('/')[-1]
        st = re.search(r"- Stage: (\w+)", blk); ps = re.search(r"Passed: (\d+), Failed: (\d+)", blk)
        d[name] = (st.group(1) if st else "?", int(ps.group(1)) if ps else None)
    return d
order = {"Compile": 0, "Run": 1, "Assess": 2, "Complete": 3}
a, b = parse(sys.argv[1]), parse(sys.argv[2])
down = [f"{k}: {a[k][0]} -> {b.get(k, ('-',))[0]}" for k in a
        if a[k][0] in order and b.get(k, ("-",))[0] in order and order[b[k][0]] < order[a[k][0]]]
fewer = [f"{k}: passed {a[k][1]} -> {b[k][1]}" for k in a
         if k in b and a[k][0] == b[k][0] == "Complete" and a[k][1] is not None and b[k][1] is not None and b[k][1] < a[k][1]]
for line in down + fewer: print("REGRESSION", line)
print(f"census: {sum(1 for v in b.values() if v[0]=='Complete')} complete; {len(down)} stage down; {len(fewer)} with fewer passes")
sys.exit(1 if down or fewer else 0)
PY
  }
  step census census
  grep -E "^census:|^REGRESSION" "$REP/census.log" | head -15 | sed 's/^/    /' >> "$SUMMARY"
fi

# ── report ──────────────────────────────────────────────────────────────────────────────
note ""
if [ $REGRESSED -eq 0 ]; then note "**Result: clean.**"; else note "**Result: REGRESSION — see the steps marked above.**"; fi
find "$NIGHTLY_HOME/reports" -mindepth 1 -maxdepth 1 -type d -mtime +30 -exec rm -rf {} + 2>/dev/null

if [ $REGRESSED -ne 0 ] && [ "${NIGHTLY_NO_ISSUE:-0}" != "1" ]; then
  REPO=phoenixmldb/phoenixmldb-xslt
  gh label create nightly -R "$REPO" --color B60205 --description "Nightly regression run" >/dev/null 2>&1
  OPEN="$(gh issue list -R "$REPO" --label nightly --state open --json number -q '.[0].number')"
  if [ -n "$OPEN" ]; then gh issue comment "$OPEN" -R "$REPO" --body-file "$SUMMARY" >/dev/null
  else gh issue create -R "$REPO" --label nightly --title "Nightly regression report" --body-file "$SUMMARY" >/dev/null; fi
fi
exit $REGRESSED
