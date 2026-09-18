# Status

Generated 2026-09-18 20:57 UTC by `scripts/status.sh`. Do not edit by hand.

## Packages

| package | published | repo version | state |
|---|---|---|---|
| `PhoenixmlDb.Core` | 2.0.0 | ? | **repo is at ?, nuget.org has 2.0.0** |
| `PhoenixmlDb.XQuery` | 2.1.0 | ? | **repo is at ?, nuget.org has 2.1.0** |
| `PhoenixmlDb.Xslt` | 2.1.0 | 2.1.0 | current |
| `xquery4` | 2.1.0 | ? | **repo is at ?, nuget.org has 2.1.0** |
| `xslt` | 2.1.0 | 2.1.0 | current |

## Open work

| repo | open issues | open PRs |
|---|---|---|
| phoenixmldb-core | 3 | 0 |
| phoenixmldb-xquery | 8 | 0 |
| phoenixmldb-xslt | 8 | 8 |
| phoenixmldb-cli | 0 | 1 |

### Open issues

**phoenixmldb-core**

- [5] Naming decision: the prefix `dbxml` means three different things
- [4] StringValue returns "" for nodes read from storage — string comparisons in cross-document queries silently match nothing
- [3] Parse_ElementWithManyChildren_ScalesLinearly is a wall-clock assertion and fails under parallel load

**phoenixmldb-xquery**

- [71] phx:score returns 0 for every node, including one contains text has just matched
- [70] phx:is-stop-word is false for every stop word and true for any letterless input
- [49] QT3 runner's assert-type passes any type it doesn't list — 17 engine type defects hidden, 11 correct results failed
- [40] XsdSchemaProvider.Validate validates an element's string value, not its markup
- [30] contains text: phrase matching ignores term positions, so a phrase matches across a stop-word gap
- [29] FullTextAnalysisOptions.Default performs no stemming despite Stemming=true
- [18] Unbound external functions silently return () — and a host cannot bind one
- [5] Atomizing fn:collection() nodes yields '' while fn:string() on the same nodes returns the text

**phoenixmldb-xslt**

- [156] fn/system-property-gen is 0/166 — compile-time XPath evaluation stops at inline function items, doc() and paths
- [148] A streamed xsl:message loses the items produced by xsl:for-each in its content
- [147] Streamable XSLT 3.0 with non motionless group-adjacent should be rejected by streamability analysis but is executed to give the strange error " XTDE1071: current-grouping-key() called when there is no current grouping key"
- [140] assert-xml comparisons are blind to whitespace-only element content (~20 assertions, 9 sets)
- [96] Xslt 1.8.0 regresses three streaming conformance sets that passed on 1.6.10 (attr/streamable below floor, stream-211, sx-gc-eq-801)
- [95] Any predicate in a match pattern is quadratic — not just chained ones (#10 is a special case)
- [13] Backlog triage: 548 failing W3C cases, 217 of them wrong error codes
- [7] 16 xslt30-test cases regress between 1.6.10 and 1.6.15 (try, analyze-string, match, base-uri)

### Open PRs

**phoenixmldb-xslt**

- [161] test: assert cancellation stopped the work, not that the machine was fast enough
- [160] BUGS: the cancellation test's host-speed assertion flipped direction
- [159] fix: the runner never set a base output URI — +5 W3C cases, 0 lost
- [158] fix: xsl:sort and xsl:key did not set temporary output state — +2 W3C cases, 0 lost
- [157] fix: xsl:expose selected nothing, silently — +13 W3C cases, 0 lost
- [155] fix: three error sites named a neighbouring code — +12 W3C cases, 0 lost
- [154] regex-group() returns the empty sequence inside a pattern
- [153] Opt decl/strip-space into whitespace-sensitive comparison

**phoenixmldb-cli**

- [6] build: Bump the phoenixmldb-engines group with 2 updates

## Defect register

`BUGS.md`: **109** entries, **23** marked OPEN. It spans repos deliberately —
the engines are split but the defects are not.

- 34. OPEN — the 1,001 XQTS cases the fail-open was hiding, clustered
- 38. OPEN — where the remaining XQTS failures actually are
- 41. OPEN — eight catalog environment attributes the XSLT runner never reads (2026-09-10)
- 44. OPEN — the harness ledger, and why these keep happening (2026-09-10)
- 46. OPEN — tests that measure the machine, not the engine (2026-09-11)
- 47. OPEN — static shadow-attribute evaluation silently DROPS what it cannot compute (2026-09-11)
- 52. OPEN — accumulators read a later-declared accumulator's value one node late (2026-09-11)
- 54. OPEN — the per-set gate cannot see a set that did not run (2026-09-11)
- 57. OPEN — our published conformance figures describe a build that does not ship (2026-09-11)
- 61. OPEN — an indirect global cycle reports XPST0008 where XTDE0640 is due (2026-09-11)
- 64. OPEN (XQuery-side) — the UCA collation ignores `alternate=shifted` (2026-09-11)
- 65. OPEN (XQuery-side, blocked) — two error-reporting defects found by the XSLT sweep (2026-09-11)
- 68. OPEN — the W3C corpus contradicts itself on streamable accumulator AVTs (2026-09-11)
- 70. OPEN — SENR0001 vs XTDE0450 needs a destination marker, not an error-code swap (2026-09-11)
- 71. OPEN — `streamable="true"` is silently not streamable; 164 corpus stylesheets have never streamed (2026-09-11, re-measured 2026-09-13)
- 77. OPEN (policy) — 919 `sandp` cases are dependency-skipped, and the streamability analyser has no corpus evidence at all (2026-09-12)
- 84. OPEN — a function-produced text node fails every axis step, and the error names an internal type (2026-09-13)
- 85. OPEN — under a streamable mode, non-streamable expressions answer instead of being rejected (2026-09-13)
- 90. OPEN — a second `xsl:mode` declaration silently discards the first's attributes (2026-09-13)
- 91. OPEN — built-in-rule descent to a matching template emits the element empty and leaks its text (2026-09-13)
- 92. OPEN — `has-children()` always answers false under streaming, and the cheap fix is wrong (2026-09-13)
- 93. OPEN — the buffered subtree has no ancestors, and the missing parent is load-bearing (2026-09-13)
- 105. OPEN — call-template-1003 gives three different verdicts on identical code (2026-09-16)

## Conformance

Figures are only as good as their provenance, so each carries how and when it was measured.

- **W3C XSLT 3.0 — 10335/10839 (95.35%)** across 221 test-sets,
  from the committed per-set baseline, Release build. A ratchet, not a live run: it
  records what each set reaches every time.
- **W3C QT3 baseline — 29532/31379 (94.11%)** across 428 test-sets,
  same file, same ratchet.
- **W3C QT3 / XQuery — figure not readable** from
  `phoenixmldb-xquery/docs/CONFORMANCE.md`. NOT a claim that none exists.

## Blocked

- **`PhoenixmlDb.Xslt.Cli` / `PhoenixmlDb.XQuery.Cli` are at 1.4.10** while the
  library line is 1.7.x. These are a second, older CLI distribution from `phoenixmldb-cli`,
  separate from the `xslt`/`xquery4` tools the engine repos ship.
