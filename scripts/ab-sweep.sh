#!/usr/bin/env bash
# Same-checkout A/B conformance sweep.
#
# Runs the suite twice from ONE checkout — once with the code as it is on the
# base branch, once as it is here — and reports the per-set difference in
# failing cases. One checkout means one corpus, one baseline and one machine
# state, so a difference between the arms is the change and nothing else.
#
# Both src/ and tests/ are swapped: a harness change moves the numbers exactly
# as an engine change does, and swapping only src/ would report a harness fix
# as no change at all.
#
#   scripts/ab-sweep.sh                 # whole suite
#   scripts/ab-sweep.sh insn misc       # named chunks only
#
# Env:
#   AB_BASE   base to compare against (default origin/main)
#   AB_OUT    directory for the two runs (default a scratch dir under /tmp)
#
# The two guards below exist because both of their failure modes are SILENT:
# each produces a clean-looking result rather than an error, which is the one
# thing a measurement must never do.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
BASE="${AB_BASE:-origin/main}"
OUT="${AB_OUT:-/tmp/ab-sweep-$$}"

# GUARD 1 — the arms must differ.
# The usual procedure stashes src/ for the baseline arm, which moves nothing
# when the change is already COMMITTED: both arms then measure identical code
# and the run reports a flat, clean, meaningless result. A comparison that
# cannot distinguish its two arms is not a weak measurement, it is not a
# measurement.
if git diff --quiet "$BASE" HEAD -- src tests; then
  echo "A/B ABORT: src and tests are identical to $BASE — the two arms would measure the same thing." >&2
  exit 2
fi

# GUARD 2 — the change must be somewhere it can ship from.
# A commit made while sitting on an already-merged branch (or on main) looks
# fine locally and is only noticed at push time, if then. On a branch that is
# not yet merged it would ride out inside an unrelated PR.
BRANCH="$(git rev-parse --abbrev-ref HEAD)"
if [ "$BRANCH" = "main" ] || [ "$BRANCH" = "HEAD" ]; then
  echo "A/B ABORT: on '$BRANCH' — commit the change to a working branch first." >&2
  exit 2
fi
if git merge-base --is-ancestor HEAD "$BASE" 2>/dev/null; then
  echo "A/B ABORT: '$BRANCH' is already merged into $BASE — new commits here ship inside an unrelated PR." >&2
  exit 2
fi

mkdir -p "$OUT"
echo "A/B: $BRANCH vs $BASE   chunks: ${*:-all}   out: $OUT"

# ISOLATED MODE (AB_WORKTREE=1): run both arms in a throwaway git worktree so the
# sweep never touches the tree you are working in. The guards above check their
# preconditions at the START; "nobody edits or switches branches for the next ten
# minutes" is a precondition that must hold THROUGHOUT, and no check can assert
# that. A separate worktree removes it instead of verifying it — and lets you keep
# working while the sweep runs.
#
# The corpus (tests/.../TestData) is untracked and ~550MB, so it is symlinked in
# rather than copied; runs only read it.
if [ "${AB_WORKTREE:-0}" = "1" ]; then
  WT="$(mktemp -d /tmp/ab-worktree-XXXX)"
  CORPUS="tests/PhoenixmlDb.Conformance.Tests/TestData"
  cleanup_worktree() { cd "$ROOT"; git worktree remove --force "$WT/tree" >/dev/null 2>&1; rm -rf "$WT"; }
  trap cleanup_worktree EXIT
  git worktree add -q --detach "$WT/tree" HEAD || { echo "A/B ABORT: could not create worktree" >&2; exit 2; }
  ln -s "$ROOT/$CORPUS" "$WT/tree/$CORPUS"
  cd "$WT/tree"
  echo "  isolated: $WT/tree (your working tree is untouched)"
fi

run_arm() {  # $1 = arm name, rest = chunks
  local arm="$1"; shift
  rm -rf "${OUT:?}/$arm"
  # A stale runtimeconfig from a previous build changes strict-mode behaviour
  # between arms; delete it so both arms build their own.
  find . -path '*/bin/*' -name '*.runtimeconfig.json' -path '*Conformance*' -delete
  CONFORMANCE_OUT="$OUT/$arm" ./scripts/conformance.sh ${*:-} > "$OUT/$arm.out" 2>&1
}

if [ "${AB_WORKTREE:-0}" != "1" ]; then
  trap 'git checkout -q HEAD -- src tests' EXIT
fi
git checkout -q "$BASE" -- src tests
run_arm base "$@"
git checkout -q HEAD -- src tests
run_arm change "$@"

python3 - "$OUT/base" "$OUT/change" <<'PY'
import glob, os, re, sys
def failures(d):
    out = {}
    for f in glob.glob(os.path.join(d, '*.log')):
        text = open(f, errors='replace').read()
        out[os.path.basename(f)] = set(re.findall(r'FAILED: (\S+)', text))
    return out
base, change = failures(sys.argv[1]), failures(sys.argv[2])
total = lambda d: sum(len(v) for v in d.values())
print(f'base failures {total(base)}  change failures {total(change)}')
moved = False
for name in sorted(set(base) | set(change)):
    lost = sorted(change.get(name, set()) - base.get(name, set()))
    won = sorted(base.get(name, set()) - change.get(name, set()))
    if lost or won:
        moved = True
        print(f'{name}  LOST {lost}  WON {won}')
if not moved:
    print('no per-set differences')
PY
