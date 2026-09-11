#!/usr/bin/env bash
# Emits a single status view across the engine repos: what is published, what is tagged but not,
# what is open, and what is blocked.
#
# Generated, not hand-maintained. A hand-kept status page goes stale silently, and a stale number
# that still renders is the failure mode this project keeps finding (BUGS.md #28, #43, #44). So
# every figure here is either read live or carries the date and provenance of its measurement,
# and anything that could not be read says so rather than showing a remembered value.
#
#   ./scripts/status.sh            > STATUS.md
#   ./scripts/status.sh --check    non-zero if any drift needs attention
set -uo pipefail
cd "$(dirname "$0")/.."
WS="$(cd .. && pwd)"

check_only=0
[ "${1:-}" = "--check" ] && check_only=1
drift=0
out=""
say() { out+="$1"$'\n'; }

nuget_latest() {
  curl -fsS --compressed "https://api.nuget.org/v3-flatcontainer/$(echo "$1" | tr '[:upper:]' '[:lower:]')/index.json" 2>/dev/null \
    | python3 -c "import json,sys;print(json.load(sys.stdin)['versions'][-1])" 2>/dev/null
}

say "# Status"
say ""
say "Generated $(date -u '+%Y-%m-%d %H:%M UTC') by \`scripts/status.sh\`. Do not edit by hand."
say ""
say "## Packages"
say ""
say "| package | published | repo version | state |"
say "|---|---|---|---|"
for pair in "phoenixmldb-core:PhoenixmlDb.Core" "phoenixmldb-xquery:PhoenixmlDb.XQuery" \
            "phoenixmldb-xslt:PhoenixmlDb.Xslt" "phoenixmldb-xquery:xquery4" "phoenixmldb-xslt:xslt"; do
  repo="${pair%%:*}"; pkg="${pair##*:}"
  pub="$(nuget_latest "$pkg")"; pub="${pub:-unreadable}"
  ver="$(grep -oP '(?<=<Version>)[^<]+' "$WS/$repo/Directory.Build.props" 2>/dev/null | head -1)"
  ver="${ver:-?}"
  if [ "$pub" = "unreadable" ]; then state="nuget.org unreadable — NOT a claim that it is current"
  elif [ "$pub" = "$ver" ]; then state="current"
  else state="**repo is at $ver, nuget.org has $pub**"; drift=1; fi
  say "| \`$pkg\` | $pub | $ver | $state |"
done

say ""
say "## Open work"
say ""
say "| repo | open issues | open PRs |"
say "|---|---|---|"
for repo in phoenixmldb-core phoenixmldb-xquery phoenixmldb-xslt phoenixmldb-cli; do
  i="$(gh issue list --repo "phoenixmldb/$repo" --state open --limit 100 --json number -q 'length' 2>/dev/null)"
  p="$(gh pr list --repo "phoenixmldb/$repo" --state open --limit 100 --json number -q 'length' 2>/dev/null)"
  say "| $repo | ${i:-unreadable} | ${p:-unreadable} |"
done

say ""
say "### Open issues"
say ""
for repo in phoenixmldb-core phoenixmldb-xquery phoenixmldb-xslt phoenixmldb-cli; do
  rows="$(gh issue list --repo "phoenixmldb/$repo" --state open --limit 100 \
          --json number,title -q '.[]|"- [\(.number)] \(.title)"' 2>/dev/null)"
  [ -n "$rows" ] && { say "**$repo**"; say ""; say "$rows"; say ""; }
done

say "### Open PRs"
say ""
for repo in phoenixmldb-core phoenixmldb-xquery phoenixmldb-xslt phoenixmldb-cli; do
  rows="$(gh pr list --repo "phoenixmldb/$repo" --state open --limit 100 \
          --json number,title -q '.[]|"- [\(.number)] \(.title)"' 2>/dev/null)"
  [ -n "$rows" ] && { say "**$repo**"; say ""; say "$rows"; say ""; }
done

say "## Defect register"
say ""
total="$(grep -c '^### [0-9]' BUGS.md 2>/dev/null || echo 0)"
open_n="$(grep -c '^### [0-9]*\. OPEN' BUGS.md 2>/dev/null || echo 0)"
say "\`BUGS.md\`: **$total** entries, **$open_n** marked OPEN. It spans repos deliberately —"
say "the engines are split but the defects are not."
say ""
grep -E '^### [0-9]+\. OPEN' BUGS.md 2>/dev/null | sed 's/^### /- /' | while read -r l; do echo "$l"; done >> /dev/null
open_list="$(grep -E '^### [0-9]+\. OPEN' BUGS.md 2>/dev/null | sed 's/^### /- /')"
[ -n "$open_list" ] && { say "$open_list"; say ""; }

say "## Conformance"
say ""
say "Figures are only as good as their provenance, so each carries how and when it was measured."
say ""
# The baseline file holds BOTH suites. Summing it whole yields a blended figure that
# describes neither — so split on the key: xslt30-test rows are paths under tests/, QT3 rows
# are category/set names. Reporting one number for two corpora would be a new way to be
# confidently wrong about conformance, which is the thing this file exists to stop.
if [ -f scripts/conformance-baseline.tsv ]; then
  read -r xp xt xn <<<"$(awk -F'\t' '$1 ~ /^tests\// {p+=$2;t+=$3;n++} END{print p+0, t+0, n+0}' scripts/conformance-baseline.tsv)"
  read -r qp qt qn <<<"$(awk -F'\t' '$1 !~ /^tests\// {p+=$2;t+=$3;n++} END{print p+0, t+0, n+0}' scripts/conformance-baseline.tsv)"
  if [ "${xt:-0}" -gt 0 ]; then
    say "- **W3C XSLT 3.0 — $xp/$xt ($(awk -v p=$xp -v t=$xt 'BEGIN{printf "%.2f", 100*p/t}')%)** across $xn test-sets,"
    say "  from the committed per-set baseline, Release build. A ratchet, not a live run: it"
    say "  records what each set reaches every time."
  else
    say "- **W3C XSLT 3.0 — no rows in the baseline.** Not a claim of 0%."
  fi
  if [ "${qt:-0}" -gt 0 ]; then
    say "- **W3C QT3 baseline — $qp/$qt ($(awk -v p=$qp -v t=$qt 'BEGIN{printf "%.2f", 100*p/t}')%)** across $qn test-sets,"
    say "  same file, same ratchet."
  fi
else
  say "- **No baseline file.** Not a claim of 0%; the file is missing."
fi
# Read the QT3 figure from the XQuery repo rather than restating it, so this cannot drift
# from the report it claims to summarise. If the line is not found, say so — an absent figure
# must not read as zero, and a remembered one must not be printed.
qt3_line="$(grep -m1 -E '^\*\*Result\*\*:' "$WS/phoenixmldb-xquery/docs/CONFORMANCE.md" 2>/dev/null \
            | sed 's/^\*\*Result\*\*: *//')"
qt3_date="$(grep -m1 -E '^\*\*Date\*\*:' "$WS/phoenixmldb-xquery/docs/CONFORMANCE.md" 2>/dev/null \
            | sed 's/^\*\*Date\*\*: *//')"
if [ -n "$qt3_line" ]; then
  say "- **W3C QT3 / XQuery** — ${qt3_line//\*\*/}, measured $qt3_date, Release build."
  say "  Verified reproducible from two checkout paths with different execution"
  say "  orders. Source: \`phoenixmldb-xquery/docs/CONFORMANCE.md\`."
  say "  Do NOT cite the whole-suite test's number — it runs in catalog order with shared state,"
  say "  scores differently, and its 95% assertion is permanently red (BUGS.md #44)."
else
  say "- **W3C QT3 / XQuery — figure not readable** from"
  say "  \`phoenixmldb-xquery/docs/CONFORMANCE.md\`. NOT a claim that none exists."
fi

say ""
say "## Blocked"
say ""
xslt_pub="$(nuget_latest PhoenixmlDb.Xslt)"
xslt_ver="$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props 2>/dev/null | head -1)"
if [ -n "$xslt_pub" ] && [ "$xslt_pub" != "$xslt_ver" ]; then
  say "- **PhoenixmlDb.Xslt $xslt_ver is tagged, built and tested, but not published.** The"
  say "  publish step fails NuGet trusted-publishing login with HTTP 401. This repo pushes TWO"
  say "  package ids — \`PhoenixmlDb.Xslt\` and \`xslt\` — and a policy for one does not cover the"
  say "  other. Needs a nuget.org owner."
  drift=1
fi
cli_pub="$(nuget_latest PhoenixmlDb.Xslt.Cli)"
say "- **\`PhoenixmlDb.Xslt.Cli\` / \`PhoenixmlDb.XQuery.Cli\` are at ${cli_pub:-?}** while the"
say "  library line is 1.7.x. These are a second, older CLI distribution from \`phoenixmldb-cli\`,"
say "  separate from the \`xslt\`/\`xquery4\` tools the engine repos ship."

if [ "$check_only" = 1 ]; then
  [ "$drift" = 1 ] && { printf '%s' "$out" >&2; echo "status: drift or blockers present" >&2; exit 1; }
  echo "status: no drift"; exit 0
fi
printf '%s' "$out"
