# PhoenixmlDb XSLT

A modern XSLT 4.0 transformation engine for .NET with streaming and package support.

## Features

### XSLT 4.0
- **xsl:switch** — conditional processing with select context
- **xsl:for-each-member** — array member iteration
- **xsl:item-type** — named type declarations
- **xsl:record** — record construction
- **method="csv"** — CSV serialization output

### XSLT 3.0 (96.2% W3C conformance — 10,224/10,630 cases, measured 2026-09-02)
- Full template matching with priorities and modes
- xsl:iterate, xsl:try/catch, xsl:evaluate
- xsl:use-package with override, xsl:original, visibility
- xsl:expose, xsl:accept with hidden visibility
- Streaming (xsl:source-document, xsl:mode streamable, xsl:fork, accumulators)
- Higher-order functions, maps, arrays
- Accumulators, merge, JSON/adaptive output

## Conformance

Every figure below is measured, dated, and reproducible. Nothing here is an estimate.

### W3C XSLT 3.0 — 10,224/10,630 cases (96.2%), 406 failing

Measured 2026-09-02 against `w3c/xslt30-test` @ `fddf1cf`.

| Group | Passing | | Failing |
|---|---|---|---|
| `attr` — attributes | 1082/1117 | 96.9% | 35 |
| `decl` — declarations | 987/1080 | 91.4% | 93 |
| `type` — types | 755/766 | 98.6% | 11 |
| `fn` — functions | 1080/1131 | 95.5% | 51 |
| `strm` — streaming | 2274/2373 | 95.8% | 99 |
| `expr` — expressions | 636/648 | 98.1% | 12 |
| `misc` | 1880/1921 | 97.9% | 41 |
| `insn` — instructions | 1530/1594 | 96.0% | 64 |
| **Total** | **10,224/10,630** | **96.2%** | **406** |

The `sandp` group runs but reports no per-case counts, so it is excluded from the total rather
than counted as passing.

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
