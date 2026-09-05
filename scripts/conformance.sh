#!/usr/bin/env bash
# Runs the W3C conformance suites one CHUNK at a time, serially.
#
# Why chunks. A single `dotnet test --filter "Suite=XSLT"` runs every group in one
# process and prints nothing until it finishes, so a run that is merely slow is
# indistinguishable from one that has hung — and when it does die you lose the whole
# sweep and learn nothing. This drives the Trait("Group") values that already exist,
# one `dotnet test` invocation each, printing a result line as each chunk lands.
#
# What that buys:
#   - progress you can watch, so slow != hung
#   - a crash or timeout costs ONE chunk; the rest still run and still report
#   - re-run just the chunk you broke:  ./scripts/conformance.sh expr
#   - a per-chunk log to read afterwards, not one 40-minute scrollback
#
# Execution is serial by construction: xunit.runner.json pins maxParallelThreads to 1
# and disables collection parallelism, and chunks run one after another here. Do not
# "speed this up" by running chunks concurrently — these suites are memory-hungry and
# concurrent runs are what caused the crashes this layout exists to avoid.
#
# Usage:
#   ./scripts/conformance.sh                # every XSLT group, in order
#   ./scripts/conformance.sh expr           # one group
#   ./scripts/conformance.sh expr fn insn   # several
#   ./scripts/conformance.sh xqts           # the XQuery (QT3) suite
#   ./scripts/conformance.sh --all          # XSLT chunks + xqts (what CI runs)
#   ./scripts/conformance.sh --list         # show the chunks
#
# Env:
#   CONFORMANCE_TIMEOUT   per-chunk seconds (default 900)
#   CONFORMANCE_OUT       results directory (default ./conformance-results)
#   CONFORMANCE_CONFIG    Debug|Release (default Debug)
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT/tests/PhoenixmlDb.Conformance.Tests"
SUITES="$PROJ/TestData"
TIMEOUT="${CONFORMANCE_TIMEOUT:-900}"

# xqts is 31,470 cases — an order of magnitude past any XSLT chunk — and takes ~37 min on this
# machine. Under the 900 s default it was killed every time and reported "TIMEOUT", which read
# as a hang and sat unexplained for weeks; it is NOT hung, it is big, exactly as the strm note
# below says of streaming. Give it its own budget rather than pushing the global default up and
# letting a genuinely wedged XSLT chunk sit for an hour. CONFORMANCE_TIMEOUT still overrides.
XQTS_TIMEOUT="${CONFORMANCE_XQTS_TIMEOUT:-3600}"
OUT="${CONFORMANCE_OUT:-$ROOT/conformance-results}"
CONFIG="${CONFORMANCE_CONFIG:-Debug}"

# Order is cheapest-first so a broken engine shows up in the first minute rather than
# the fortieth. It is not alphabetical on purpose.
#
# These are "CHUNKS", not "GROUPS", even though they mostly hold Trait("Group") values:
# GROUPS is a bash-maintained special array of the current user's Unix group IDs.
# Assigning to it is silently ignored — and NOT caught by `set -u`, because it is always
# defined and never empty — so a `GROUPS=("$@")` version of this script cheerfully ran
# fifteen chunks named 1000, 24, 25, 27 … Do not rename these back.
#
# strm is split three ways because Group=strm is 90 test-sets — more than twice the next
# largest (fn, 35) and about four times typical. As one chunk it does not finish inside
# any sane timeout: a 900 s run got through 49 of the 90, still passing, and was killed
# mid-sweep. It is NOT hung, it is big. The three sub-chunks follow the class split that
# already exists in the test project, ~30 test-sets each.
ALL_CHUNKS=(attr decl type sandp fn strm1 strm2 strm3 expr misc insn)

# Chunk -> --filter. Most chunks are a Group trait; the strm sub-chunks address the test
# CLASS instead, since all three carry Group=strm. The trailing dot matters: without it
# `XsltStreamingTests` also substring-matches XsltStreamingTests2 and 3.
filter_for() {
  case "$1" in
    xqts)  echo "Suite=XQTS" ;;
    strm1) echo "FullyQualifiedName~XsltStreamingTests." ;;
    strm2) echo "FullyQualifiedName~XsltStreamingTests2." ;;
    strm3) echo "FullyQualifiedName~XsltStreamingTests3." ;;
    *)     echo "Suite=XSLT&Group=$1" ;;
  esac
}

usage() { sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

if [ "${1:-}" = "--list" ] || [ "${1:-}" = "-l" ]; then
  printf 'XSLT chunks (in run order): %s\n' "${ALL_CHUNKS[*]}"
  printf 'XQuery suite:               xqts   (included by --all)\n'
  exit 0
fi
if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then usage; exit 0; fi

# --all adds the QT3/XQuery suite to the XSLT chunks. It is NOT the bare default: locally
# you almost always want the XSLT sweep, and xqts roughly doubles the wall clock. CI uses
# --all so the nightly covers exactly what the pre-chunking `dotnet test <project>` did —
# that command had no filter, so it ran XQTS too, and defaulting to XSLT-only here would
# have quietly dropped XQuery coverage from the nightly.
if [ "${1:-}" = "--all" ]; then set -- "${ALL_CHUNKS[@]}" xqts; fi

# A missing suite does NOT fail the fixtures — IsTestDataAvailable goes false and every
# test returns green without executing anything. A green run that tested nothing is the
# single most expensive failure mode here, so refuse to start.
missing=0
for s in xslt30-test qt3tests; do
  if [ ! -e "$SUITES/$s/catalog.xml" ]; then
    echo "error: $s missing or incomplete at $SUITES/$s" >&2
    missing=1
  fi
done
if [ "$missing" = 1 ]; then
  echo "refusing to run: the fixtures would skip silently and report a green sweep that tested nothing." >&2
  echo "fetch them with: ./scripts/fetch-conformance-suites.sh" >&2
  exit 1
fi

export XSLT30_TEST_SUITE="$SUITES/xslt30-test"
export QT3_TEST_SUITE="$SUITES/qt3tests"
# ICU is mandatory: invariant globalization silently changes collation and
# normalize-unicode() results rather than failing, which would quietly move the score.
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

if [ $# -gt 0 ]; then CHUNKS=("$@"); else CHUNKS=("${ALL_CHUNKS[@]}"); fi

# `strm` stays usable as a name — it expands to its three sub-chunks rather than erroring,
# so muscle memory and older notes keep working.
expanded=()
for g in "${CHUNKS[@]}"; do
  if [ "$g" = "strm" ]; then expanded+=(strm1 strm2 strm3); else expanded+=("$g"); fi
done
CHUNKS=("${expanded[@]}")

# Reject unknown chunk names up front. `dotnet test --filter` exits 0 when a filter
# matches NOTHING, so a typo (or a mangled argument) would otherwise run, report
# nothing, and look like an infrastructure hiccup rather than the mistake it is.
for g in "${CHUNKS[@]}"; do
  ok=0
  for k in "${ALL_CHUNKS[@]}" xqts; do [ "$g" = "$k" ] && ok=1 && break; done
  if [ $ok -eq 0 ]; then
    echo "error: unknown chunk '$g'" >&2
    echo "valid: ${ALL_CHUNKS[*]} xqts" >&2
    exit 2
  fi
done

echo "chunks: ${CHUNKS[*]}"

mkdir -p "$OUT"
: > "$OUT/summary.txt"

echo "building once (chunks then run with --no-build)..."
if ! dotnet build "$PROJ/PhoenixmlDb.Conformance.Tests.csproj" -c "$CONFIG" -f net10.0 > "$OUT/build.log" 2>&1; then
  tail -20 "$OUT/build.log" >&2
  echo "error: build failed — see $OUT/build.log" >&2
  exit 1
fi

for s in xslt30-test qt3tests; do
  rev=$(git -C "$SUITES/$s" rev-parse --short HEAD 2>/dev/null || echo "unpinned")
  echo "$s @ $rev" | tee -a "$OUT/summary.txt"
done
echo | tee -a "$OUT/summary.txt"

failed=0
started=$SECONDS
for g in "${CHUNKS[@]}"; do
  filter=$(filter_for "$g")
  s=$SECONDS
  # A .trx PER CHUNK rather than one for the sweep: a chunk that times out still leaves
  # the other chunks' results intact and parseable, which a single combined trx does not.
  #
  # verbosity=detailed is not noise, it is THE result. A chunk reports "Passed" as long as
  # the fixture ran — it asserts only that some cases passed — so W3C cases that fail
  # inside a test-set are invisible at default verbosity. Detailed output carries the
  # per-test-set tallies and the `FAILED: <case>` lines with their error codes, which is
  # the only place the real conformance score exists. Without this the sweep can report
  # nine green chunks while hundreds of individual cases fail.
  # Per-chunk timeout: xqts needs far longer than the XSLT groups (see XQTS_TIMEOUT above).
  chunk_timeout="$TIMEOUT"
  [ "$g" = "xqts" ] && [ -z "${CONFORMANCE_TIMEOUT:-}" ] && chunk_timeout="$XQTS_TIMEOUT"

  timeout "$chunk_timeout" dotnet test "$PROJ/PhoenixmlDb.Conformance.Tests.csproj" \
      -c "$CONFIG" -f net10.0 --no-build --filter "$filter" \
      --logger "console;verbosity=detailed" \
      --logger "trx;LogFileName=$g.trx" --results-directory "$OUT" > "$OUT/$g.log" 2>&1
  rc=$?
  d=$((SECONDS - s))

  if [ $rc -eq 124 ]; then
    line="TIMEOUT after ${chunk_timeout}s"
    failed=1
  else
    # The detailed console logger does NOT print the terse "Passed! - Failed: 0, ..."
    # summary; it prints "Test Run Successful./Failed." followed by Total tests:/Passed:/
    # Failed:. Parse that shape — keying on the terse line made every chunk report
    # "NO RESULT (exit 0)" the moment detailed logging went in.
    fx=$(grep -E "^Test Run (Successful|Failed)" "$OUT/$g.log" | tail -1 | sed 's/Test Run //; s/\.//')
    ftot=$(grep -oE "^Total tests: [0-9]+" "$OUT/$g.log" | tail -1 | grep -oE "[0-9]+")
    ffail=$(grep -oE "^ *Failed: +[0-9]+" "$OUT/$g.log" | tail -1 | grep -oE "[0-9]+")
    line=""
    [ -n "$fx" ] && line="fixture $fx (${ftot:-?} tests${ffail:+, $ffail failed})"
    if grep -q "No test matches the given testcase filter" "$OUT/$g.log"; then
      # Exit 0 with nothing run. Loudly not-green: this is the silent-pass shape.
      line="MATCHED NOTHING — filter ran no tests"
      failed=1
    elif [ -z "$line" ]; then
      line="NO RESULT (exit $rc) — see $OUT/$g.log"
      failed=1
    elif [ $rc -ne 0 ]; then
      failed=1
    fi
  fi
  # The xunit pass/fail above only says the FIXTURE ran. Tally the W3C cases the chunk
  # actually executed — that is the number anyone means by "conformance", and it is the
  # one a green chunk can hide.
  # grep -c prints its count AND exits 1 when the count is zero, so `|| echo 0` fires as well
  # and nfail becomes "0\n0". Every chunk with no failures then blew up the integer comparison
  # below with "integer expression expected", which is what the sandp chunk was hitting.
  nfail=$(grep -c "^ FAILED: " "$OUT/$g.log" 2>/dev/null || true)
  nfail=${nfail:-0}
  # Anchor on "^ Results: " for the same reason nfail anchors on "^ FAILED: ": when a fixture
  # assertion fails, xunit echoes that test's output a SECOND time behind an "[xUnit.net ...]"
  # prefix, and an unanchored grep counted the test-set twice. That inflated the case total by
  # exactly the size of every failing test-set — `fn` reported 1135 cases instead of 1131 the
  # moment load-xquery-module went red. The failure count was already right; only this tally
  # was wrong, so the two disagreed silently.
  read -r cp ct <<<"$(grep -E "^ Results: " "$OUT/$g.log" 2>/dev/null |
                      grep -oE "[0-9]+/[0-9]+" |
                      awk -F/ '{p+=$1; t+=$2} END {print p+0, t+0}')"
  if [ "${ct:-0}" -gt 0 ]; then
    line="$line | $cp/$ct cases $(awk -v p="$cp" -v t="$ct" 'BEGIN{printf "%.1f%%", 100*p/t}')"
    # The per-test-set "Results:" lines are the XSLT runner's tallies. The XQuery runner
    # emits far more FAILED lines than it emits Results lines, so a tally that cannot
    # account for the failures is partial — say so rather than quietly under-reporting.
    if [ "$nfail" -gt $((ct - cp)) ]; then
      line="$line, $nfail failed (TALLY PARTIAL — more failures than these test-sets cover)"
    else
      line="$line, $nfail failed"
    fi
  elif [ "$nfail" -gt 0 ]; then
    line="$line | $nfail cases failed (no per-test-set tallies in this chunk)"
  fi

  printf '%-7s %4ds  %s\n' "$g" "$d" "$line" | tee -a "$OUT/summary.txt"
done

total=$((SECONDS - started))
printf '\n%d chunk(s) in %dm%02ds — logs in %s\n' "${#CHUNKS[@]}" $((total / 60)) $((total % 60)) "$OUT" |
  tee -a "$OUT/summary.txt"
[ $failed -eq 0 ] || echo "one or more chunks failed" | tee -a "$OUT/summary.txt"
exit $failed
