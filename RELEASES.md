# Release History

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
