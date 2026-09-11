# 1.8.0 — draft release notes

**STATUS: HELD 2026-09-11 by Lucas.** Nothing is tagged and nothing is published.

**This release is deliberately not being cut yet.** Lockstep goes in first — one version number
across the engines, the CLIs, XSpec and the MCP servers — and 1.8.0 then becomes the **first
lockstep train** rather than the last release under the old process. See
`RELEASE-POLICY-PROPOSAL.md`.

The content below is complete and stays accurate; what changes at release is the scope, which
grows to include `xquery-mcp` and `xslt-mcp`, and the conformance figures, which should be
measured in the **release configuration** once the Xslt train pins the XQuery of the same train
(see `BUGS.md` #57 — today's figures are xquery-main numbers).

**Do not cut from this file alone.** The lockstep ordering and its pack-time checks come first.

## Why this release exists

Not the conformance numbers. **Four classes of silently wrong output**, three of them present in
every version this project has ever published.

### A streaming aggregate answered from nothing (BUGS.md #63)

**Read this one first — it needs no opt-in and no unusual stylesheet.** Under a declared
streamable mode, a `match="/"` template whose body aggregates over the stream computed its answer
from **no input at all**:

| expression | returned |
|---|---|
| `count(//PRICE)` | `0` |
| `sum(//PRICE)` | *empty* |
| `count(//*)` | `0` |

Whatever the document held. No error — and `0` is exactly what a reader expects from a document
with no matches, so nothing in the output says it never looked.

The body was classified *literal-only* (it registers no `for-each` subscriptions) and literal-only
bodies run **before** the streaming pass, so it evaluated against watchers that had seen nothing.

**This is the default CLI path.** `xslt` auto-streams a file whenever the stylesheet declares a
streamable mode, and `--no-stream` gave the same wrong answers. The W3C streaming tests could not
catch it because every one of them aggregates inside `xsl:source-document`, whose path drains the
stream first — full coverage of the feature, none of the way users reach it.

### `xsl:text` moved to the end of any untyped variable (BUGS.md, xslt #34)

```xml
<xsl:variable>a<b>c</b><xsl:text>d</xsl:text>e</xsl:variable>
```

read as `aced` and copied as `a<b>c</b>ed` — the `d` relocated past the `e`. Any untyped variable
mixing `xsl:text` with other content was affected. Predates 1.7.0.

### And two that are in every version ever published — 1.1.0 through 1.7.0, all 47 tags

### `xsl:function cache="yes"` returned other calls' results (BUGS.md #51)

The memoization key was built by string concatenation, so calls with *different* arguments
shared a cache entry:

- `f((1,2))` and `f((3,4))` — both keyed as `System.Object[]`
- `f('1')` and `f(1)` — both keyed as `1`
- `map{1:'x'}` and `map{'1':'x'}` — map keys rendered the same way
- commas collided across argument boundaries

No error was raised. The function returned a well-formed answer computed for someone else's
arguments. The key is now a structural identity.

**Opt-in**: a stylesheet that never sets `cache="yes"` was unaffected. Our own documentation
recommended the attribute, which is corrected separately in `phoenixml-docs` PR #7.

### Accumulators read a later-declared accumulator one node late (BUGS.md #52)

End-phase rules ran in **declaration order**, so an accumulator reading another accumulator's
after-value at the same node got the *previous node's value* whenever the other was declared
later. Declaration order — which an author has no reason to think significant — decided whether
the result was correct.

**Not opt-in.** Accumulators are ordinary XSLT 3.0 and nothing marked the hazard.

The same loop also failed to detect a **two-accumulator cycle** (`a` reads `after(b)`, `b` reads
`after(a)`): it resolved to stale data instead of raising. That is now `XTDE3400`.

Both halves had one cause — reading a provisional entry was indistinguishable from reading a
settled one. Accumulator values are now evaluated on demand.

## Behaviour change — read this before upgrading

**Built-in functions now enforce declared parameter cardinality.** Previously they did not, so
invalid calls returned a value instead of raising.

This applies to **every built-in**, not only the string functions the examples happen to show.
Any built-in call passing an empty or multi-item argument where the spec requires exactly one
now raises `XPTY0004`, carrying the call-site location. Examples, not an exhaustive list:

| call | before | after |
|---|---|---|
| `substring('abc', ())` | `""` | `XPTY0004` |
| `substring('abc', (1,2))` | `"abc"` | `XPTY0004` |
| `round-half-to-even(1.5, ())` | `2` | `XPTY0004` |

This is spec-correct — user-defined functions already raised `XPTY0004` — and it is the kind of
change a consumer notices. A stylesheet or query relying on the lenient behaviour will now fail
where it previously produced a value. **That behaviour change is itself the argument for a minor
version rather than a patch.**

Enabling the check required correcting our own signatures first, because many were stricter
than F&O 3.1 / XSLT 3.0 demand:

- **XQuery — 86 signatures**: 38 individually declared parameters, plus all 48 `xs:`
  constructors through one shared base (`TypeConstructorFunction`). The 38 are 18 date/time/
  duration component extractors, 12 math (11 one-argument functions plus `math:pow`'s `$x`),
  `format-number#2`/`#3`, `json-to-xml#1`/`#2` and `parse-json#1`/`#2`, `round#2`'s `$arg`, and
  `sum#2`'s `$zero`.
- **XSLT — 9 parameters across 9 signatures**, including `document()`'s `$uri-sequence`
  declared `xs:string?` where the spec says `item()*`, and `format-number()`'s `$value`.

A blanket check *before* that audit measured −113 QT3 cases — every one of them our own
mis-declaration rather than a spec disagreement, which is why the declarations were corrected
first and the check enabled second.

## Conformance

Backed by the committed per-set baseline as of `0a48c76`.

| suite | 1.7.0 | 1.8.0 |
|---|---|---|
| W3C XSLT 3.0 | 10,082/10,630 (94.84%) | **10,163/10,630 (95.61%)** |
| W3C QT3 | no reproducible figure | **29,534/31,414 (94.02%)** |

The QT3 line is not a regression from the 99.72% that stood in `CONFORMANCE.md` until
2026-09-11 — that figure measured a smaller corpus with a runner that scored an expected-error
test as passing whenever the query threw anything at all. This is the first reproducible number.

## Release sequencing — enforced, not remembered

`PhoenixmlDb.Xslt` consumes `PhoenixmlDb.XQuery` as a package, so the XQuery fixes reach XSLT
users only when the pin moves. Order:

1. **XQuery 1.8.0** published to nuget.org
2. **XSLT's `Directory.Packages.props` pin** bumped to 1.8.0
3. **XSLT 1.8.0** tagged

Getting this wrong is what produced the known-bad **Xslt 1.6.13**, which shipped depending on
XQuery 1.6.12 because the tag was cut before the XQuery push — the sole Tier 1 unlisting
candidate in `RELEASE-HYGIENE.md`.

It cannot happen silently now: `scripts/check-release-train.sh` runs on tag push before Pack and
fails the release if any train-locked `PhoenixmlDb.*` pin does not equal the release version. It
also **fails closed** — no train-locked pins found is an error, not a pass.

## Not in this release

- The `dbxml` prefix conflict (`phoenixmldb-core` #5) — a user-facing naming decision, unresolved.
- The `NamespaceId` 10 collision between XQuery's full-text id and Core's XSLT id. Latent; needs
  a Core change, and Core is unchanged at 1.7.0.
- Core is **not** being rebuilt for this release. It stays at 1.7.0.
