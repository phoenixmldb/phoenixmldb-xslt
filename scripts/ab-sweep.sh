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
#   AB_SOURCE 1 (default): build against the sibling engines' SOURCE, as CI does; 0: against the
#             pinned packages. xslt main can depend on unreleased XQuery API, so package mode can
#             fail to build at all. In AB_WORKTREE mode the siblings are cloned at their main next
#             to the throwaway worktree (scripts/clone-siblings.sh); in place, the checkouts beside
#             this repo are used. Both arms build against the same siblings.
#
# The three guards below exist because all of their failure modes are SILENT:
# each produces a clean-looking result rather than an error, which is the one
# thing a measurement must never do. Guard 3's is the worst of them, because it
# produces a FLATTERING one.
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

# GUARD 3 — the branch must not be BEHIND base.
# The base arm is $BASE, so anything merged into $BASE since the branch was cut
# is present in the base arm and absent from the change arm. The two arms then
# differ by the change MINUS those commits, and the sweep credits their effect
# to the change — in the flattering direction, if they were fixes.
#
# A stale branch defeats the guards above because it looks completely healthy:
# it has a real diff against base and it is not merged. Measured for real once —
# a serialize branch cut before a deep-equal fix landed reported 0 losses and
# 3 wins; rebased onto current main it reported 0 losses and 1 win.
BEHIND="$(git rev-list --count HEAD.."$BASE" 2>/dev/null || echo 0)"
if [ "${BEHIND:-0}" -gt 0 ]; then
  echo "A/B ABORT: '$BRANCH' is $BEHIND commit(s) behind $BASE — the base arm would carry fixes the change arm lacks." >&2
  echo "  git rebase $BASE" >&2
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
  # Named like the repo, not "tree": in source mode Directory.Build.targets re-points this repo's
  # own ProjectReferences at <workspace>/phoenixmldb-xslt, so any other name points at nothing.
  cleanup_worktree() { cd "$ROOT"; git worktree remove --force "$WT/phoenixmldb-xslt" >/dev/null 2>&1; rm -rf "$WT"; }
  trap cleanup_worktree EXIT
  git worktree add -q --detach "$WT/phoenixmldb-xslt" HEAD || { echo "A/B ABORT: could not create worktree" >&2; exit 2; }
  ln -s "$ROOT/$CORPUS" "$WT/phoenixmldb-xslt/$CORPUS"
  cd "$WT/phoenixmldb-xslt"
  echo "  isolated: $WT/phoenixmldb-xslt (your working tree is untouched)"
  if [ "${AB_SOURCE:-1}" = "1" ]; then
    ./scripts/clone-siblings.sh phoenixmldb-core phoenixmldb-xquery | sed 's/^/  /' \
      || { echo "A/B ABORT: could not clone the sibling engines" >&2; exit 2; }
  fi
fi
if [ "${AB_SOURCE:-1}" = "1" ]; then
  export PHOENIXML_DEV=1
else
  unset PHOENIXML_DEV
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
# `checkout BASE -- paths` restores BASE's files but leaves the ones HEAD ADDED, so a change that
# adds a source file left it in the base arm, which then failed to build (or, worse, built with
# part of the change). Remove them; `checkout HEAD` below restores them.
git diff --name-only --diff-filter=A "$BASE" HEAD -- src tests | while IFS= read -r f; do rm -f -- "$f"; done
run_arm base "$@"
git checkout -q HEAD -- src tests
run_arm change "$@"

python3 - "$OUT/base" "$OUT/change" <<'PY'
import glob, os, re, sys

# FAIL CLOSED. An arm whose build failed, or whose chunk crashed, writes no FAILED lines, and
# comparing it would report every base failure as WON. That happened: a change that did not
# compile against the pinned packages was reported as +258. Both arms must have run every chunk
# to completion, over the same number of cases, before any difference means anything.
def chunk_totals(d):
    totals = {}
    for line in open(d + '.out', errors='replace'):
        m = re.match(r'^(\S+)\s+\d+s\s+.*\|\s*\d+/(\d+) cases', line)
        if m:
            totals[m.group(1)] = int(m.group(2))
    return totals
arms = {name: chunk_totals(path) for name, path in (('base', sys.argv[1]), ('change', sys.argv[2]))}
problems = []
for name, path in (('base', sys.argv[1]), ('change', sys.argv[2])):
    if 'build failed' in open(path + '.out', errors='replace').read():
        problems.append(f'{name}: build failed (see {path}/build.log)')
    if not arms[name]:
        problems.append(f'{name}: no completed chunk')
if arms['base'] and arms['change'] and arms['base'].keys() != arms['change'].keys():
    problems.append(f"chunks differ: base {sorted(arms['base'])} vs change {sorted(arms['change'])}")
for chunk in sorted(set(arms['base']) & set(arms['change'])):
    if arms['base'][chunk] != arms['change'][chunk]:
        problems.append(f"{chunk}: base ran {arms['base'][chunk]} cases, change {arms['change'][chunk]}")
if problems:
    print('A/B ABORT: the arms are not comparable')
    for problem in problems:
        print('  ' + problem)
    sys.exit(2)

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
