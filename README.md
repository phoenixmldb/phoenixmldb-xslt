# PhoenixmlDb XSLT

A modern XSLT 4.0 transformation engine for .NET with streaming and package support.

## Features

### XSLT 4.0
- **xsl:switch** — conditional processing with select context
- **xsl:for-each-member** — array member iteration
- **xsl:item-type** — named type declarations
- **xsl:record** — record construction
- **method="csv"** — CSV serialization output

### XSLT 3.0 (99.9% W3C Conformance)
- Full template matching with priorities and modes
- xsl:iterate, xsl:try/catch, xsl:evaluate
- xsl:use-package with override, xsl:original, visibility
- xsl:expose, xsl:accept with hidden visibility
- Streaming (xsl:source-document, xsl:fork)
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

## Quick Start

```csharp
var transformer = new XsltTransformer();
await transformer.LoadStylesheetAsync(xsltString);
var result = await transformer.TransformAsync(xmlInput);
```

## License

Apache 2.0 — see [LICENSE](LICENSE)

## Related Projects

- [phoenixmldb-core](https://github.com/phoenixmldb/phoenixmldb-core) — Core types and XDM
- [phoenixmldb-xquery](https://github.com/phoenixmldb/phoenixmldb-xquery) — XPath/XQuery 4.0 engine
- [phoenixmldb-cli](https://github.com/phoenixmldb/phoenixmldb-cli) — CLI tools
