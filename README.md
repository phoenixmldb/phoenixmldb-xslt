# PhoenixmlDb XSLT

A modern XSLT 4.0 transformation engine for .NET with streaming and package support.

## Features

### XSLT 4.0
- **xsl:switch** — conditional processing with select context
- **xsl:for-each-member** — array member iteration
- **xsl:item-type** — named type declarations
- **xsl:record** — record construction
- **method="csv"** — CSV serialization output

### XSLT 3.0 (94.3% W3C conformance — 10,020/10,630 cases, measured 2026-09-04)
- Full template matching with priorities and modes
- xsl:iterate, xsl:try/catch, xsl:evaluate
- xsl:use-package with override, xsl:original, visibility
- xsl:expose, xsl:accept with hidden visibility
- Streaming (xsl:source-document, xsl:mode streamable, xsl:fork, accumulators)
- Higher-order functions, maps, arrays
- Accumulators, merge, JSON/adaptive output

## Conformance

Every figure below is measured, dated, and reproducible. Nothing here is an estimate.

### W3C XSLT 3.0 — 10,020/10,630 cases (94.3%), 610 failing

Measured 2026-09-04 against `w3c/xslt30-test` @ `fddf1cf`.

| Group | Passing | | Failing |
|---|---|---|---|
| `attr` — attributes | 1064/1117 | 95.3% | 53 |
| `decl` — declarations | 944/1080 | 87.4% | 136 |
| `type` — types | 750/766 | 97.9% | 16 |
| `fn` — functions | 1068/1131 | 94.4% | 63 |
| `strm` — streaming | 2248/2373 | 94.7% | 125 |
| `expr` — expressions | 634/648 | 97.8% | 14 |
| `misc` | 1815/1921 | 94.5% | 106 |
| `insn` — instructions | 1497/1594 | 93.9% | 97 |
| **Total** | **10,020/10,630** | **94.3%** | **610** |

The `sandp` group runs but reports no per-case counts, so it is excluded from the total rather
than counted as passing. The streaming groups are not perfectly repeatable — `strm2` returned
684, 684 and 678 across three runs of this same build — so treat the last digit of the total as
noise, not signal.

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
