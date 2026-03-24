# xslt

Command-line XSLT 3.0/4.0 processor for .NET. Transform XML documents from the terminal using the PhoenixmlDb XSLT engine.

## Installation

```bash
dotnet tool install -g xslt
```

## Usage

```bash
# Transform XML with a stylesheet
xslt stylesheet.xsl input.xml

# Write output to a file
xslt -o result.html report.xsl data.xml

# Start from a named template (no source needed)
xslt -it main generate.xsl

# Pass parameters
xslt -p year=2026 -p title="Report" style.xsl data.xml

# Read source from stdin
cat data.xml | xslt transform.xsl

# Show timing breakdown
xslt --timing style.xsl large-input.xml

# Validate a stylesheet without running
xslt --dry-run style.xsl

# Stream large files (lower memory)
xslt --stream style.xsl large-input.xml
```

## Features

- **XSLT 3.0/4.0** — packages, streaming, maps/arrays, higher-order functions, JSON output
- **Multiple output methods** — XML, HTML, XHTML, text, JSON, adaptive
- **Streaming** — process large files without loading into memory
- **xsl:result-document** — generate multiple output files in one transform
- **Parameters** — pass values from the command line
- **Timing** — built-in performance profiling
- **Tracing** — log template matching, function calls, and built-in rules

## Documentation

Full documentation at [phoenixml.dev](https://phoenixml.dev/tools/xslt-cli.html)

## License

Apache-2.0
