# Changelog

## 1.2.9 (2026-05-06)

### Features
- **HTTP(S) URLs work as stylesheet inputs and as `xsl:import` / `xsl:include` hrefs.** The `xslt` CLI now accepts `http://` and `https://` URLs directly (`xslt https://example.com/sheet.xsl input.xml`) — fetched via `HttpClient` with a 30 s timeout. The library's stylesheet parser also resolves imports against an HTTP base URI: when the entry stylesheet is loaded with an `https://...` base, its `xsl:import href="lib.xsl"/>` resolves to `https://.../lib.xsl` and is fetched over HTTP. Previously raised "Stylesheet not found" / `XTSE0165: Cannot find stylesheet module 'lib.xsl'`. `ResourcePolicy.IsAllowed(uri, ImportStylesheet)` is consulted before the fetch when a policy is configured. Reported by Martin Honnen against schxslt2 stylesheets hosted on github.io.

## 1.2.8 (2026-05-06)

### Features
- **Auto-registers with `fn:transform()`** on assembly load. A `[ModuleInitializer]` in `XsltModuleInitializer` now sets `TransformFunction.Provider ??= new XsltTransformProvider()` the first time any type in `PhoenixmlDb.Xslt` is touched. Any application or CLI that references `PhoenixmlDb.Xslt` gets `fn:transform()` working out of the box — the previous "Add a reference to PhoenixmlDb.Xslt and call TransformFunction.Provider = new XsltTransformProvider()" prompt is no longer needed. Reported by Martin Honnen against the standalone `xquery` CLI tool.

## 1.2.7 (2026-05-05)

### Fixes
- **`as=` template/function bodies dispatched via `apply-templates` now reassemble result items in source order**, matching the prior fix to the `call-template` path. Schxslt2's transpiled validation stylesheet has `<template match="root()" as="element()*" mode="...validate">` whose body emits an LRE (`<svrl:active-pattern/>`) before `<apply-templates>` whose results route through `xsl:sequence`. Before this fix, the apply-templates dispatch path placed all accumulator-routed items (`xsl:sequence`, `xsl:attribute`) before serialized output regardless of source order, so `<svrl:schematron-output>` ended up with `<svrl:failed-assert/>`/`<svrl:successful-report/>` *before* `<svrl:active-pattern/>`/`<svrl:fired-rule/>`. Reported by Martin Honnen.
- Refactored as-body capture into a scoped `AsBodyCapture` object that only records position offsets when items go to *its* accumulator. Inner accumulators (xsl:variable typed-sequence buffers, etc.) no longer leak position recordings into the outer capture's list. Eliminates the position/item count mismatch that prevented the earlier per-field tracking from working.
- Bumps `PhoenixmlDb.XQuery` pin to 1.2.3 for `fn:doc-available` accepting `xs:untypedAtomic`.

## 1.2.6 (2026-05-05)

### Fixes
- **`as="node()*"` template/function bodies now reassemble result items in source order.** When a body produces both `xsl:attribute` (which routes to `_sequenceAccumulator`) and an LRE (which writes to `_output`), the engine previously placed all accumulator items *after* serialized output regardless of source order. The parent element constructor then saw an attribute after non-attribute children and raised a spurious `XTDE0410`. Now records the `_output` offset at the moment each accumulator item is added and weaves them back into result items in document order. Reported by Martin Honnen against Schxslt2 1.10.3 transpile.xsl (`schxslt:failed-assertion-content` composes `svrl:failed-assert` from an `as="node()*"` helper that returns attrs followed by `svrl:text`).
- **`xsl:where-populated` now filters zero-length-valued `xsl:attribute` even inside an `as=` body.** Previously `where-populated`'s zero-length attribute filter only saw attrs routed via `_collectedAttributes`; attrs that landed in `_sequenceAccumulator` (the `as=` body path) leaked through unfiltered. Now snapshots accumulator length on entry, filters insignificant attrs from the slice on exit. Schxslt2's `failed-assertion-attributes` no longer emits stray `ruleId=""` / `patternId=""` on rules without an `@id`.
- CLI now catches `PhoenixmlDb.XQuery.XQueryRuntimeException` and `PhoenixmlDb.XQuery.Functions.XQueryException` and formats them as `XQuery error: <code>: <message>` instead of dumping a .NET stack trace. Stack traces still print under `--verbose`. Reported by Martin Honnen against DocBook xslTNG `docbook.xsl` (XPDY0050-class error escaping the engine without an XSLT-instruction wrapper).

## 1.2.5 (2026-05-05)

### Diagnostics
- **Source-location info on every error**, including the originating module path. Every `XsltException.Location` now carries a `Module` (the imported/included stylesheet's URI / file path) alongside `Line` / `Column`. The CLI's error formatter prints `XSLT error at <module>:<line>:<col>: <message>` so failures in multi-module stylesheets (DocBook xslTNG, Schxslt2) are debuggable. Reported by Martin Honnen.
- **`XPST0008` in static use-when shows the full QName** (`$prefix:local` or `Q{uri}local`) instead of just the bare local name. A reference to `$v:debug` no longer reports as `$debug`, eliminating the namespace-mismatch confusion. Includes the offending element's source location.
- **`XTDE0410` / `XTDE0420` (attribute-after-children) carry the source location** of the offending `xsl:attribute` / `xsl:copy` / `xsl:copy-of` instruction. A best-effort `_currentInstructionLocation` field is updated by attribute-emitting instruction handlers and consumed by the helper paths that ultimately raise the error.
- Stylesheet loading now always goes through `XmlReader` when a base URI is available, so `XElement.BaseUri` is populated and downstream `GetSourceLocation` calls capture the originating module.
- Bumps `PhoenixmlDb.XQuery` pin to 1.2.2 for the new `SourceLocation.Module` field.

## 1.2.4 (2026-04-29)

### Fixes
- Pulls in PhoenixmlDb.XQuery 1.2.1, which fixes `fn:serialize($input)` (1-arg) and `fn:serialize($input, map { 'method': 'adaptive' })` producing JSON instead of adaptive output. Per XPath/XQuery 3.1 §17.1.3, the default serialization method is `adaptive`. Maps now serialize as `map{key:value,…}`, arrays as `[…]`, sequences as `(…)`, atomic types in their constructor form (e.g. `xs:date("2025-01-01")`), and nodes as XML. Reported by Martin Honnen.

## Unreleased

### Fixes
- Fix `mode="#current"` not propagating across `xsl:for-each` and `xsl:for-each-group`. The engine was nulling the current-mode tracking field alongside the current-template-rule field on entry to these instructions; per XSLT 3.0 §13.4.1 only the current template rule becomes absent, the current mode is unchanged. Schxslt2's transpile pass relies on this for dispatching `sch:rule` templates from inside a for-each over `map:keys($patterns)`. Reported by Martin Honnen.
- Fix `<xsl:variable as="map(*)">…<xsl:map>…</xsl:map></xsl:variable>` ending up as a JSON-serialized string instead of the map item. Downstream `map:contains($var, …)` then failed with XPTY0004. Routes map / array / function / record item types through the sequence-accumulator branch so the live `Dictionary` / `List` lands as the variable value. Reported by Martin Honnen against DocBook xslTNG 2.8.0.
- CLI `-p name=value` now feeds static parameters too (in addition to runtime parameters), so `xslt -p debug=true …` overrides `<xsl:param name="debug" static="yes" select="false()"/>` as expected. External static-param value parser additionally accepts bare `true` / `false` / integers / doubles, on top of the existing XPath-shaped literals (`true()`, `false()`, `'…'`, `"…"`, `()`). `LoadStylesheetAsync(staticParams: …)` cross-feeds the static values into the runtime parameter map so both compile-time and runtime see consistent values. Reported by Martin Honnen against Schxslt2's `schxslt:debug` static parameter.

### Features
- **`as="schema-element(name)"` and `as="schema-attribute(name)"` are now parsed and runtime-matched** against the registered `ISchemaProvider`. Works on `xsl:variable`, `xsl:param`, function parameters, function return types, and template return types. Substitution-group members and elements with schema-derived type annotations match correctly. Local prefixed names (e.g. `i:item`) resolve via the in-scope namespace declarations on the surrounding XSLT element; EQName syntax (`Q{uri}item`) is also accepted.
- **`xsl:result-document validation="strict|lax"` runs schema validation.** Schema-aware result documents are now actually validated against the registered `ISchemaProvider`. Strict mode raises XQDY0027 (wrapped in `XsltException`) when content doesn't match a global declaration; lax mode skips silently when no declaration is found, per XSLT 3.0 §27.2. `validation="preserve|strip"` remain no-ops (they only affect type annotations on the XDM tree). Required: a registered `ISchemaProvider` (default `XsdSchemaProvider` in-box) and at least one loaded schema for strict mode to be useful.
- **`validation="strict|lax"` on `xsl:document`, `xsl:element`, `xsl:copy`, `xsl:copy-of`, `xsl:attribute`** now runs through the registered `ISchemaProvider`. Element/copy/copy-of capture the produced fragment via output-buffer slicing and validate via `ISchemaProvider.ValidateXmlFragment`, which now accepts an optional `inScopeNamespaces` map so prefixes declared on enclosing elements (or the stylesheet root) resolve correctly without being repeated on every constructed element. `xsl:attribute validation="strict"` confirms a global schema-attribute declaration exists for the attribute name (raises XQDY0027 if missing); full value-against-type validation is best-handled at the parent element level. A bare `<xsl:import-schema/>` (no namespace, no location) is treated as a schema-aware-mode marker only — it no longer raises XQST0059 trying to load an empty schema.
- **`xsl:import-schema` wired to `ISchemaProvider`.** Schema imports captured during stylesheet parsing are now forwarded to the runtime provider's `ImportSchema` method when the stylesheet is loaded. `XsltTransformer.SchemaProvider` defaults to a fresh `XsdSchemaProvider`; callers can replace it with a custom `ISchemaProvider` implementation (RelaxNG, Schematron-derived, in-memory) before calling `LoadStylesheetAsync`. Schema-location URIs in `xsl:import-schema` resolve relative to the stylesheet base URI. Errors from the provider surface as `XsltException` with the underlying error code (e.g. XQST0059 when a schema can't be located). Imports are walked recursively across imported and included modules.

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
