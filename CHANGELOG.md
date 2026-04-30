# Changelog

## Unreleased

### Fixes
- Fix fn:transform `post-process` option — was completely unimplemented. Now invokes the post-process function for each result document, enabling stylesheet chaining pipelines
- Fix streaming: empty template suppression now correctly skips all child events instead of leaking them as stray output
- Fix `castable as xs:integer` / `instance of xs:Name` and other prefixed atomic types wrongly raising XPST0051 — the validator was treating prefixed type references as if they were unprefixed. Reported by Martin Honnen against DocBook xslTNG and Schxslt2 transpile.xsl (paired XQuery fix in `XdmSequenceType.UnprefixedTypeName`/`LocalTypeName` split)
- Fix XTSE3450 false positive when an importing module declared a static variable whose local name matched an imported static param **in a different namespace** (e.g. DocBook xslTNG's `v:debug` colliding with `param.xsl`'s `debug`) — static variable tracking now keys on full QName, not local name
- Fix `namespace::` axis raising XQST0134 in XSLT/XPath — the axis is deprecated but optional in XPath 3.1/XSLT 3.0 (only XQuery prohibits it). Added `AllowNamespaceAxis` to the parser facade; XSLT sets it to `true`. Reported against DocBook xslTNG
- Fix locally-declared `xmlns:*` on `xsl:when` not being visible to its own `test=` expression (raised XPST0081). Now the namespace context is anchored at the `xsl:when` itself when parsing the test. Reported against DocBook xslTNG `docbook.xsl` line 152
- Add source location (line/column) to XPST0051 errors for "unknown atomic type" — previously the error fired without context, making it hard to locate the offending expression

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
