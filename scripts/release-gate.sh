#!/usr/bin/env bash
# Release gate: run real-world corpora through the PREVIOUS release and the CANDIDATE, and block
# the tag on any case that got worse.
#
# Why this exists (xslt#191, 2026-09-28). 2.4.0 passed QT3 and XSLT conformance with zero per-set
# losses, then broke XSpec compilation within two hours of publishing: Martin Honnen found it on
# his first run. The W3C suites test features in isolation; the break needed a spec-correct type
# change (prefix-from-QName -> xs:NCName) meeting a pre-existing hole (EBV of a string subtype),
# and only real stylesheets combine things that way. Every field regression reported so far has
# had that shape. This gate is the corpus that finds them before a user does.
#
# It is A/B, not golden-file: most cases have no recorded "right answer" (Martin's folder holds
# repros and bisection probes), so the question asked is only "did the candidate do anything
# different from the release users already have?" Every difference is classified:
#
#   REGRESSED  baseline succeeded, candidate errors             -> blocks the tag
#   DIFFERS    both succeed, output differs                      -> blocks until reviewed
#   ERRCHANGE  both error, but with a different error code       -> blocks until reviewed
#   IMPROVED   baseline errored, candidate succeeds              -> reported, never blocks
#   SAME / BOTH-FAIL-SAME                                        -> quiet
#   NONDET     baseline disagrees with itself on a second run    -> excluded, listed
#
# A fix shows up as IMPROVED or DIFFERS. DIFFERS needs a human to say "yes, that is the fix";
# pass --accept <case-id> for each one so the approval is on the record.
#
# Usage:
#   scripts/release-gate.sh --base 2.4.0 --cand-local            # candidate = this tree's CLI
#   scripts/release-gate.sh --base 2.4.0 --cand 2.4.1            # candidate = a published tool
#   scripts/release-gate.sh --base 2.5.1 --cand-dev              # candidate = this tree built against
#        the SIBLING checkouts (../phoenixmldb-xquery, ../phoenixmldb-core) in dev mode. Use it before
#        a release: main depends on unreleased XQuery source, so --cand-local (which builds against the
#        pinned packages, as a release tag does) cannot compile it. The label records the sibling SHAs.
#   options: --docbook N|all (every-Nth sample of xslTNG's 661 test docs; default 12)
#            --jobs N (default nproc)   --out DIR   --accept ID (repeatable)
#
# Corpora (override with env): MARTIN_DIR, XSPEC_DIR, TNG_DIR. Plus one runtime case,
# wasm/transform: a transform on the browser-wasm runtime under Node (scripts/wasm-probe; needs
# node, not the wasm workload).
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WS="$(cd "$ROOT/.." && pwd)"
MARTIN_DIR="${MARTIN_DIR:-$WS/martin}"
XSPEC_DIR="${XSPEC_DIR:-$WS/xspec}"
TNG_DIR="${TNG_DIR:-$WS/docbook/xslTNG}"

BASE="" CAND="" CAND_LOCAL=0 CAND_DEV=0 DOCBOOK=12 JOBS="$(nproc)" OUT="" ACCEPT=()
while [ $# -gt 0 ]; do
  case "$1" in
    --base) BASE="$2"; shift 2 ;;
    --cand) CAND="$2"; shift 2 ;;
    --cand-local) CAND_LOCAL=1; shift ;;
    --cand-dev) CAND_LOCAL=1; CAND_DEV=1; shift ;;
    --docbook) DOCBOOK="$2"; shift 2 ;;
    --jobs) JOBS="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --accept) ACCEPT+=("$2"); shift 2 ;;
    -h|--help) sed -n '2,40p' "$0"; exit 0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[ -n "$BASE" ] || { echo "--base <published version> is required" >&2; exit 2; }
[ -n "$CAND" ] || [ "$CAND_LOCAL" = 1 ] || { echo "need --cand <version> or --cand-local" >&2; exit 2; }
OUT="${OUT:-$ROOT/release-gate-results}"
rm -rf "$OUT"; mkdir -p "$OUT/tools" "$OUT/runs"

# ── Tools ─────────────────────────────────────────────────────────────────────────────────
# Published tools install into a private tool-path, so the gate never touches the user's global
# tools and always tests exactly what nuget.org serves.
install_tool() { # id version dir
  dotnet tool install --tool-path "$3" "$1" --version "$2" >/dev/null 2>&1 \
    || { echo "cannot install $1 $2 from nuget.org" >&2; exit 2; }
}
install_tool xslt "$BASE" "$OUT/tools/base"
install_tool xquery4 "$BASE" "$OUT/tools/base" 2>/dev/null || true
BX="$OUT/tools/base/xslt"; BQ="$OUT/tools/base/xquery"
if [ "$CAND_LOCAL" = 1 ]; then
  echo "building candidate CLI from $ROOT ..."
  if [ "$CAND_DEV" = 1 ]; then
    for sib in phoenixmldb-xquery phoenixmldb-core; do
      [ -e "$WS/$sib/.git" ] || { echo "--cand-dev needs $WS/$sib checked out" >&2; exit 2; }
      git -C "$WS/$sib" diff --quiet HEAD -- || echo "WARNING: $sib has uncommitted changes; this reading is not reproducible from SHAs" >&2
    done
    export PHOENIXML_DEV=1   # inherited by every candidate build below; the base arms use packages
  fi
  dotnet build "$ROOT/src/PhoenixmlDb.Xslt.Cli/PhoenixmlDb.Xslt.Cli.csproj" -c Release -f net10.0 \
    --nologo -v q >"$OUT/cand-build.log" 2>&1 || { echo "candidate build failed, see $OUT/cand-build.log" >&2; exit 2; }
  CX="$ROOT/src/PhoenixmlDb.Xslt.Cli/bin/Release/net10.0/xslt"
  CQ=""   # the xquery CLI lives in phoenixmldb-xquery; .xq cases run only with --cand <version>
  CAND_LABEL="local $(git -C "$ROOT" rev-parse --short HEAD)"
  [ "$CAND_DEV" = 1 ] && CAND_LABEL="$CAND_LABEL (dev: xquery $(git -C "$WS/phoenixmldb-xquery" rev-parse --short HEAD), core $(git -C "$WS/phoenixmldb-core" rev-parse --short HEAD))"
else
  install_tool xslt "$CAND" "$OUT/tools/cand"
  install_tool xquery4 "$CAND" "$OUT/tools/cand" 2>/dev/null || true
  CX="$OUT/tools/cand/xslt"; CQ="$OUT/tools/cand/xquery"
  CAND_LABEL="$CAND"
fi
[ -x "$CQ" ] || CQ=""; [ -x "$BQ" ] || BQ=""

# ── Cases ─────────────────────────────────────────────────────────────────────────────────
# One line per case: id <TAB> kind(xslt|xquery) <TAB> workdir <TAB> args (tab-separated).
CASES="$OUT/cases.tsv"; : > "$CASES"
add() { local IFS=$'\t'; echo "$*" >> "$CASES"; }

# Martin's repros. No manifest pairs stylesheets with inputs, so: in.xml if present, else the
# first .xml in the folder, else run the named initial template. Bisection probes are included
# on purpose; an A/B only asks whether behaviour moved.
if [ -d "$MARTIN_DIR" ]; then
  for d in "$MARTIN_DIR"/*/; do
    in=""; [ -f "$d/in.xml" ] && in="in.xml" || in="$(cd "$d" && ls *.xml 2>/dev/null | head -1)"
    for x in "$d"*.xsl; do
      [ -f "$x" ] || continue
      n="martin/$(basename "$d")/$(basename "$x")"
      if [ -n "$in" ]; then add "$n" xslt "$d" "$(basename "$x")" "$in"
      else add "$n" xslt "$d" "$(basename "$x")" -it xsl:initial-template; fi
    done
    for q in "$d"*.xq; do
      [ -f "$q" ] || continue
      add "martin/$(basename "$d")/$(basename "$q")" xquery "$d" -f "$(basename "$q")"
    done
  done
fi

# XSpec: compile every tutorial suite. The compiler is a large, idiomatic XSLT 3 program that
# exercises the engine the way users' stylesheets do — this is the case that caught #191.
if [ -d "$XSPEC_DIR/tutorial" ]; then
  for s in "$XSPEC_DIR"/tutorial/*.xspec "$XSPEC_DIR"/tutorial/*/*.xspec; do
    [ -f "$s" ] || continue
    grep -q 'stylesheet=' "$s" || continue          # XSLT suites only; query suites need the xquery compiler
    add "xspec/${s#$XSPEC_DIR/tutorial/}" xslt "$XSPEC_DIR" src/compiler/compile-xslt-tests.xsl "$s"
  done
fi

# DocBook xslTNG against its own authors' test documents. Each transform recompiles docbook.xsl
# (~19 s), so the default is a deterministic every-Nth sample; --docbook all runs all 661.
if [ -f "$TNG_DIR/build/xslt/docbook.xsl" ]; then
  i=0
  while IFS= read -r f; do
    i=$((i+1))
    if [ "$DOCBOOK" = all ] || [ $(( (i-1) % DOCBOOK )) -eq 0 ]; then
      add "docbook/$(basename "$f")" xslt "$TNG_DIR" build/xslt/docbook.xsl "$f"
    fi
  done < <(find "$TNG_DIR/src/test/resources" -name '*.xml' | sort)   # 661, in subdirectories
fi

echo "cases: $(wc -l < "$CASES")   baseline: $BASE   candidate: $CAND_LABEL   jobs: $JOBS"

# ── Run ───────────────────────────────────────────────────────────────────────────────────
# Each case runs on the candidate once and the baseline twice (the second baseline run exposes
# nondeterminism that masking did not catch). stdin is /dev/null: the xslt CLI otherwise waits
# on stdin and an isolation "hangs" in a way that looks like an engine bug.
run_one() {
  local line="$1" arm="$2" tool="$3" id kind wd
  IFS=$'\t' read -r -a f <<< "$line"
  id="${f[0]}"; kind="${f[1]}"; wd="${f[2]}"; local args=("${f[@]:3}")
  local dir="$OUT/runs/$arm/${id//\//__}"; mkdir -p "$dir"
  [ -n "$tool" ] || { echo "SKIP" > "$dir/rc"; return; }
  # A repro that writes files (xsl:result-document) must not write into the corpus: the three
  # arms run at once in the same folder, and leftovers change later runs. xslt#213's repro left
  # chapter-*.xml behind, and the next run picked chapter-1.xml as the input. Each martin case
  # runs in its own copy of its folder; the folders are small.
  if [[ "$id" == martin/* ]]; then cp -a "$wd" "$dir/wd" && wd="$dir/wd"; fi
  ( cd "$wd" &&timeout 300 "$tool" "${args[@]}" </dev/null >"$dir/out" 2>"$dir/err"; echo $? > "$dir/rc" )
}
export -f run_one; export OUT
for arm in cand base base2; do
  case "$arm" in cand) T="$CX"; Q="$CQ" ;; *) T="$BX"; Q="$BQ" ;; esac
  while IFS= read -r line; do
    kind=$(printf '%s' "$line" | cut -f2)
    [ "$kind" = xquery ] && printf '%s\0%s\0%s\0' "$line" "$arm" "$Q" || printf '%s\0%s\0%s\0' "$line" "$arm" "$T"
  done < "$CASES" | xargs -0 -n3 -P "$JOBS" bash -c 'run_one "$0" "$1" "$2"'
  echo "  $arm done"
done

# ── WebAssembly ───────────────────────────────────────────────────────────────────────────
# 2.5.1 started every transform on a dedicated Thread (#197's large stack), and browser-wasm
# cannot start threads: every XSLT call in a Blazor WebAssembly app failed (xslt#237), and
# nothing here ran on that runtime. scripts/wasm-probe builds a browser-wasm app around one
# transform and runs it under Node, so no browser and no wasm workload are needed. It lands in
# runs/<arm>/wasm__transform like any other case: a candidate that fails where the baseline
# ran is REGRESSED.
# Probe builds compile an engine into a scratch app under $OUT, which sits inside this repo, so
# its Directory.Build.targets applies. With dev mode on (--cand-dev exports PHOENIXML_DEV=1, or a
# workspace .phoenixml-dev marker) it swapped a BASELINE probe's PhoenixmlDb.Xslt package for this
# tree's source: the baseline then WAS the candidate, and every probe case compared the candidate
# with itself and classified SAME (found when 2.4.1 "passed" a deep recursion it cannot pass).
# probe_engine_prop builds a package arm with dev mode forced off; assert_probe_engine then reads
# what restore actually resolved and stops the gate unless it matches the arm.
probe_engine_prop() { # msbuild property selecting the engine -> the full property list for it
  case "$1" in -p:XsltVersion=*) printf '%s\n' "$1" "-p:PhoenixmlDev=false" ;; *) printf '%s\n' "$1" ;; esac
}
assert_probe_engine() { # project dir, msbuild property selecting the engine, label
  python3 - "$1/obj/project.assets.json" "$2" "$3" <<'PY'
import json, sys
assets, prop, label = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    libs = json.load(open(assets, encoding='utf-8'))['libraries']
except Exception as e:
    sys.exit(f"release gate: {label}: cannot read {assets}: {e}")
hits = {k: v.get('type') for k, v in libs.items() if k.split('/')[0] == 'PhoenixmlDb.Xslt'}
if prop.startswith('-p:XsltVersion='):
    want = f"PhoenixmlDb.Xslt/{prop.split('=', 1)[1]}"
    ok = hits.get(want) == 'package'
else:
    ok = len(hits) == 1 and next(iter(hits.values())) == 'project'
print(f"  {label}: PhoenixmlDb.Xslt resolved as {hits}")
if not ok:
    sys.exit(f"release gate: {label} built the wrong engine for {prop}: {hits}")
PY
}
WASM_ID="wasm/transform"
# wasm/deep-recursion: the same probe with argument "deep-recursion" runs Martin Honnen's memoised
# Fibonacci at n = 1000. Inline on wasm's small stack that failed (XTDE0000) on every build before
# xslt#266. His own input, n = 200, sits at the stack's edge and flips run to run, so it can't
# serve as a case. A baseline that fails this is expected (an old release), not a broken gate.
WASM_DEEP_ID="wasm/deep-recursion"
wasm_deep_run() { # arm whose probe is built, run dir name
  local src="$OUT/wasm/$1" dir="$OUT/runs/$2/wasm__deep-recursion"
  mkdir -p "$dir"
  if [ ! -f "$src/pub/wwwroot/main.mjs" ]; then
    echo "probe for $1 was not built" > "$dir/err"; echo 3 > "$dir/rc"; return
  fi
  ( cd "$src/pub/wwwroot" && timeout 300 node main.mjs deep-recursion >"$dir/out" 2>"$dir/err"; echo $? > "$dir/rc" )
}
wasm_arm() { # arm, then msbuild property selecting the engine
  local arm="$1" prop="$2" dir="$OUT/runs/$1/wasm__transform" src="$OUT/wasm/$1"
  mkdir -p "$dir" "$OUT/wasm"; rm -rf "$src"; cp -a "$ROOT/scripts/wasm-probe" "$src"
  local props; mapfile -t props < <(probe_engine_prop "$prop")
  if ! dotnet publish "$src/WasmProbe.csproj" -c Release "${props[@]}" --nologo -v q -o "$src/pub" \
       >"$dir/build.log" 2>&1; then
    { echo "probe build failed:"; tail -20 "$dir/build.log"; } > "$dir/err"; echo 3 > "$dir/rc"; return
  fi
  assert_probe_engine "$src" "$prop" "wasm probe ($arm)" || exit 2
  cp "$src/main.mjs" "$src/pub/wwwroot/"
  ( cd "$src/pub/wwwroot" && timeout 300 node main.mjs >"$dir/out" 2>"$dir/err"; echo $? > "$dir/rc" )
}
if command -v node >/dev/null 2>&1; then
  printf '%s\twasm\t-\n' "$WASM_ID" >> "$CASES"
  printf '%s\twasm\t-\n' "$WASM_DEEP_ID" >> "$CASES"
  wasm_arm base "-p:XsltVersion=$BASE"
  # A probe that did not BUILD or did not RUN means the gate itself is broken, and continuing would
  # file the case under NONDET, which does not block: the first version of this step reported GATE
  # PASSED with neither probe built. But a baseline that runs and raises an ENGINE error is a valid
  # reading, not a broken gate: 2.5.1 itself fails this probe (#237), and that baseline must yield
  # IMPROVED for a fixed candidate, not stop the gate. The probe exits 2 only from its own catch,
  # with a "WASM <exception>" line; anything else (3 = build failed, a Node crash or timeout) is the
  # gate's fault.
  wasm_rc=$(cat "$OUT/runs/base/wasm__transform/rc")
  if ! { [ "$wasm_rc" = 0 ] || { [ "$wasm_rc" = 2 ] && grep -q '^WASM ' "$OUT/runs/base/wasm__transform/err"; }; }; then
    echo "release gate: the wasm probe did not run on the BASELINE $BASE (rc=$wasm_rc); fix the gate before trusting it:" >&2
    cat "$OUT/runs/base/wasm__transform/err" >&2; exit 2
  fi
  mkdir -p "$OUT/runs/base2/wasm__transform"   # rerun the base build for the NONDET check
  ( cd "$OUT/wasm/base/pub/wwwroot" 2>/dev/null && timeout 300 node main.mjs \
      >"$OUT/runs/base2/wasm__transform/out" 2>"$OUT/runs/base2/wasm__transform/err"
    echo $? > "$OUT/runs/base2/wasm__transform/rc" ) || echo 3 > "$OUT/runs/base2/wasm__transform/rc"
  wasm_deep_run base base
  wasm_deep_run base base2
  if [ "$CAND_LOCAL" = 1 ]; then
    wasm_arm cand "-p:XsltProject=$ROOT/src/PhoenixmlDb.Xslt/PhoenixmlDb.Xslt.csproj"
  else
    wasm_arm cand "-p:XsltVersion=$CAND"
  fi
  wasm_deep_run cand cand
  echo "  wasm done"
else
  echo "  wasm SKIPPED: node not found (the browser-wasm case needs Node to run)" >&2
fi

# ── Martin's workbench ────────────────────────────────────────────────────────────────────
# Martin Honnen's PhoenixmlWorkbench (github.com/martin-honnen/PhoenixmlWorkbench, cloned into
# MARTIN_DIR/PhoenixmlWorkbench) runs XSLT, SchXslt2 Schematron and DocBook xslTNG in Blazor
# WebAssembly. It is where #237 surfaced. Two kinds of case:
#   wb/build      his app, built against the engine (an API break shows here before he finds it)
#   wb/<example>  each of his examples run on the browser-wasm runtime under Node, the way his
#                 worker runs it (scripts/workbench-probe), A/B like every other case.
# The example list is read from his app each run (workbench-probe/cases.py), so new examples are
# covered without editing this script.
WB="$MARTIN_DIR/PhoenixmlWorkbench/PhoenixmlWorkbench"
XQ_PIN=$(grep -oP '(?<=Include="PhoenixmlDb.XQuery" Version=")[^"]+' "$ROOT/Directory.Packages.props" | head -1)
wb_build_arm() { # arm, then "pkg <version>" or "src"
  local arm="$1" mode="$2" ver="${3:-}" dir="$OUT/runs/$1/wb__build" src="$OUT/wbbuild/$1"
  mkdir -p "$dir"; rm -rf "$src"; mkdir -p "$src"; cp -a "$WB" "$src/"
  echo '<Project />' > "$src/Directory.Build.props"; echo '<Project />' > "$src/Directory.Packages.props"
  local proj="$src/PhoenixmlWorkbench/PhoenixmlWorkbench.csproj"
  if [ "$mode" = pkg ]; then
    sed -i -E "s#(Include=\"PhoenixmlDb\.(Xslt|XQuery)\" Version=\")[^\"]+#\1$ver#" "$proj"
  else
    if [ "$CAND_DEV" = 1 ]; then
      # Dev mode points the engine at the XQuery SOURCE; his own direct XQuery reference must follow,
      # or two PhoenixmlDb.XQuery assemblies meet in one compilation (CS1704).
      sed -i -E "s#<PackageReference Include=\"PhoenixmlDb\.XQuery\"[^/]*/>#<ProjectReference Include=\"$WS/phoenixmldb-xquery/src/PhoenixmlDb.XQuery/PhoenixmlDb.XQuery.csproj\" SetTargetFramework=\"TargetFramework=net10.0\" />#" "$proj"
    else
      sed -i -E "s#(Include=\"PhoenixmlDb\.XQuery\" Version=\")[^\"]+#\1$XQ_PIN#" "$proj"
    fi
    sed -i -E "s#<PackageReference Include=\"PhoenixmlDb\.Xslt\"[^/]*/>#<ProjectReference Include=\"$ROOT/src/PhoenixmlDb.Xslt/PhoenixmlDb.Xslt.csproj\" SetTargetFramework=\"TargetFramework=net10.0\" />#" "$proj"
  fi
  local eprop; [ "$mode" = pkg ] && eprop="-p:XsltVersion=$ver" || eprop="-p:XsltProject=$ROOT"
  local props; mapfile -t props < <(probe_engine_prop "$eprop")
  # Only PhoenixmlDev=false reaches the build: XsltVersion is a stand-in for "package arm" here,
  # since the versions are already written into his project file above.
  dotnet build "$proj" -c Release "${props[@]:1}" --nologo -v q >"$dir/build.log" 2>&1; echo $? > "$dir/rc"
  : > "$dir/out"; grep -E ' error ' "$dir/build.log" | sort -u | head -20 > "$dir/err"
  if [ "$(cat "$dir/rc")" = 0 ]; then
    assert_probe_engine "$(dirname "$proj")" "$eprop" "workbench app build ($arm)" || exit 2
  fi
}
wb_probe_arm() { # arm, then msbuild property selecting the engine; writes runs/<arm>/wb__<id>
  local arm="$1" prop="$2" src="$OUT/wbprobe/$1"
  rm -rf "$src"; mkdir -p "$OUT/wbprobe"; cp -a "$ROOT/scripts/workbench-probe" "$src"
  local props; mapfile -t props < <(probe_engine_prop "$prop")
  if ! dotnet publish "$src/WorkbenchProbe.csproj" -c Release "${props[@]}" -p:ExamplesDir="$WB/wwwroot/examples" \
       -p:CasesFile="$OUT/wb-cases.tsv" --nologo -v q -o "$src/pub" >"$src/build.log" 2>&1; then
    echo "probe build failed"; return 1
  fi
  assert_probe_engine "$src" "$prop" "workbench probe ($arm)" || exit 2
  cp "$src/main.mjs" "$src/pub/wwwroot/"
  wb_probe_run "$arm" "$src"
}
wb_probe_run() { # arm, built probe dir: runs it and splits its JSON lines into case dirs
  ( cd "$2/pub/wwwroot" && timeout 1800 node main.mjs >"$2/results.jsonl" 2>"$2/node.err" )
  python3 - "$2/results.jsonl" "$OUT/runs/$1" "$OUT/wb-cases.tsv" <<'PY'
import json, os, sys
res, runs, cases = sys.argv[1], sys.argv[2], sys.argv[3]
got = {}
for line in open(res, encoding='utf-8', errors='replace'):
    try: d = json.loads(line); got[d['id']] = d
    except Exception: pass
for line in open(cases, encoding='utf-8'):
    cid = line.rstrip('\n').split('\t')[1]
    d = os.path.join(runs, 'wb__' + cid.replace('/', '__')); os.makedirs(d, exist_ok=True)
    r = got.get(cid, {'rc': 3, 'out': '', 'err': 'no result from the probe (it crashed or timed out)'})
    open(os.path.join(d, 'out'), 'w', encoding='utf-8').write(r['out'])
    open(os.path.join(d, 'err'), 'w', encoding='utf-8').write(r['err'])
    open(os.path.join(d, 'rc'), 'w').write(str(r['rc']))
PY
}
if [ -d "$WB" ] && command -v node >/dev/null 2>&1; then
  python3 "$ROOT/scripts/workbench-probe/cases.py" "$WB" > "$OUT/wb-cases.tsv"
  printf '%s\twb\t-\n' "wb/build" >> "$CASES"
  cut -f2 "$OUT/wb-cases.tsv" | while read -r id; do printf '%s\twb\t-\n' "wb/$id" >> "$CASES"; done
  # Note the case ids carry the wb/ prefix in cases.tsv; run dirs are wb__<id>.
  wb_build_arm base pkg "$BASE"; cp -a "$OUT/runs/base/wb__build" "$OUT/runs/base2/wb__build"
  # As with the wasm probe: the baseline is published and known to run, so a probe that will not
  # build or run there means the gate is broken, and that must not pass as NONDET.
  wb_probe_arm base "-p:XsltVersion=$BASE" || { echo "release gate: the workbench probe failed to build on the BASELINE $BASE" >&2; tail -20 "$OUT/wbprobe/base/build.log" >&2; exit 2; }
  # Same distinction as the wasm probe: the probe writes one JSON line per example it ran, whatever
  # the example's outcome. No lines at all means the probe itself crashed or never started; every
  # example failing with an engine error (2.5.1 on browser-wasm, #237) is a valid baseline.
  if ! grep -q '"id":' "$OUT/wbprobe/base/results.jsonl" 2>/dev/null; then
    echo "release gate: the workbench probe produced no results on the BASELINE $BASE; fix the gate before trusting it" >&2
    tail -5 "$OUT/wbprobe/base/node.err" >&2; exit 2
  fi
  wb_probe_run base2 "$OUT/wbprobe/base"
  if [ "$CAND_LOCAL" = 1 ]; then
    wb_build_arm cand src
    wb_probe_arm cand "-p:XsltProject=$ROOT/src/PhoenixmlDb.Xslt/PhoenixmlDb.Xslt.csproj" || true
  else
    wb_build_arm cand pkg "$CAND"
    wb_probe_arm cand "-p:XsltVersion=$CAND" || true
  fi
  echo "  workbench done ($(wc -l < "$OUT/wb-cases.tsv") examples)"
else
  echo "  workbench SKIPPED: $WB not cloned, or node not found" >&2
fi

# ── Compare ───────────────────────────────────────────────────────────────────────────────
python3 - "$OUT" "$CASES" "$BASE" "$CAND_LABEL" "${ACCEPT[@]+"${ACCEPT[@]}"}" <<'PY'
import os, re, sys
out, cases, base, cand, accept = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], set(sys.argv[5:])
MASKS = [
    (re.compile(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)?'), 'DATETIME'),
    # generate-id(): Saxon-style d12e3, and this engine's hash form (d22be8d8), which differs on
    # every compile and so between two runs of the SAME version. Hash ids drop leading zeros,
    # so they can be as short as d + 3 hex (d0c711 came out as dc711c); require a digit so
    # English words spelt from a-f (deface, decade) are not masked.
    (re.compile(r'\b(d\d+e\d+|d(?=[0-9a-f]*[0-9])[0-9a-f]{3,}|R_[A-Za-z0-9]+|id[A-Za-z0-9_-]{6,}|N[0-9A-F]{6,})\b'), 'GENID'),
    (re.compile(r'PhoenixmlDb (XSLT|XQuery)[^<\n"]*?\d+\.\d+\.\d+[^<\n"]*'), 'ENGINEVERSION'),
    (re.compile(r'\b' + re.escape(base) + r'\b'), 'VER'),
]
if re.match(r'^\d+\.\d+\.\d+$', cand): MASKS.append((re.compile(r'\b' + re.escape(cand) + r'\b'), 'VER'))
def mask(s):
    for rx, rep in MASKS: s = rx.sub(rep, s)
    return s
def load(arm, cid):
    d = os.path.join(out, 'runs', arm, cid.replace('/', '__'))
    rd = lambda n: open(os.path.join(d, n), encoding='utf-8', errors='replace').read() if os.path.exists(os.path.join(d, n)) else ''
    rc = rd('rc').strip()
    err = rd('err')
    code = (re.search(r'\b((?:FO|XP|XT|XQ|SE|SX|XU|FT)[A-Z]{2}\d{4})\b', err) or [None, None])[1]
    return rc, mask(rd('out')), code, err
res = {}
for line in open(cases, encoding='utf-8'):
    cid = line.split('\t')[0]
    bc, bo, bcode, _ = load('base', cid); b2c, b2o, *_ = load('base2', cid)
    cc, co, ccode, cerr = load('cand', cid)
    # Exit status is decided before output. A candidate that errors where the baseline
    # succeeded both times is a regression no matter how noisy the output is. The first
    # version of this gate checked NONDET first and filed Martin's own #191 case under it.
    if 'SKIP' in (bc, cc): k = 'SKIPPED'
    elif bc == '0' and b2c == '0' and cc != '0': k = 'REGRESSED'
    elif bc != b2c or (bc == '0' and bo != b2o): k = 'NONDET'
    elif bc == '0' and cc == '0': k = 'SAME' if bo == co else 'DIFFERS'
    elif cc == '0': k = 'IMPROVED'
    else: k = 'BOTH-FAIL-SAME' if bcode == ccode else 'ERRCHANGE'
    res[cid] = (k, bcode, ccode, cerr.strip().splitlines()[-1:] if cerr.strip() else [])
order = ['REGRESSED', 'ERRCHANGE', 'DIFFERS', 'IMPROVED', 'NONDET', 'SKIPPED', 'BOTH-FAIL-SAME', 'SAME']
counts = {k: sum(1 for v in res.values() if v[0] == k) for k in order}
print(f"\n=== release gate: {base} -> {cand}, {len(res)} cases ===")
print('  '.join(f"{k}={counts[k]}" for k in order if counts[k]))
blocking = []
for k in order[:5]:
    for cid, (kk, bcode, ccode, tail) in sorted(res.items()):
        if kk != k: continue
        acc = cid in accept
        note = f"  base={bcode or 'ok'} cand={ccode or 'ok'}" if k in ('REGRESSED', 'ERRCHANGE', 'IMPROVED') else ''
        print(f"  {k:9} {cid}{note}{'  [accepted]' if acc else ''}")
        if k == 'REGRESSED' and tail: print(f"            {tail[0][:160]}")
        if k in ('REGRESSED', 'ERRCHANGE', 'DIFFERS') and not acc: blocking.append(cid)
with open(os.path.join(out, 'summary.txt'), 'w') as f:
    for cid, v in sorted(res.items()): f.write(f"{v[0]}\t{cid}\n")
if blocking:
    print(f"\nBLOCKED: {len(blocking)} case(s) need a fix or an explicit --accept <id>. Diff with:")
    print(f"  diff {out}/runs/base/<id> {out}/runs/cand/<id>   (id with / written as __)")
    sys.exit(1)
print("\nGATE PASSED")
PY
