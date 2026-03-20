# PhoenixmlDb.Xslt

XSLT 4.0 transformation engine for [PhoenixmlDb](https://phoenixml.dev) — transform XML documents into HTML, JSON, CSV, text, or other XML formats.

## Features

- **XSLT 3.0/4.0** — template matching, streaming, accumulators, packages, maps/arrays
- **All output methods** — HTML5, XML, XHTML, JSON, text, CSV, adaptive
- **Multiple outputs** — `xsl:result-document` for multi-file generation
- **Full XPath 4.0** — 240+ built-in functions available in all expressions
- **Packages** — reusable stylesheet libraries with visibility control

## Quick example

```csharp
using PhoenixmlDb.Xslt;

var transformer = new XsltTransformer();
await transformer.LoadStylesheetAsync(
    File.ReadAllText("style.xsl"),
    new Uri(Path.GetFullPath("style.xsl")));

transformer.SetParameter("title", "My Report");
var html = await transformer.TransformAsync(
    File.ReadAllText("data.xml"));

// Handle secondary outputs (xsl:result-document)
foreach (var (href, content) in transformer.SecondaryResultDocuments)
    File.WriteAllText(Path.Combine(outputDir, href), content);
```

## Related packages

| Package | Description |
|---------|-------------|
| **PhoenixmlDb.Core** | Core types and XDM data model (dependency) |
| **PhoenixmlDb.XQuery** | XQuery 4.0 query engine (dependency) |
| **PhoenixmlDb.Xslt.Cli** | `xslt` command-line tool |

## Documentation

Full documentation at [phoenixml.dev](https://phoenixml.dev)

## License

Apache 2.0
