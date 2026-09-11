# 1.8.0 — draft release notes

**STATUS: DRAFT. Nothing is tagged and nothing is published.** Awaiting Lucas's decision on
whether to cut, and on the version number. Figures marked *provisional* move when the final
baseline raise lands.

## Why this release exists

Not the conformance numbers. **Two classes of silently wrong output, both present in every
version this project has ever published**, 1.1.0 through 1.7.0 — all 47 tags.

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
invalid calls returned a value instead of raising:

| call | before | after |
|---|---|---|
| `substring('abc', ())` | `""` | `XPTY0004` |
| `substring('abc', (1,2))` | `"abc"` | `XPTY0004` |
| `round-half-to-even(1.5, ())` | `2` | `XPTY0004` |

This is spec-correct — user-defined functions already raised `XPTY0004` — and it is the kind of
change a consumer notices. A stylesheet or query relying on the lenient behaviour will now fail
where it previously produced a value. **That behaviour change is itself the argument for a minor
version rather than a patch.**

Enabling the check required correcting our own signatures first: 9 XSLT built-in declarations
and 40+ XQuery ones were stricter than F&O 3.1 / XSLT 3.0 — `document()`'s `$uri-sequence`
declared `xs:string?` where the spec says `item()*`, `format-number()`'s `$value`, all 48 `xs`
constructors, the date/duration extractors, the math functions. A blanket check before that
audit measured −113 QT3 cases, every one of them our own mis-declaration rather than a spec
disagreement.

## Conformance

*Provisional — final figures come from the baseline raise covering #24 and #25.*

| suite | 1.7.0 | 1.8.0 |
|---|---|---|
| W3C XSLT 3.0 | 10,082/10,630 (94.84%) | ~10,163/10,630 (~95.6%) |
| W3C QT3 | no reproducible figure | ~29,534/31,414 (~94.0%) |

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
