# Release History

## 1.2.8 (2026-05-06)

### `fn:transform()` auto-registers on assembly load

Adds a `[ModuleInitializer]` to `PhoenixmlDb.Xslt` that registers
`XsltTransformProvider` with `TransformFunction.Provider` the first time the assembly
loads. Any application or CLI that references `PhoenixmlDb.Xslt` gets `fn:transform()`
working — no explicit
`TransformFunction.Provider = new XsltTransformProvider()` call needed.

Setup that fixed: standalone `xquery` CLI tool that bundles
`PhoenixmlDb.Xslt` (via PackageReference) so XQuery scripts can call `fn:transform()`
without an extra registration step. Reported by Martin Honnen.

## 1.2.7 (2026-05-05)

### Schxslt2 transpiled validation runs cleanly

A second source-order fix for `as=` typed bodies — this one for the `apply-templates`
dispatch path. The `call-template` path was already covered by 1.2.6; this release
extends the same reassembly logic to matched templates invoked via `apply-templates`,
which is what Schxslt2's transpiled validation stylesheet uses:

```xml
<template match="root()" as="element()*" mode="...validate">
  <svrl:active-pattern/>
  <apply-templates select="root()" mode="group..."/>
</template>
```

The body emits an LRE before `apply-templates` whose results route through
`xsl:sequence` (i.e. via `_sequenceAccumulator`). Before this fix, the engine put all
accumulator items before the serialized LRE output regardless of source order, so
`<svrl:schematron-output>` ended up with `<svrl:failed-assert/>` and
`<svrl:successful-report/>` *before* `<svrl:active-pattern/>` and `<svrl:fired-rule/>`.

Internally, this required refactoring the as-body capture state into a scoped
`AsBodyCapture` object so position recording only fires when an item goes into *that*
capture's accumulator, not into an inner accumulator (e.g. an `xsl:variable` typed-
sequence buffer) that just happens to be active at the same time.

Also bumps `PhoenixmlDb.XQuery` pin to 1.2.3 for `fn:doc-available` accepting
`xs:untypedAtomic`.

Reported by Martin Honnen.

## 1.2.6 (2026-05-05)

### Schxslt2 transpile.xsl runs cleanly

Two fixes for `as="node()*"` template/function bodies that compose attributes and
non-attribute children, and one CLI ergonomics fix:

- **Source-order reassembly.** When the body emitted an `xsl:attribute` (routed to
  `_sequenceAccumulator`) before an LRE (written to `_output`), the parent element
  constructor saw the attribute land *after* the LRE and raised `XTDE0410`. The engine
  now records the `_output` offset for each accumulator item and weaves them back in
  document order at result assembly. This is exactly the failure mode in Schxslt2
  1.10.3's `schxslt:failed-assertion-content` template.
- **`xsl:where-populated` filters empty attributes inside `as=` bodies.** The
  zero-length-value filter previously only saw attrs that routed via `_collectedAttributes`;
  attrs that landed in `_sequenceAccumulator` (the `as=` body path) leaked through.
  Schxslt2's `failed-assertion-attributes` no longer emits `ruleId=""` / `patternId=""`
  for rules without an `@id`.
- **CLI catches `XQueryRuntimeException` / `XQueryException`** and prints a clean
  `XQuery error: <code>: <message>` instead of a .NET stack trace. Stack traces still
  print under `--verbose`. Useful when an XPath/XQuery runtime error escapes an XSLT
  instruction without a surrounding `XsltException` wrapper.

Reported by Martin Honnen.

## 1.2.5 (2026-05-05)

### Diagnostics: source location and module URI on every error

This release threads originating-module and line/column info through every error path
that previously raised a bare message, and surfaces them in the CLI:

```
XSLT error at /path/to/stylesheet.xsl:70:4: XPST0008: Variable $v:debug is not declared in the static use-when context
```

- `XsltException.Location` now carries a `Module` field (file path / URI of the originating
  stylesheet module) alongside `Line` and `Column`. Pulls in `PhoenixmlDb.XQuery` 1.2.2
  for the new `SourceLocation.Module` property.
- `XPST0008` in static use-when shows the full QName: `$v:debug` instead of `$debug`, and
  `Q{namespace}local` for prefix-less names with a namespace. Includes the source location
  of the offending element.
- `XTDE0410` / `XTDE0420` (attribute-after-children, attribute-on-document-node) carry the
  source location of the offending `xsl:attribute`, `xsl:copy`, or `xsl:copy-of` instruction.
- Stylesheet loading goes through `XmlReader` whenever a base URI is available so
  `XElement.BaseUri` is populated — that's the input to module-path detection at every
  diagnostic site.

Reported by Martin Honnen — debugging multi-module stylesheets (DocBook xslTNG,
Schxslt2) without source location info was unworkable.

## 1.2.4 (2026-04-29)

### fn:serialize adaptive method

Pulls in `PhoenixmlDb.XQuery` 1.2.1, which fixes `fn:serialize($input)` and
`fn:serialize($input, map { 'method': 'adaptive' })` producing JSON instead of adaptive
output per XPath/XQuery 3.1 §17.1.3. The fallback serialization path (used by XSLT and
any caller whose node provider isn't `XdmDocumentStore`) was hard-coded to JSON; it now
honors the requested method and emits adaptive form for maps (`map{key:value,…}`),
arrays (`[…]`), sequences (`(…)`), atomic types in constructor form, and nodes via the
XML serializer. The 1-arg form defaults to adaptive per spec.

Reported by Martin Honnen.

## 1.2.3 (2026-05-03)

### Fixes

- **`mode="#current"` now preserved across `xsl:for-each` and `xsl:for-each-group`.** The
  engine was clearing the current-mode tracking field alongside the current-template-rule
  field on entry to these instructions. Per XSLT 3.0 §13.4.1, only the current template
  rule becomes absent inside `xsl:for-each`/`xsl:for-each-group`; the current mode is
  unchanged. The bug meant a nested `<xsl:apply-templates mode="#current">` resolved to the
  unnamed mode and silently failed to match templates declared with `mode="m1"`. Schxslt2's
  transpile pass relies on this for dispatching `sch:rule` templates from inside a
  `for-each` over `map:keys($patterns)` — those templates never fired before. Reported by
  Martin Honnen.

## 1.2.2 (2026-05-02)

### Fixes / UX

- **CLI `-p name=value` now feeds static parameters too.** Previously `xslt -p debug=true …`
  only set runtime parameters; `<xsl:param name="debug" static="yes" select="false()"/>` kept
  its default because the static-param compile-time path never saw the override. The CLI now
  passes `-p` values to both `LoadStylesheetAsync(staticParams: …)` and `SetParameter`,
  covering both kinds without the user having to know which is which.
- **External static-param value parser accepts bare `true` / `false` / integers / doubles**,
  not only XPath-shaped literals like `true()` / `false()`. So `xslt -p debug=true` Just
  Works for `<xsl:param … as="xs:boolean">`. Reported by Martin Honnen against Schxslt2's
  `schxslt:debug` static parameter.

## 1.2.1 (2026-05-01)

### Fixes

- **`<xsl:variable as="map(*)">…<xsl:map>…</xsl:map></xsl:variable>` lost the map at the
  variable boundary** — the global-init non-sequence-type branch captured serialized text
  output, and `xsl:map`'s top-level fallback path (when no sequence accumulator is active)
  writes the map as JSON via `WriteText`. The variable then held a JSON string, and any
  downstream `map:contains($var, …)` failed at runtime with XPTY0004 "must be a single map".
  Fix: route map / array / function / record item types through the sequence-accumulator
  branch so `xsl:map` adds the live `Dictionary<object, object?>` and the variable holds
  the map item directly. Reported by Martin Honnen running DocBook xslTNG 2.8.0
  `docbook.xsl` against `samples/article.xml`.

## 1.2.0 (2026-04-30)

### Schema-aware XSLT, end-to-end

`XsltTransformer.SchemaProvider` is the public extension point — defaults to a fresh
`XsdSchemaProvider`, swap with any `ISchemaProvider` implementation. The whole
schema-aware feature surface now actually does work:

- `xsl:import-schema` is captured during stylesheet parsing and forwarded to the
  registered provider's `ImportSchema` when the stylesheet loads. Schema-location URIs
  resolve relative to the stylesheet base URI.
- `validation="strict|lax"` on `xsl:result-document`, `xsl:document`, `xsl:element`,
  `xsl:copy`, `xsl:copy-of`, `xsl:attribute` runs schema validation against the loaded
  set. Strict mode raises XQDY0027 (wrapped in `XsltException`); lax mode skips silently
  when no declaration is found, per XSLT 3.0 §27.2.
- `as="schema-element(name)"` and `as="schema-attribute(name)"` on `xsl:variable`,
  `xsl:param`, function parameters/return types — parsed and matched at runtime via
  `ISchemaProvider.MatchesSchemaElement` (substitution-group members, schema-derived
  type annotations honored). Names accept bare local, prefixed (`po:order`), or
  EQName (`Q{http://x}order`) syntax.
- A bare `<xsl:import-schema/>` (no namespace, no location) is treated as a
  schema-aware-mode marker only — it no longer fails with XQST0059.

### Critical fixes from real-world stylesheets (Martin Honnen reports)

DocBook xslTNG 2.7.1 `docbook.xsl` now compiles successfully:

- Prefixed atomic types (`castable as xs:integer`, `instance of xs:Name`) wrongly raised
  XPST0051. Paired XQuery fix in `XdmSequenceType.UnprefixedTypeName`/`LocalTypeName`.
- XTSE3450 false positive when an importer declared a static `xsl:variable` whose local
  name matched an imported static `xsl:param` *in a different namespace* (DocBook's
  `v:debug` colliding with `param.xsl`'s `debug`). Static-variable tracking now keys on
  full QName.
- `namespace::` axis raised XQST0134 in XSLT/XPath. Now permitted (deprecated-but-optional
  per XPath 3.1 §3.2); only XQuery prohibits.
- Locally-declared `xmlns:*` on `xsl:when` not visible to its own `test=` expression.
  Now scoped correctly.
- XPST0051 errors carry source location (line/column).

### Other

- Pin `PhoenixmlDb.Core` to 1.0.28 and `PhoenixmlDb.XQuery` to 1.2.0.

## 1.1.0.22 (Unreleased)

### Fixes
- **fn:transform `post-process` option**: the `post-process` option was completely unimplemented — the function value was silently ignored. Now extracted from the options map and invoked as `function($uri, $result)` for each entry in the result map (primary output and secondary result documents). The returned value replaces the original, enabling stylesheet chaining pipelines.
- **Streaming: empty template suppression leaks children**: when a user template with an empty body matched an element in streaming mode (e.g., `<xsl:template match="uomConversion[accumulator-before('found')]"/>`), the element start/end tags were correctly suppressed but all child events (text, child elements) continued to be processed by built-in templates, producing stray output. Fixed by tracking suppression depth in `StreamingXmlProcessor`: when `MatchAndExecuteStreamingNodeAsync` returns true (empty template body), all child events are skipped until the matching EndElement. Accumulator rules still fire for suppressed subtrees so accumulator state remains correct.
- **xsl:message with text-only content**: `<xsl:message>text {expr}</xsl:message>` (sequence constructor with no child elements) silently produced empty messages. `ParseMessage` checked `HasElements` instead of `Nodes().Any()`, so text-only bodies were dropped.
- **`root()` pattern matching**: `match="root()"` was not recognized as a document-node pattern (equivalent to `/`). Templates with this pattern silently never fired. Affected Schxslt2 compiled stylesheets.
- **EQName catch variable resolution**: `$Q{http://www.w3.org/2005/xqt-errors}line-number` in `xsl:catch` failed to resolve because catch variables were registered with `NamespaceId` form only. Now registered under both `NamespaceId` and EQName forms for Dictionary exact-match (QName is a record struct).
- **EQName variable references at compile time**: `$Q{uri}name` from the XPath parser had `NamespaceId.None` while declared variables had a resolved `NamespaceId`. Fixed by resolving EQName references in `ResolveExpressionNamespaces` during stylesheet compilation. This fixed Schxslt2 compiled stylesheet execution with EQName params.

- **fn:transform delivery-format 'document'**: two issues fixed. (1) Source node from `doc()` needed to be serialized to XML for the inner engine to parse into its own node store (passing the outer store directly didn't work because the inner engine needs independent namespace resolution). (2) `ParseResultAsDocument` created nodes in a local throw-away store — the outer context couldn't access children when serializing the returned document. Fixed by passing the outer `_nodeStore` to `ParseResultAsDocument`.
- **xsl:message location info**: CLI now shows source line/column in message output (e.g., `xsl:message (18:6): text`). Added `MessageListenerWithLocation` on both `XsltTransformOptions` and `XsltTransformer` facade.
- **fn:transform static-params**: the `static-params` option was ignored entirely. Now extracted from the options map, passed to `StylesheetParser.Parse` for compile-time resolution, and also passed as `InitialParameters` for runtime availability.
- **fn:transform stylesheet-node**: `doc()` node passed as `stylesheet-node` crashed because `StringValue` was used (strips markup). Now serializes the node to XML via `SerializeXdmNodeToXml`.

## 1.1.0 (2026-03-26)

### Features
- **Streaming expression evaluation**: `xsl:source-document streamable="yes"` now evaluates consuming expressions (count, sum, min, max, avg, string-join) via StreamWatcher infrastructure. Pre-scanner identifies consuming sub-expressions, generates watchers, fires them during streaming pass.
- **Top-level map serialization**: maps at the top level are now serialized as JSON text instead of being silently ignored.
- **`json-to-xml` escape option**: the `escape` flag is threaded through `JsonToXmlConverter` and adds `escaped="true"` attribute on string elements containing backslash escape sequences.
- **`xsl:message` in CLI**: messages now output to stderr. `MessageListener` property exposed on `XsltTransformer` facade.
- **Improved error diagnostics**: XTTE0505 errors include template name or match pattern. CLI prints `XsltException.Location` line/column when available.

### Fixes
- **Function return type coercion for string types** (fixes #4): `xsl:value-of` in a function body produces a `TextNodeItem` in the sequence accumulator. When the function declares `as="xs:string?"`, the `TextNodeItem` must be atomized to a string. Previously coercion only applied to numeric/date/boolean types. Added `IsAtomicReturnType` covering all atomic types. This fixed Schxslt2 transpile failure.
- **Template return type checking for prefixed elements**: serialized template output re-parsed via XML wrapper lacked in-scope namespace declarations, so prefixed elements like `<svrl:active-pattern/>` failed to parse and were treated as strings. Added `BuildInScopeNamespaceDeclarations()` to include all in-scope namespaces on the wrapper.
- **EQName element type matching**: `as="element(Q{uri}name)"` stored the full EQName but compared only `localName`. Added `MatchesElementName` that extracts the local part from EQName syntax.
- **`root()` pattern matching**: `match="root()"` was not recognized as a document-node pattern (equivalent to `/`). Templates with this pattern silently never fired. Fixed in `ParsePattern` to treat `root()` like `document-node()`.
- **EQName variable resolution in xsl:catch**: catch error variables (`$Q{http://www.w3.org/2005/xqt-errors}line-number`) were set with `NamespaceId` but looked up via EQName with `ExpandedNamespace`. QName is a record struct — Dictionary equality requires all fields. Fixed by registering catch variables under both forms.
- **EQName variable references at compile time**: `$Q{uri}name` from the XPath parser had `NamespaceId.None` while declared variables had a resolved `NamespaceId`. Fixed by resolving EQName references to their proper `NamespaceId` in `ResolveExpressionNamespaces` during stylesheet compilation. This fixed Schxslt2 compiled stylesheet execution.
- **String value propagation in XSLT copy and LinkChild**: `_stringValue` not carried through copy operations or recomputed after tree mutations.
- **DTD processing**: enabled by default (security risk). Changed to `AllowDtdProcessing = false` default.
- **HTML/XHTML indent**: defaulted to no. Changed to yes per XSLT 3.0 §20.
- **`supports-dynamic-evaluation`**: reported "no". Changed to "yes".

## 1.0.0 (2026-03-20)

Initial release: XSLT 3.0/4.0 engine (97.9% W3C conformance — 2604/2661 tests), streaming, packages, higher-order functions, maps/arrays, accumulators, and `xslt` CLI tool.
