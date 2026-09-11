# Status

Generated 2026-09-11 05:01 UTC by `scripts/status.sh`. Do not edit by hand.

## Packages

| package | published | repo version | state |
|---|---|---|---|
| `PhoenixmlDb.Core` | 1.7.0 | 1.7.0 | current |
| `PhoenixmlDb.XQuery` | 1.7.0 | 1.7.0 | current |
| `PhoenixmlDb.Xslt` | 1.6.15 | 1.7.0 | **repo is at 1.7.0, nuget.org has 1.6.15** |
| `xquery4` | 1.7.0 | 1.7.0 | current |
| `xslt` | 1.6.15 | 1.7.0 | **repo is at 1.7.0, nuget.org has 1.6.15** |

## Open work

| repo | open issues | open PRs |
|---|---|---|
| phoenixmldb-core | 2 | 0 |
| phoenixmldb-xquery | 5 | 3 |
| phoenixmldb-xslt | 7 | 1 |
| phoenixmldb-cli | 0 | 1 |

### Open issues

**phoenixmldb-core**

- [4] StringValue returns "" for nodes read from storage — string comparisons in cross-document queries silently match nothing
- [3] Parse_ElementWithManyChildren_ScalesLinearly is a wall-clock assertion and fails under parallel load

**phoenixmldb-xquery**

- [8] Cancellation not observed in recursion, user callbacks, or a slow every — a caller's timeout cannot stop the query
- [7] Map keys the comparer calls equal can hash apart; missed numeric lookups are O(n)
- [6] map:put and map:remove copy the whole map — incremental map building is O(n²)
- [5] Atomizing fn:collection() nodes yields '' while fn:string() on the same nodes returns the text
- [4] fn:sum returns 0 for xs:integer cast from text (BigInteger falls through SumHelper)

**phoenixmldb-xslt**

- [13] Backlog triage: 548 failing W3C cases, 217 of them wrong error codes
- [12] A node passed to SetParameter is not usable as a node in the stylesheet
- [11] xsl:for-each over a range crashes with InvalidCastException when an operand is a cast xs:integer
- [10] Chained predicates in a match pattern make the whole transform quadratic (169x)
- [9] XTDE1260 not raised for an unknown key name (key-080, error-1260e/f) — since 1.6.10, unfixed at 1.6.15
- [8] position-2201: last() on a virtual copy loses content, holding fn/position one below floor
- [7] 16 xslt30-test cases regress between 1.6.10 and 1.6.15 (try, analyze-string, match, base-uri)

### Open PRs

**phoenixmldb-xquery**

- [11] fix: a caller's timeout could not stop recursion, callbacks, or a slow every
- [10] fix: map keys the comparer calls equal could hash apart; missed numeric lookups were O(n)
- [9] fix: map:put/remove/replace copied the whole map — O(n) per update, O(n²) per loop

**phoenixmldb-xslt**

- [14] test: give each QT3 test set a fresh runner — per-set results depended on run order

**phoenixmldb-cli**

- [3] build: Bump the phoenixmldb-engines group with 2 updates

## Defect register

`BUGS.md`: **44** entries, **4** marked OPEN. It spans repos deliberately —
the engines are split but the defects are not.

- 34. OPEN — the 1,001 XQTS cases the fail-open was hiding, clustered
- 38. OPEN — where the remaining XQTS failures actually are
- 41. OPEN — eight catalog environment attributes the XSLT runner never reads (2026-09-10)
- 44. OPEN — the harness ledger, and why these keep happening (2026-09-10)

## Conformance

Figures are only as good as their provenance, so each carries how and when it was measured.

- **W3C XSLT 3.0 — 10082/10630 (94.84%)** across 221 test-sets, from the committed per-set
  baseline (`scripts/conformance-baseline.tsv`), Release build. This is a ratchet, not a
  live run: it records what each set reaches every time.
- **W3C QT3 / XQuery — no published figure.** `phoenixmldb-xquery/docs/CONFORMANCE.md` is
  marked SUPERSEDED: it measured 26,730 cases against today's 31,414 and predates the
  fail-open harness audit. Per-set results were order-dependent until the fresh-runner fix
  (BUGS.md #44). No honest number exists yet, and none is invented here.

## Blocked

- **PhoenixmlDb.Xslt 1.7.0 is tagged, built and tested, but not published.** The
  publish step fails NuGet trusted-publishing login with HTTP 401. This repo pushes TWO
  package ids — `PhoenixmlDb.Xslt` and `xslt` — and a policy for one does not cover the
  other. Needs a nuget.org owner.
- **`PhoenixmlDb.Xslt.Cli` / `PhoenixmlDb.XQuery.Cli` are at 1.4.10** while the
  library line is 1.7.x. These are a second, older CLI distribution from `phoenixmldb-cli`,
  separate from the `xslt`/`xquery4` tools the engine repos ship.
