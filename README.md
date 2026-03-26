# PhoenixmlDb XSLT

A modern XSLT 4.0 transformation engine for .NET with streaming and package support.

## Features

### XSLT 4.0
- **xsl:switch** — conditional processing with select context
- **xsl:for-each-member** — array member iteration
- **xsl:item-type** — named type declarations
- **xsl:record** — record construction
- **method="csv"** — CSV serialization output

### XSLT 3.0 (97.9% W3C Conformance — 2604/2661 tests)
- Full template matching with priorities and modes
- xsl:iterate, xsl:try/catch, xsl:evaluate
- xsl:use-package with override, xsl:original, visibility
- xsl:expose, xsl:accept with hidden visibility
- Streaming (xsl:source-document, xsl:mode streamable, xsl:fork, accumulators)
- Higher-order functions, maps, arrays
- Accumulators, merge, JSON/adaptive output

### W3C Conformance
- **969/1026 declaration tests** (94.4%) including package test sets
- **648/648 expression tests** (100%)
- **987/987 regex tests** (100%)
- Clone [W3C XSLT 3.0 test suite](https://github.com/nicolo-ribaudo/xslt30-test) to run conformance tests

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
