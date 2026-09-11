# Status

Generated 2026-09-11 11:28 UTC by `scripts/status.sh`. Do not edit by hand.

## Packages

| package | published | repo version | state |
|---|---|---|---|
| `PhoenixmlDb.Core` | 1.7.0 | ? | **repo is at ?, nuget.org has 1.7.0** |
| `PhoenixmlDb.XQuery` | 1.7.0 | ? | **repo is at ?, nuget.org has 1.7.0** |
| `PhoenixmlDb.Xslt` | 1.7.0 | 1.7.0 | current |
| `xquery4` | 1.7.0 | ? | **repo is at ?, nuget.org has 1.7.0** |
| `xslt` | 1.7.0 | 1.7.0 | current |

## Open work

| repo | open issues | open PRs |
|---|---|---|
| phoenixmldb-core | 3 | 0 |
| phoenixmldb-xquery | 3 | 0 |
| phoenixmldb-xslt | 7 | 2 |
| phoenixmldb-cli | 0 | 1 |

### Open issues

**phoenixmldb-core**

- [5] Naming decision: the prefix `dbxml` means three different things
- [4] StringValue returns "" for nodes read from storage — string comparisons in cross-document queries silently match nothing
- [3] Parse_ElementWithManyChildren_ScalesLinearly is a wall-clock assertion and fails under parallel load

**phoenixmldb-xquery**

- [18] Unbound external functions silently return () — and a host cannot bind one
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

**phoenixmldb-xslt**

- [23] test: raise the baseline for #20 and #21 — XSLT 10,160
- [22] fix: XSLT built-ins declare their spec parameter cardinalities

**phoenixmldb-cli**

- [3] build: Bump the phoenixmldb-engines group with 2 updates

## Defect register

`BUGS.md`: **52** entries, **7** marked OPEN. It spans repos deliberately —
the engines are split but the defects are not.

- 34. OPEN — the 1,001 XQTS cases the fail-open was hiding, clustered
- 38. OPEN — where the remaining XQTS failures actually are
- 41. OPEN — eight catalog environment attributes the XSLT runner never reads (2026-09-10)
- 44. OPEN — the harness ledger, and why these keep happening (2026-09-10)
- 46. OPEN — tests that measure the machine, not the engine (2026-09-11)
- 47. OPEN — static shadow-attribute evaluation silently DROPS what it cannot compute (2026-09-11)
- 52. OPEN — accumulators read a later-declared accumulator's value one node late (2026-09-11)

## Conformance

Figures are only as good as their provenance, so each carries how and when it was measured.

- **W3C XSLT 3.0 — 10157/10630 (95.55%)** across 221 test-sets,
  from the committed per-set baseline, Release build. A ratchet, not a live run: it
  records what each set reaches every time.
- **W3C QT3 baseline — 29524/31414 (93.98%)** across 428 test-sets,
  same file, same ratchet.
- **W3C QT3 / XQuery — figure not readable** from
  `phoenixmldb-xquery/docs/CONFORMANCE.md`. NOT a claim that none exists.

## Blocked

- **`PhoenixmlDb.Xslt.Cli` / `PhoenixmlDb.XQuery.Cli` are at 1.4.10** while the
  library line is 1.7.x. These are a second, older CLI distribution from `phoenixmldb-cli`,
  separate from the `xslt`/`xquery4` tools the engine repos ship.
