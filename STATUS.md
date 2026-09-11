# Status

Generated 2026-09-11 05:41 UTC by `scripts/status.sh`. Do not edit by hand.

## Packages

| package | published | repo version | state |
|---|---|---|---|
| `PhoenixmlDb.Core` | 1.7.0 | 1.7.0 | current |
| `PhoenixmlDb.XQuery` | 1.7.0 | 1.7.0 | current |
| `PhoenixmlDb.Xslt` | 1.7.0 | 1.7.0 | current |
| `xquery4` | 1.7.0 | 1.7.0 | current |
| `xslt` | 1.7.0 | 1.7.0 | current |

## Open work

| repo | open issues | open PRs |
|---|---|---|
| phoenixmldb-core | 2 | 0 |
| phoenixmldb-xquery | 2 | 0 |
| phoenixmldb-xslt | 7 | 0 |
| phoenixmldb-cli | 0 | 1 |

### Open issues

**phoenixmldb-core**

- [4] StringValue returns "" for nodes read from storage — string comparisons in cross-document queries silently match nothing
- [3] Parse_ElementWithManyChildren_ScalesLinearly is a wall-clock assertion and fails under parallel load

**phoenixmldb-xquery**

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

**phoenixmldb-cli**

- [3] build: Bump the phoenixmldb-engines group with 2 updates

## Defect register

`BUGS.md`: **46** entries, **5** marked OPEN. It spans repos deliberately —
the engines are split but the defects are not.

- 34. OPEN — the 1,001 XQTS cases the fail-open was hiding, clustered
- 38. OPEN — where the remaining XQTS failures actually are
- 41. OPEN — eight catalog environment attributes the XSLT runner never reads (2026-09-10)
- 44. OPEN — the harness ledger, and why these keep happening (2026-09-10)
- 46. OPEN — tests that measure the machine, not the engine (2026-09-11)

## Conformance

Figures are only as good as their provenance, so each carries how and when it was measured.

- **W3C XSLT 3.0 — 10082/10630 (94.84%)** across 221 test-sets, from the committed per-set
  baseline (`scripts/conformance-baseline.tsv`), Release build. This is a ratchet, not a
  live run: it records what each set reaches every time.
- **W3C QT3 / XQuery** — 93.94% — 29,509 / 31,414 across all 428 catalog test-sets, measured 2026-09-11, Release build.
  Verified reproducible from two checkout paths with different execution
  orders. Source: `phoenixmldb-xquery/docs/CONFORMANCE.md`.
  Do NOT cite the whole-suite test's number — it runs in catalog order with shared state,
  scores differently, and its 95% assertion is permanently red (BUGS.md #44).

## Blocked

- **`PhoenixmlDb.Xslt.Cli` / `PhoenixmlDb.XQuery.Cli` are at 1.4.10** while the
  library line is 1.7.x. These are a second, older CLI distribution from `phoenixmldb-cli`,
  separate from the `xslt`/`xquery4` tools the engine repos ship.
