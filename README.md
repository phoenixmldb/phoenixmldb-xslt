# PhoenixmlDb XSLT

A modern XSLT 4.0 transformation engine for .NET with streaming and package support.

## Features

### XSLT 4.0
- **xsl:switch** — conditional processing with select context
- **xsl:for-each-member** — array member iteration
- **xsl:item-type** — named type declarations
- **xsl:record** — record construction
- **method="csv"** — CSV serialization output

### XSLT 3.0 (96.6% W3C conformance — 10,310/10,672 cases, measured 2026-09-14)
- Full template matching with priorities and modes
- xsl:iterate, xsl:try/catch, xsl:evaluate
- xsl:use-package with override, xsl:original, visibility
- xsl:expose, xsl:accept with hidden visibility
- Streaming (xsl:source-document, xsl:mode streamable, xsl:fork, accumulators)
- Higher-order functions, maps, arrays
- Accumulators, merge, JSON/adaptive output

## Conformance

Every figure below is measured, dated, and reproducible. Nothing here is an estimate.

### W3C XSLT 3.0 — 10,310/10,672 cases (96.6%), 362 failing

Measured 2026-09-14 against `w3c/xslt30-test` @ `fddf1cf`, in a **Release** build, from the
committed per-set baseline (`scripts/conformance-baseline.tsv`).

**The denominator grew, and that is the point.** It was 10,630 because the harness never opened
`decl/expose` — 42 cases of an entire feature area, scoring no information at all rather than
scoring zero. Wiring it in adds 42 cases of which 9 pass, so it **lowers the percentage** while
the engine has only improved. A number that went up because a failing area stayed invisible is
worse than a smaller number that counts it, so the smaller honest one is published here. See
`BUGS.md` #72.

**Which XQuery this is measured against, because it changes the number.** The conformance
project reaches XQuery through the `src/PhoenixmlDb.XQuery` symlink, so a local run measures
whatever that working tree is checked out at. The figure above is against `phoenixmldb-xquery`
**main** at 1.8.0, which is the intended basis.

**A worked example of why the basis is stated and not assumed.** That checkout was found sitting
detached at the v1.7.0 release commit, 27 behind main, left over from an A/B three days earlier.
Measured there, the suite reported **21 test-sets below baseline, 34 cases** — which reads
exactly like a regression, and QT3 alone accounted for 28 of it. Restoring the checkout to main
cleared 18 of the 21 sets and returned QT3 to 29,534 on the nose. Same engine throughout; only
the dependency source had moved. A per-set gate cannot distinguish "the code got worse" from
"a sibling checkout moved", so the environment has to be part of the reading.

The baseline records the **minimum across full runs, not the best one seen**. Repeated sweeps
agree on every set but one: `insn/call-template` alternates between 38 and 37, and the baseline
holds 37. A baseline set to the maximum observed enshrines a lucky run and then reports a
regression every time the suite behaves normally.

| Group | Passing | | Failing |
|---|---|---|---|
| `attr` — attributes | 1084/1117 | 97.0% | 33 |
| `decl` — declarations | 1017/1122 | 90.6% | 105 |
| `type` — types | 753/766 | 98.3% | 13 |
| `fn` — functions | 1082/1131 | 95.7% | 49 |
| `strm` — streaming | 2309/2373 | 97.3% | 64 |
| `expr` — expressions | 637/648 | 98.3% | 11 |
| `misc` | 1884/1921 | 98.1% | 37 |
| `insn` — instructions | 1544/1594 | 96.9% | 50 |
| **Total** | **10,310/10,672** | **96.6%** | **362** |

`decl` is the only group whose denominator changed: 1080 to 1122, the 42 newly-opened
`decl/expose` cases.

The `sandp` group runs but reports no per-case counts, so it is excluded from the total rather
than counted as passing.

**A note on how these figures are measured, because one of them was wrong for months.**

Conformance is measured in a **Release** build. It used to be measured in Debug, which lost 38
cases across 21 sets purely to the harness's 10s per-case timeout — 43 timeouts against Release's
5. Release is what ships, so a figure taken from a Debug build was not a claim about the product.
Measured both ways on the same commit, same machine:

| | cases | rate | timeouts |
|---|---|---|---|
| Debug | 10,044/10,630 | 94.49% | 43 |
| Release | 10,082/10,630 | 94.84% | 5 |

21 sets better under Release, **zero worse**. `misc/bug-3701` is the clean illustration: ~10.5s in
Debug against a 10s cap, ~5.7s in Release. `scripts/conformance.sh` now defaults to Release.

That correction is why the published figure rose from 94.4% to 94.8% on 2026-09-10 without the
engine changing. The rise since then — to 95.6% — *is* engine work. See `BUGS.md` #43.

**This number went DOWN from the 96.2% published on 2026-09-02, and the engine did not get
worse — the measurement got honest.** Tests that expect a specific error code were scored as
passes whenever the transform threw *anything at all*: the corpus writes the expected code as an
attribute (`<error code="XTSE0010"/>`) and both conformance runners read it from the element's
text content, so the comparison was always against an empty string, which matches everything.
`fn:load-xquery-module` scored 4/4 on the strength of throwing four times. Full write-up in
[BUGS.md](BUGS.md) entry 28.

Checking the code properly cost 4.6 points; reading it from the right place gave most of that
back. Many errors carry their code in a structured `ErrorCode` property rather than in the
message text, so a message-only comparison under-credited correct behaviour just as badly as it
over-credited wrong behaviour. Both runners now check message text and property, down the whole
inner-exception chain.

**222 of the 610 remaining failures are "the engine raised an error, but not the expected
code."** Those are real failures — the codes are normative — but they are a different and
generally shallower defect than a wrong result or a missed error, so runs report the split:

```
Results: 27/50 passed (54.0%) — 23 of 23 failures raised an error with the wrong code
```

Every XSLT conformance figure this project published before 2026-09-04 was overstated. The net
correction is 1.9 points, and 204 failures that were previously invisible.

### XSpec — 139/284 suites (49%), 1152/1364 assertions (84.5%)

Measured 2026-09-02 against the [XSpec](https://github.com/xspec/xspec) test corpus. This is the
weakest of our conformance numbers and is published for the same reason as the strongest one.

**Stale, and understating.** It predates a week of engine work — three `xsl:try` defects, the
`analyze-string` dot-all flag, a copied element losing its text, seven wrong error-code sites,
package-version validation, and the XQuery-side `fn:sum`, atomization and `map:put` fixes. The
real figure is very likely higher. It is left as measured rather than estimated upward, and
re-measuring it is open work.

| | |
|---|---|
| Suites running to completion | 139 of 284 |
| — of the 162 the runner can drive | 139 (122 are XQuery or Schematron suites it does not) |
| Assertions passing | 1152 of 1364 |
| Assertions failing | 209 |

Roughly half the corpus completes, and about one assertion in seven still fails. Open causes are
tracked in [BUGS.md](BUGS.md).

### Reproducing these numbers

```bash
./scripts/fetch-conformance-suites.sh   # clones xslt30-test + qt3tests into TestData/
./scripts/conformance.sh                # W3C XSLT groups; writes conformance-results/summary.txt
./scripts/conformance.sh --all          # adds the XQuery (QT3) suite, as CI runs it
phxspec --census $(find test -maxdepth 1 -name '*.xspec' | sort)   # from an xspec checkout
```

A conformance run with `TestData/` absent reports success without executing anything, so confirm
the corpora are present before believing a green result.

## Installation

```bash
dotnet add package PhoenixmlDb.Xslt
```

### Command-Line Tool

A standalone `xslt` CLI tool is also available as a .NET global tool:

```bash
dotnet tool install -g xslt
```

```bash
# Transform XML with a stylesheet
xslt -s:stylesheet.xsl input.xml

# Transform with parameters
xslt -s:stylesheet.xsl -p:name=value input.xml

# Call a named template (no source document)
xslt -s:stylesheet.xsl -it:main

# Show timing breakdown
xslt --timing -s:stylesheet.xsl input.xml
```

Run `xslt --help` for the full list of options.

## Quick Start

```csharp
var transformer = new XsltTransformer();
await transformer.LoadStylesheetAsync(xsltString);
var result = await transformer.TransformAsync(xmlInput);
```

## API Overview

### Source Document
- `TransformAsync(string? inputXml)` — pass source XML as string, or `null` for call-template/call-function
- `TransformAsync(TextReader inputXml)` — read source from a TextReader (for large documents)
- `TransformAsync(Stream inputXml)` — read source from a Stream
- `TransformAsync(string? inputXml, TextWriter output)` — write result directly to a TextWriter
- `TransformAsync(TextReader inputXml, TextWriter output)` — full stream-to-stream pipeline
- `TransformAsync(Stream inputXml, Stream output)` — full stream-to-stream pipeline
- `ResultDocumentHandler` — callback to provide TextWriters for xsl:result-document outputs
- `SetSourceDocumentUri(Uri)` — set base-uri/document-uri metadata on the source document
- `SetSourceSelect(string xpath)` — select initial context node (default: document root)
- `SetInitialModeSelect(string xpath)` — apply templates to a computed node selection

### Parameters
- `SetParameter(string name, string value)` — string parameter (xs:untypedAtomic)
- `SetParameter(string name, object? value)` — typed parameter (int, long, double, bool, decimal)
- `SetInitialTemplateParameter(QName, object?)` — xsl:with-param for named templates
- `SetInitialTunnelParameter(QName, object?)` — tunnel parameter

### Invocation Styles
- **Apply templates** (default) — optionally set mode with `SetInitialMode(string)`
- **Call template** — `SetInitialTemplate(string)`, pass `null` to TransformAsync
- **Call function** — `SetInitialFunction(string)` + `AddInitialFunctionArgument(object?)`

### Collections
- `SetCollection(string uri, List<string> paths)` — register documents for `fn:collection()`

### Debugging
- `TraceListener` — callback for template-match, function-call, built-in-rule events

## License

Apache 2.0 — see [LICENSE](LICENSE)

## Related Projects

- [phoenixmldb-core](https://github.com/phoenixmldb/phoenixmldb-core) — Core types and XDM
- [phoenixmldb-xquery](https://github.com/phoenixmldb/phoenixmldb-xquery) — XPath/XQuery 4.0 engine
- [phoenixmldb-cli](https://github.com/phoenixmldb/phoenixmldb-cli) — CLI tools
