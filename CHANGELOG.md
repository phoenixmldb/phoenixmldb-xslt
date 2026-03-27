# Changelog

## 1.1.0 (2026-03-26)

### Features
- **Streaming expression evaluation**: `xsl:source-document streamable="yes"` now evaluates consuming expressions (count, sum, min, max, avg, string-join) via StreamWatcher infrastructure
- **Top-level map serialization**: maps at the top level are now serialized as JSON instead of being silently ignored
- **`json-to-xml` escape option**: the `escape` flag is now threaded through and adds `escaped="true"` attribute on string elements with backslash escape sequences
- **`xsl:message` in CLI**: messages now output to stderr; `MessageListener` exposed on `XsltTransformer` facade
- **Improved error diagnostics**: XTTE0505 errors include template name/match pattern; CLI prints `XsltException.Location` line/column

### Fixes
- Fix function return type coercion for string types — `xsl:value-of` in function body now correctly atomizes `TextNodeItem` to `xs:string` when the function declares `as="xs:string?"` (fixes #4, Schxslt2 transpile)
- Fix template return type checking for prefixed elements — include in-scope namespace declarations when re-parsing serialized template output so elements like `<svrl:active-pattern/>` are recognized as nodes, not strings
- Fix EQName element type matching — `as="element(Q{uri}name)"` now extracts local name for comparison
- Fix string value propagation in XSLT copy and `LinkChild` operations
- Fix DTD processing disabled by default (security)
- Fix HTML/XHTML indent default to yes per XSLT 3.0 spec
- Fix `supports-dynamic-evaluation` system property to report "yes"

## 1.0.0 (2026-03-20)

Initial release with XSLT 3.0/4.0 support (97.9% W3C conformance), streaming, packages, and CLI tool.
