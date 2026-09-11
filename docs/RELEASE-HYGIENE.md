# Plan: unlisting the 1.6.x release churn

**Status: PLAN ONLY. Nothing has been unlisted.** Unlisting is an account action on nuget.org and
needs credentials I do not have. Every command below is written out so it can be run or handed to
CI once approved.

## The problem, in numbers

| package | 1.6.x versions | window | cadence |
|---|---|---|---|
| `PhoenixmlDb.Xslt` | **16** | 2026-07-31 → 09-09 | one every 2.5 days |
| `PhoenixmlDb.XQuery` | **14** | 2026-07-27 → 09-09 | one every 3.1 days |
| `PhoenixmlDb.Core` | 4 | | |
| `PhoenixmlDb.*.Cli` | **0** | — | **never shipped a 1.6.x at all** |

Totals across all lines: Xslt 127 published versions, XQuery 117.

Two structural causes, both now fixed but neither retroactive:

1. **`publish` was gated on `build`, not `test`.** Every package in the org could reach nuget.org
   with a red suite, and conformance never ran on the release path. Fixed today.
2. **No release train.** `PhoenixmlDb.Xslt 1.6.13` depends on `PhoenixmlDb.XQuery 1.6.12` because
   the XSLT tag was cut before the XQuery push — that is the artifact Martin Honnen was handed.
   `scripts/check-release-train.sh` now refuses to pack such a build.

## Principles

- **Unlisting does not break existing consumers.** An exact pinned version still restores. What
  unlisting removes is discovery: search, the version dropdown, and floating-version resolution.
  It is the right tool for "do not start here", not a recall.
- **Never unlist a version a known consumer pins**, even if superseded. Check first, every time.
- **Never leave a line with nothing listed.** At least the latest of each minor stays.
- **Unlist known-bad before merely-superseded.** Reputation damage comes from someone landing on
  a version that gives wrong answers, not from choice.

## Known consumer pins — these must stay listed

| consumer | pins | note |
|---|---|---|
| `phoenixml` (database) | XQuery **1.6.9**, Xslt **1.6.10** | documented hold; bumping at this cut |
| `phoenixml-platform` | Core **1.1.0** | badly drifted, still live |
| `phoenixmldb-cli` | **1.1.9** | drifted |

Confirm against each consumer's `Directory.Packages.props` immediately before executing, not from
this table — it will go stale.

## Tier 1 — unlist now (known-bad artifacts)

| package | version | why |
|---|---|---|
| `PhoenixmlDb.Xslt` | **1.6.13** | ships depending on XQuery 1.6.12, a train we know is mismatched. Not a judgement call: the dependency is wrong. |

That is the only version I can name as *known* wrong on its face rather than merely superseded.
Everything else in 1.6.x carries defects we have since fixed, but so does every version of most
software; that alone is not grounds.

### Unlisting is not a remedy for a defect, and #51 is the worked example

`cache="yes"` has returned other calls' results since `030bfe1`, the initial release commit —
**all 47 published tags, 1.1.0 through 1.7.0** (BUGS.md #51). Unlisting does nothing for it:
there is no good version to push a user onto, because the newest version has it too.

That is the general case, not a special one. Unlisting narrows *choice*; it does not fix
anything, and reaching for it as a response to a defect gets the tool wrong. The three remedies
that do work, in the order they apply:

1. **Say so where the user is** — the docs page recommending the feature is the one that
   reaches someone about to use it today (`phoenixml-docs` PR #7 does this for #51).
2. **Ship the fix forward.** For #51 that is 1.8.0; no earlier pin helps.
3. **Unlist** only to stop someone *newly* picking a version we know is wrong on its face —
   which is what Tier 1 is, and why it has exactly one entry.

## Tier 2 — unlist once 1.7.0 is published and the database has bumped

All 1.6.x **except** the latest and the pinned ones. Concretely:

    Xslt:    1.6.0 1.6.1 1.6.2 1.6.3 1.6.4 1.6.5 1.6.6 1.6.7 1.6.8 1.6.11 1.6.12 1.6.14
             KEEP 1.6.10 (database pin), 1.6.15 (line latest), 1.6.13 → Tier 1
    XQuery:  1.6.0 1.6.1 1.6.2 1.6.5 1.6.6 1.6.7 1.6.8 1.6.10 1.6.11 1.6.12 1.6.13 1.6.14
             KEEP 1.6.9 (database pin), 1.6.15 (line latest)

That removes 24 versions from discovery and leaves a consumer three sensible choices per package
instead of sixteen.

**Precondition:** 1.7.0 published AND the database repo has bumped off 1.6.9/1.6.10. Until then
unlisting their pins would be hostile even though restore still works.

## Tier 3 — older lines, lowest priority

1.0.x–1.5.x are 100+ versions between the two packages. Same rule: keep the latest of each minor,
unlist the rest. No urgency; nobody is landing on 1.2.7 by accident.

## How to execute

nuget.org's `delete` verb unlists rather than deletes:

```bash
dotnet nuget delete PhoenixmlDb.Xslt 1.6.13 \
  --source https://api.nuget.org/v3/index.json \
  --api-key "$NUGET_KEY" --non-interactive
```

Do them one at a time, and **verify after each**:

```bash
curl -s --compressed \
  https://api.nuget.org/v3/registration5-gz-semver2/phoenixmldb.xslt/index.json \
  | python3 -c "import json,sys; [print(i['catalogEntry']['version'], i['catalogEntry'].get('listed')) \
    for p in json.load(sys.stdin)['items'] for i in p['items']]"
```

Assert the count that remains listed, not just that the command exited 0 — a bulk unlist that
silently no-ops looks exactly like one that worked.

## What actually stops the next 40 versions

Unlisting is cleanup. These are the controls:

1. **`publish` gated on `test`** — done today, all four repos.
2. **`check-release-train.sh`** — pins must equal the release version at pack time. Already in CI.
3. **`check-pins.sh`** — refuses a pin behind what is published. Already in CI.
4. **A cadence rule, which does not exist yet and is the real gap.** Nothing prevents another
   16-in-40-days. Options worth deciding between: a minimum interval between releases of a line;
   batching fixes into a scheduled release unless the fix is a wrong-answer defect; or requiring
   a named consumer need for each patch release. This is a policy decision, not a script.
5. **Conformance on the release path.** It runs nightly and on tags in `conformance.yml`, but
   nothing blocks a publish on it.

Item 4 is the one that would have prevented this, and it is the one still open.
