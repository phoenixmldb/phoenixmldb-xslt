#!/usr/bin/env bash
# Refuses to approve a release tag until the things that went wrong in past releases are checked.
#
#   scripts/pre-tag.sh <repo-dir> <tag> [--gate-log FILE]
#     <tag>  v2.9.0 (library) or cli-v2.9.0 (xquery4 CLI)
#     --gate-log  the console output of scripts/release-gate.sh (or a nightly report's gate.log);
#                 required for an XQuery or Xslt v-tag
#
# Each check exists because of a real miss:
#   version      <Version> in Directory.Build.props equals the tag. (XQuery v2.7.0 was cut with
#                2.6.0 in the file; the guard in CI stopped it, after the tag.)
#   tree         clean, on main, equal to origin/main.
#   train        scripts/check-release-train.sh and check-pins.sh where the repo has them.
#   ci           main's CI for this exact commit succeeded, INCLUDING a Windows job. (2.7.0: a
#                security merge from an advisory fork, which runs no CI, crashed on Windows only.)
#   notes        RELEASES.md has the section for this version.
#   claims       every #N the notes cite as fixed is closed or merged; a still-open one must be
#                marked "known issue"/"reopened" on that line. (#105 was announced fixed in the CLI
#                while the CLI still dropped xmlns="".)
#   gate         the release gate passed for this code: the gated commit differs from HEAD only in
#                RELEASES.md and the two Directory.*.props files. (Engine repos, v-tags only.)
#   advisories   lists draft advisories, which must not be published before the package is out.
#
# Exit 0 only when every check passes. It prints a short list of what it cannot check by itself.
set -uo pipefail

REPO_DIR="${1:?usage: pre-tag.sh <repo-dir> <tag> [--gate-log FILE]}"; TAG="${2:?tag}"; shift 2
GATE_LOG=""
while [ $# -gt 0 ]; do case "$1" in --gate-log) GATE_LOG="$2"; shift 2 ;; *) echo "unknown: $1" >&2; exit 2 ;; esac; done
cd "$REPO_DIR" || exit 2
NAME="$(basename "$(git rev-parse --show-toplevel)")"
SLUG="$(git remote get-url origin | sed -E 's#.*github.com[:/]##; s#\.git$##')"
case "$TAG" in cli-v*) KIND=cli; VER="${TAG#cli-v}" ;; v*) KIND=lib; VER="${TAG#v}" ;; *) echo "tag must be v<ver> or cli-v<ver>" >&2; exit 2 ;; esac
FAIL=0
ok()   { printf '  ok    %s\n' "$*"; }
bad()  { printf '  FAIL  %s\n' "$*"; FAIL=1; }
warn() { printf '  warn  %s\n' "$*"; }
echo "pre-tag: $SLUG $TAG"

# version
FILEVER="$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props 2>/dev/null | head -1)"
[ "$FILEVER" = "$VER" ] && ok "version $VER" || bad "Directory.Build.props says ${FILEVER:-nothing}, tag says $VER — bump it in a PR first"

# tree
git fetch -q origin main
BR="$(git branch --show-current)"; HEAD="$(git rev-parse HEAD)"; ORIGIN="$(git rev-parse origin/main)"
[ "$BR" = main ] || bad "on branch '$BR', not main"
[ -z "$(git status --porcelain --untracked-files=no)" ] || bad "uncommitted changes"
[ "$HEAD" = "$ORIGIN" ] && ok "HEAD = origin/main (${HEAD:0:7})" || bad "HEAD ${HEAD:0:7} != origin/main ${ORIGIN:0:7}"
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null && bad "tag $TAG already exists locally"
git ls-remote --exit-code --tags origin "$TAG" >/dev/null 2>&1 && bad "tag $TAG already exists on origin"

# train
if [ -x scripts/check-release-train.sh ] && { [ "$KIND" = cli ] || [ "$NAME" != phoenixmldb-xquery ]; }; then
  scripts/check-release-train.sh "$VER" >/tmp/pretag-train.$$ 2>&1 && ok "release train" || { bad "check-release-train.sh:"; sed 's/^/        /' /tmp/pretag-train.$$; }
  rm -f /tmp/pretag-train.$$
fi
if [ -x scripts/check-pins.sh ]; then
  scripts/check-pins.sh >/tmp/pretag-pins.$$ 2>&1 && ok "pins" || { bad "check-pins.sh:"; sed 's/^/        /' /tmp/pretag-pins.$$; }
  rm -f /tmp/pretag-pins.$$
fi

# ci
RUN="$(gh run list -R "$SLUG" --branch main --limit 20 --json databaseId,headSha,workflowName,conclusion,status \
  -q ".[]|select(.headSha==\"$HEAD\" and .workflowName==\"CI\")|\"\(.databaseId) \(.status) \(.conclusion)\"" | head -1)"
if [ -z "$RUN" ]; then bad "no CI run on main for ${HEAD:0:7}"
else
  set -- $RUN
  if [ "$2 $3" != "completed success" ]; then bad "CI on ${HEAD:0:7} is '$2 $3'"
  else
    WIN="$(gh run view "$1" -R "$SLUG" --json jobs -q '[.jobs[]|select((.name|ascii_downcase|test("windows")) and .conclusion=="success")]|length')"
    [ "${WIN:-0}" -gt 0 ] && ok "CI green on ${HEAD:0:7}, $WIN Windows job(s)" || bad "CI on ${HEAD:0:7} has no successful Windows job"
  fi
fi

# notes
if [ "$KIND" = cli ]; then PAT="ships on \`cli-v$VER\`"; else PAT="## $VER"; fi
if grep -qF "$PAT" RELEASES.md 2>/dev/null; then
  ok "RELEASES.md has the $VER section"
  if [ "$KIND" = cli ]; then SECTION="$(awk -v p="$PAT" 'index($0,p){f=1} f&&/^## /&&!index($0,p){exit} f' RELEASES.md)"
  else SECTION="$(awk -v v="## $VER" 'index($0,v)==1{f=1;next} f&&/^## /{exit} f' RELEASES.md)"; fi
  # claims
  for n in $(echo "$SECTION" | grep -oE '#[0-9]+' | tr -d '#' | sort -un); do
    line="$(echo "$SECTION" | grep -m1 "#$n\b")"
    st="$(gh api "repos/$SLUG/issues/$n" -q 'if .pull_request then (if .pull_request.merged_at then "merged" else "open-pr" end) else .state end' 2>/dev/null)"
    case "$st" in
      closed|merged) : ;;
      open|open-pr) echo "$line" | grep -qiE 'known issue|reopened|not fixed|open' && warn "#$n is open (marked as known in the notes)" || bad "#$n is cited in the notes but still open: ${line:0:100}" ;;
      *) warn "#$n not found in $SLUG (another repo?)" ;;
    esac
  done
  ok "cited issues checked"
else bad "RELEASES.md has no '$PAT'"; fi

# gate
if [ "$KIND" = lib ] && { [ "$NAME" = phoenixmldb-xquery ] || [ "$NAME" = phoenixmldb-xslt ]; }; then
  if [ -z "$GATE_LOG" ] || [ ! -f "$GATE_LOG" ]; then bad "no --gate-log given (run scripts/release-gate.sh --base <published> --cand-dev, keep its output)"
  elif ! grep -q "GATE PASSED" "$GATE_LOG"; then bad "gate log does not say GATE PASSED: $GATE_LOG"
  else
    HDR="$(grep -m1 '=== release gate' "$GATE_LOG")"
    if [ "$NAME" = phoenixmldb-xslt ]; then G="$(echo "$HDR" | grep -oP '(?<=local )[0-9a-f]{7,}')"; else G="$(echo "$HDR" | grep -oP '(?<=xquery )[0-9a-f]{7,}')"; fi
    if [ -z "$G" ] || ! git cat-file -e "$G^{commit}" 2>/dev/null; then bad "gated commit '${G:-?}' from the log is not known here: $HDR"
    else
      D="$(git diff --name-only "$G" HEAD -- . ':(exclude)RELEASES.md' ':(exclude)Directory.Build.props' ':(exclude)Directory.Packages.props')"
      [ -z "$D" ] && ok "gate passed on $G; HEAD differs only in notes and props" || { bad "code changed since the gated commit $G:"; echo "$D" | head -10 | sed 's/^/        /'; }
    fi
  fi
fi

# advisories
DRAFTS="$(gh api "repos/$SLUG/security-advisories?state=draft" -q '.[]|"\(.ghsa_id) \(.summary)"' 2>/dev/null)"
[ -n "$DRAFTS" ] && echo "$DRAFTS" | while read -r a; do warn "draft advisory (publish only after the package is on NuGet): $a"; done

echo
echo "Not checked automatically, do these by hand:"
echo "  - run each 'Fixed' item of the notes against the BUILT package (not the PR description)"
echo "  - after publish: wait for NuGet, install into a fresh project / tool path, then create the GitHub Release"
echo "  - MCP servers: dispatch follow-engine-train.yml after cli-v$VER"
[ $FAIL -eq 0 ] && { echo; echo "PRE-TAG PASSED: $TAG may be tagged."; } || { echo; echo "PRE-TAG FAILED: do not tag $TAG."; }
exit $FAIL
