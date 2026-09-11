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
#   CONFORMANCE_CONFIG    Debug|Release (default RELEASE — see below)
#
# Why Release is the default. Debug was, and it cost 38 cases across 21 sets — 0.35 points of
# conformance — entirely to timeouts. Measured, same commit, same machine:
#
#       Debug    10044/10630  94.49%   43 timeouts
#       Release  10082/10630  94.84%    5 timeouts
#       21 sets better under Release, ZERO sets worse.
#
# misc/bug-3701 is the clearest case: ~10.5s in Debug against the harness's 10s cap, ~5.7s in
# Release. It is not flaky, it is over the line in one configuration and comfortably under in the
# other, which is why the database repo's package-based floors have always shown it passing.
#
# The point is not that Release is faster. It is that RELEASE IS WHAT SHIPS. A Debug measurement
# does not measure the artifact anyone receives, so a conformance figure taken from one is not a
# claim about the product — and this one understated it. That is the fail-open shape inverted:
# a harness that undercounts is still a harness reporting something other than the truth.
#   CONFORMANCE_BASELINE  per-set baseline file (default scripts/conformance-baseline.tsv)
#   CONFORMANCE_UPDATE_BASELINE=1   rewrite the baseline from this run instead of checking it
#
# Per-set gating. Chunk totals CANNOT express a regression: `insn` once went 1497 -> 1508
# while insn/call-template quietly lost 2 cases, because try gained 7 and analyze-string
# gained 4. An aggregate hides a loss exactly when something else in the aggregate improves
# — which is when you are most likely to be reading it and least likely to be suspicious.
# The same shape as the fail-open harness bug (BUGS.md #28): a check that cannot express the
# thing it exists to catch. So every test-set's passing count is recorded and compared
# individually, and ANY set going down fails the run regardless of what the totals did.
# A consumer found the call-template drop before this script could; that is the gap this
# closes. "Remember to also diff the per-case list" is not a control.
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
CONFIG="${CONFORMANCE_CONFIG:-Release}"

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

# ---- per-set regression gate -------------------------------------------------
# Pair each "Running N tests from <set>" with the "Results: X/Y passed" that follows it.
BASELINE="${CONFORMANCE_BASELINE:-$ROOT/scripts/conformance-baseline.tsv}"
CURRENT="$OUT/per-set-current.tsv"
: > "$CURRENT"
for g in "${CHUNKS[@]}"; do
  [ -f "$OUT/$g.log" ] || continue
  awk '/^ Running [0-9]+ tests from /{set=$NF}
       /^ Results: /{split($2,a,"/"); if(set!=""){print set"\t"a[1]"\t"a[2]; set=""}}' \
    "$OUT/$g.log" >> "$CURRENT"
done
sort -o "$CURRENT" "$CURRENT"

if [ "${CONFORMANCE_UPDATE_BASELINE:-0}" = "1" ]; then
  # Only the sets this run actually covered are refreshed; sets from other chunks are kept,
  # so updating after a single-chunk run cannot silently erase the rest of the baseline.
  if [ -f "$BASELINE" ]; then
    # Carry each set's existing tolerance across the refresh — re-baselining must not
    # silently reset a tolerance someone set deliberately.
    awk -F'\t' 'NR==FNR{tol[$1]=$4; next}
                 {t=($1 in tol && tol[$1]!="")?tol[$1]:""; print $1"\t"$2"\t"$3 (t==""?"":"\t" t)}' \
      "$BASELINE" "$CURRENT" > "$CURRENT.tol"
    awk -F'\t' 'NR==FNR{seen[$1]=1; next} !($1 in seen)' "$CURRENT" "$BASELINE" > "$BASELINE.keep"
    cat "$CURRENT.tol" "$BASELINE.keep" | sort -o "$BASELINE"
    rm -f "$BASELINE.keep" "$CURRENT.tol"
  else
    cp "$CURRENT" "$BASELINE"
  fi
  echo "per-set baseline updated: $BASELINE" | tee -a "$OUT/summary.txt"
elif [ -f "$BASELINE" ]; then
  # Optional 4th baseline column: cases this set may lose without failing the run.
  #
  # Measured, and the first measurement was WRONG. Two back-to-back serial sweeps gave
  # identical per-set counts across all 89 streaming sets, and that was recorded here as
  # "no variance, tolerance 0 everywhere". A third clean run then produced strm1 699 where
  # both earlier ones gave 700. Two agreeing samples do not establish zero variance — the
  # same over-claim from too small an observation that BUGS.md #40 catalogues, made while
  # writing the control meant to catch such things.
  #
  # So: exactly ONE set carries a tolerance, and only because its count moved between two
  # runs with no source change and no competing load — strm/sf-min (37/36/37). Everything
  # else stays at 0. Do not widen tolerances to silence a failure you have not first
  # reproduced under a quiet machine: a gate that cries wolf gets ignored, but a gate with
  # slack in it is worse, because it never fires at all.
  #
  # Baseline each set at the value it RELIABLY achieves, and re-baseline only from a quiet
  # machine. Two rules that pull against each other, both learned here:
  #
  #   Never lower a baseline from a perturbed run. Three streaming sets briefly lost a case
  #   each when CLI probes ran alongside a re-baseline; taking that run at face value would
  #   have silently lowered the bar, which is the failure this gate exists to prevent.
  #
  #   But "always take the maximum" is wrong too, and it was the rule here first. It records
  #   a lucky run as the standard and then fires on every ordinary one. misc/bug was
  #   baselined at 73 from a run where bug-3701 happened to finish inside its 10s timeout;
  #   it does not usually, so the gate reported a regression on a build that was PROVEN
  #   identical — the same drop appeared with the change reverted.
  #
  # For a set demonstrated unstable, baseline the value it reaches every time (misc/bug 72,
  # si-element 67 of an observed 69/68/67) rather than its best. A gate that fires on noise
  # gets ignored, and an ignored gate catches nothing — which is the whole point of #40.
  #
  # PAIRING RULE — a baseline raise commits its evidence in the same PR.
  #
  # `conformance-results/summary.txt` is tracked (see .gitignore) so a published figure has
  # versioned evidence. It only serves that purpose while it describes the run behind the
  # committed baseline. It drifted once: the baseline said 10,163/29,534 while the tracked
  # summary was a timed-out XQTS run reporting 1,271/1,286 at 98.8% (BUGS.md #55). A stale
  # artifact is worse than a missing one — a missing one makes a figure unverifiable, a stale
  # one makes it look refuted.
  #
  # So: raise the baseline and update summary.txt in the SAME PR, or neither.
  #
  # The evidence must be a CLEAN CONFIRMING RUN on the raised baseline, not one of the runs the
  # raise was computed from. Raises take the minimum across runs, so no single contributing run
  # equals the result — one of the two that produced this baseline had call-template at 38
  # (10,164) and the other lost a chunk to a SIGSEGV. Neither summary is the baseline. A fresh
  # --all afterwards is, and it re-tests the gate on the numbers actually committed.
  #
  # This is process, not machinery: raises are hand-edited min-of-runs rather than
  # CONFORMANCE_UPDATE_BASELINE=1, so the script cannot enforce it. Reviewers can: a raise
  # whose PR has no accompanying summary is incomplete.
  #
  # Note the default OUT is the tracked directory, so ANY run overwrites the artifact —
  # including a single-chunk one. Do not commit a summary from `conformance.sh <chunk>`; it
  # replaces whole-suite evidence with one chunk's and still looks like a valid file.
  regressed="$(awk -F'\t' '
    NR==FNR { base[$1]=$2; tol[$1]=($4==""?0:$4); next }
    ($1 in base) && $2 < base[$1] - tol[$1] {
      printf "  %s: %d -> %d  (-%d, tolerance %d)\n", $1, base[$1], $2, base[$1]-$2, tol[$1] }
  ' "$BASELINE" "$CURRENT")"
  gained="$(awk -F'\t' '
    NR==FNR { base[$1]=$2; next }
    ($1 in base) && $2 > base[$1] { printf "  %s: %d -> %d  (+%d)\n", $1, base[$1], $2, $2-base[$1] }
  ' "$BASELINE" "$CURRENT")"
  newsets="$(awk -F'\t' 'NR==FNR{base[$1]=1; next} !($1 in base){printf "  %s (%d/%d)\n", $1, $2, $3}' \
              "$BASELINE" "$CURRENT")"
  # A baselined set this run should have covered but that reported NO result. The checks above
  # all iterate CURRENT, so a set that never ran was never visited: when strm3's test host
  # crashed, sx-GeneralComp-le/-ne vanished and the per-set section said nothing (BUGS.md #54).
  # A set with no verdict is worse news than a set with a lower count, so it fails the run too.
  #
  # Only sets whose chunk ran are expected — a single-chunk run must not flag the rest of the
  # baseline. Ownership: a QT3 set (no tests/ prefix) belongs to xqts; tests/<g>/ belongs to
  # chunk <g>; the strm sub-chunks list their sets in their test classes, read from source here
  # so there is no second copy of that list to drift.
  EXPECTED_STRM="$OUT/per-set-expected-strm.txt"
  : > "$EXPECTED_STRM"
  for n in 1 2 3; do
    case " ${CHUNKS[*]} " in *" strm$n "*) ;; *) continue ;; esac
    cls="$PROJ/Xslt/XsltStreamingTests$([ $n -eq 1 ] || echo $n).cs"
    listed="$(grep -o 'InlineData("tests/strm/[^"]*")' "$cls" 2>/dev/null | sed 's/^InlineData("//; s/")$//')"
    # Fail closed: a renamed class or a reshaped attribute would otherwise yield an empty list,
    # and this check would go blind for strm$n without saying so.
    if [ -z "$listed" ]; then
      echo "PER-SET CHECK BROKEN — could not read strm$n's test-set list from $cls" | tee -a "$OUT/summary.txt"
      failed=1
    fi
    printf '%s\n' "$listed" >> "$EXPECTED_STRM"
  done
  noresult="$(awk -F'\t' -v ran=" ${CHUNKS[*]} " '
    FILENAME == ARGV[1] { strm[$1] = 1; next }
    FILENAME == ARGV[2] { cur[$1] = 1; next }
    {
      if ($1 !~ /^tests\//)          owned = index(ran, " xqts ") > 0
      else if ($1 ~ /^tests\/strm\//) owned = ($1 in strm)
      else { split($1, p, "/");       owned = index(ran, " " p[2] " ") > 0 }
      if (owned && !($1 in cur)) printf "  %s: baselined %d, NO RESULT this run\n", $1, $2
    }
  ' "$EXPECTED_STRM" "$CURRENT" "$BASELINE")"
  [ -n "$gained" ]  && { echo "per-set GAINS:"    | tee -a "$OUT/summary.txt"; echo "$gained"  | tee -a "$OUT/summary.txt"; }
  [ -n "$newsets" ] && { echo "per-set NEW sets:" | tee -a "$OUT/summary.txt"; echo "$newsets" | tee -a "$OUT/summary.txt"; }
  if [ -n "$regressed" ]; then
    echo "PER-SET REGRESSION — these test-sets lost cases:" | tee -a "$OUT/summary.txt"
    echo "$regressed" | tee -a "$OUT/summary.txt"
    echo "If the drop is intended, re-run with CONFORMANCE_UPDATE_BASELINE=1 to re-baseline." |
      tee -a "$OUT/summary.txt"
    failed=1
  fi
  if [ -n "$noresult" ]; then
    echo "PER-SET NO RESULT — baselined test-sets this run should have covered did not report:" |
      tee -a "$OUT/summary.txt"
    echo "$noresult" | tee -a "$OUT/summary.txt"
    failed=1
  fi
else
  echo "no per-set baseline at $BASELINE — create it with CONFORMANCE_UPDATE_BASELINE=1" |
    tee -a "$OUT/summary.txt"
fi

total=$((SECONDS - started))
printf '\n%d chunk(s) in %dm%02ds — logs in %s\n' "${#CHUNKS[@]}" $((total / 60)) $((total % 60)) "$OUT" |
  tee -a "$OUT/summary.txt"
[ $failed -eq 0 ] || echo "one or more chunks failed" | tee -a "$OUT/summary.txt"
exit $failed
