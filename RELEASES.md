# Release History

## 1.3.4 (2026-05-11)

### Docbook TNG conformance push (multiple Martin Honnen reports)

Six engine bugs surfaced while bisecting the Docbook xslTNG 2.8.0
stylesheet against `samples/article.xml`. Each fix unblocks the next
bug in the chain; together they take Docbook TNG several thousand
instructions deeper before failing.

**1. `as="xs:string"` accepts `xs:anyURI` per function-conversion rules.**
Per F&O 4.0 §1.6.3, an xs:anyURI value supplied where xs:string is
expected is cast to xs:string. We rejected it. `templates.xsl` declares
`<xsl:variable name="uri" as="xs:string" select="resolve-uri(...)"/>`
and resolve-uri returns xs:anyURI; the bind raised XTTE0570.

**2. Empty `<xsl:document/>` produces a real document-node.**
The global-variable initializer's body-content path lacked an
`as="document-node()"` branch. Empty `<xsl:document/>` fell through to
the RTF/string path and ended up as `xs:string ""`. Downstream
`$v:templates/*` then evaluated `Child::*` on the empty string,
raising XPTY0020 — Martin's literal "v:theme-list returns
ResultTreeFragment" report.

**3. Atomic-typed `<xsl:variable>` body isolates its sequence accumulator.**
The else branch of `BindVariableAsync` (atomic `as=` with body
content) didn't save/clear `_sequenceAccumulator`. When invoked inside
an `xsl:function` body (which sets up its own accumulator), the
variable's `<xsl:sequence>` leaked into the FUNCTION's accumulator —
body output empty, variable bound to `""`, XTTE0570. Found in Docbook
TNG `$process`. Also added xs:string→xs:boolean coercion in
`CoerceToType` so `"true"`/`"false"` lexical forms convert.

**4. `xsl:with-param` body content preserves typed items in xsl:iterate.**
Same shape as fix 3 but in `<xsl:iterate>`'s parameter binding (both
initial `<xsl:param>` and per-iteration `<xsl:next-iteration>`'s
`<xsl:with-param>`). Body content without `select=` ran without a
sequence accumulator, so `<xsl:sequence>` inside fell through to text
serialization. A doc-node bound this way was serialized to text and
rebound as `xs:untypedAtomic`. Found in Docbook TNG `fp:run-transforms`
where each iteration re-binds `$document`. Extracted
`EvaluateBodyContentToValueAsync` helper.

**5. `xsl:map` content ignores insignificant whitespace text.**
The XTTE3375 check tripped on whitespace strings emitted by source
formatting between sibling `<xsl:map-entry>` declarations (e.g. `\n  `).
Per XSLT 3.0 §6.2 such whitespace is insignificant.

**6. Non-package stylesheet components default to public visibility.**
`ParseVisibility` defaulted to `Visibility.Private` everywhere. Per
XSLT 3.0 §3.5: components in a regular stylesheet (not in `xsl:package`)
default to **public**. Defaulting to Private blocked `<xsl:evaluate>`
calling ordinary top-level stylesheet functions with XTDE3160. Found
in Docbook TNG (`fp:pi-from-list` and similar).

**7. LRE prefix map: rebuild per included module + record default-ns
elements.** `_elementPrefixMap` was built once for the entry stylesheet
but queried for ALL included modules — line/col entries from
`docbook.xsl` collided with positions in `head.xsl`, returning the wrong
prefix. Default-namespace elements (no prefix in source) had no entry
at all, so the fallback LINQ walk could return any ancestor's prefix
matching the namespace URI. Result: `<link>` LREs in the head module
were getting prefix `xsl:`, serialization couldn't reparse the chunk
(undeclared `xsl:` prefix), template body fell back to a raw string,
XTTE0505 fired. Fix: rebuild map per `LoadExternalStylesheet`; record
empty prefix for default-namespace elements so the lookup is
authoritative.

### Improvement: XQuery errors include the offending XPath text + module URI

XQuery exceptions raised from XSLT-embedded XPath now surface as
`XPTY0020: [file:///path/stylesheet.xsl:47] [line 2, col 24] An axis step …\n
↳ in expression (FunctionCallExpression): not(namespace-uri(/*) = ...)`.

The first prefix is the XSLT source file (stamped at parse time by
`StylesheetParser.AttachXsltSourceLocation`). The relative line/col
remains for pinpointing the position within multi-line inline XPath.
The expression text + AST type makes "needle-in-haystack" debugging of
real-world stylesheets actionable.

Bumps `PhoenixmlDb.XQuery` to 1.3.3.

Eight new regression tests across `AnyUriToStringCoercionTests`,
`VariableSequenceAccumulatorIsolationTests`, and
`DefaultNamespaceLrePrefixTests`. Full XSLT test suite at 364/364 passing.

## 1.3.3 (2026-05-09)

### Improvement: XSLT runtime errors carry module / line / column

Inherited from `PhoenixmlDb.XQuery` 1.3.2: any runtime `XQueryException` raised
during stylesheet execution now includes the originating module URI, line, and
column in its formatted `Message`, prefixed as `[<module>:<line>:<col>] `.
Particularly relevant for `XPTY0020` errors fired from axis steps in
multi-module stylesheets (the canonical case being Docbook TNG): the message
now pinpoints the offending step instead of leaving users to guess.

This release also bumps `PhoenixmlDb.Core` to 1.1.1 (additive
`IContainer.QueryAsync` overload — see core RELEASES.md).

No XSLT-side code changes — pure rebuild against the upstream fixes.

## 1.3.2 (2026-05-07)

### Fix: namespace fixup for serialized elements

When an XDM element selected from a variable (e.g. `<xsl:sequence
select="$var/node()"/>`) was serialized into a new context whose output
namespace scope did not include the element's prefix binding, the
serializer omitted the `xmlns:` declaration — the prefix had been
declared on an ancestor in the source tree, not on the element itself.
The serialized chunk then failed XmlReader re-parsing in `as=`-typed
template/variable bodies, the body fell back to a raw string, and the
type check raised `XTTE0505: ... return value item of type String does
not match declared type Element`.

Reported by Martin Honnen — Schxslt2's `transpile.xsl` `reduce-schema`
template raised XTTE0505 on `flowers.sch`. Schxslt2 transpile now runs
to completion.

Fix: `SerializeNode` emits the element's own prefix→URI binding when
neither `elem.NamespaceDeclarations` nor the output scope already
declares it — symmetric with the existing namespace fixup for
prefixed attributes.

## 1.3.1 (2026-05-07)

### Internal: `INodeBuilder.InternNamespace(uri, preferredId)` implementation

`InMemoryNodeStore` now implements the new `InternNamespace(uri, preferredId)`
overload added in `PhoenixmlDb.XQuery` 1.3.0, so namespace IDs assigned during
XQuery static analysis round-trip through the XSLT engine's in-memory store
when the engines are composed (e.g. `fn:transform`). No public API change;
internal class only.

## 1.3.0 (2026-05-07)

### Blazor WebAssembly support

The engine no longer throws `Cannot wait on monitors on this runtime` on Blazor
WebAssembly when a stylesheet imports another module over HTTP(S) or calls
`fn:doc()` on an absolute HTTP(S) URI. Reported by Martin Honnen.

Cause: the parser's `xsl:import`/`xsl:include` resolver and the runtime
`fn:doc()` document loader both went through synchronous-over-async
`HttpClient.GetAsync(...).GetAwaiter().GetResult()` calls. That works on
runtimes with a real thread pool (server, desktop, CLI) but blows up on
WASM, which is single-threaded and disallows monitor waits.

Fix: introduce `PhoenixmlDb.Xslt.PreloadedResources`, a small cache that the
host populates with pre-fetched contents before calling `LoadStylesheetAsync`.
The parser and the runtime document loader both consult this cache before
falling back to synchronous HTTP. On WASM, a cache miss now raises a clear
engine exception that names the missing URI, instead of the obscure runtime
error.

```csharp
// Blazor WebAssembly: pre-fetch every URI the stylesheet will need.
var http = new HttpClient();
var transpileXsl = await http.GetStringAsync("https://example.org/transpile.xsl");
var priceSch     = await http.GetStringAsync("https://example.org/price.sch");

var preloaded = new PreloadedResources();
preloaded.Add(new Uri("https://example.org/transpile.xsl"), transpileXsl);
preloaded.Add(new Uri("https://example.org/price.sch"),    priceSch);

var t = new XsltTransformer { PreloadedResources = preloaded };
await t.LoadStylesheetAsync(stylesheetXml, baseUri);
var result = await t.TransformAsync(sourceXml);
```

Server / desktop / CLI behavior is unchanged — the synchronous HTTP path
remains in place when running on a runtime that supports thread blocking.

### Performance: stream-parse `as=` body output

The transformation hot path for `xsl:variable as="element(…)"` and similar
typed bodies (Schxslt2 transpile, Dataverse-shape projections) no longer goes
through `XmlDocument.LoadXml` round-trips after each iteration. The body
output is now stream-parsed via `XmlReader` directly into XDM nodes,
eliminating the per-iteration DOM allocation that dominated wall time on
large source documents.

Bench result on a synthesized 10 MB Dataverse-shape XSD with the
`projection-style` stylesheet (`xsl:variable as="element(entry)" / xsl:where-populated`
inside a deep `xsl:for-each`):

| | Before | After |
|---|---|---|
| ProjectToReport (mean of 30 runs) | 4151 ms | 1593 ms |
| ProjectToReport (median) | — | 1597 ms |
| ProjectToReport (stddev) | — | 110 ms |

Net: 2.6× speedup on this shape. No measurable change on identity-copy and
LRE-only workloads (those don't go through the body parse-back path).

The bench harness gains a `micro` mode for reproducible A/B comparisons:

```
dotnet run --project bench/PhoenixmlDb.Xslt.Bench -c Release -- micro report 30 10
```

Reports min / median / p90 / max / mean / stddev across 30 iterations after
a 3-iteration warm-up.

## 1.2.10 (2026-05-06)

### HTTP(S) source documents and fn:doc fetches

Completes the HTTP coverage started in 1.2.9 — that release handled stylesheet
entry + imports, this one handles the source side:

```bash
xslt my-sheet.xsl https://example.com/data.xml
```

```xml
<!-- inside a stylesheet loaded over HTTPS -->
<xsl:variable name="cfg" select="doc('config.xml')"/>
<!-- relative URI resolves to https://.../config.xml and is fetched -->
```

Previously the CLI rejected an HTTPS source with "Source file not found" and
`fn:doc()` returned `()` (raising FODC0002 in stricter callers) when given an
HTTPS URI. Both paths now branch to a streaming HTTP fetcher with the same
30 s timeout and `User-Agent: PhoenixmlDb.Xslt` as `xsl:import`.

Bumps `PhoenixmlDb.XQuery` pin to 1.2.5 for the matching change in
`XdmDocumentStore` (the underlying document resolver).

Reported by Martin Honnen.

## 1.2.9 (2026-05-06)

### HTTP(S) stylesheet URLs and imports

The CLI and library now accept stylesheets and imports over HTTP/HTTPS:

```bash
xslt https://example.com/transform.xsl input.xml
```

```csharp
var transformer = new XsltTransformer();
await transformer.LoadStylesheetAsync(
    await client.GetStringAsync(xsltUrl),
    new Uri(xsltUrl));   // imports relative to this resolve over HTTPS
```

Previously `xsl:import href="lib.xsl"/>` against an HTTPS base URI raised
`XTSE0165: Cannot find stylesheet module 'lib.xsl'` because the parser only walked
`file://`. The CLI also rejected non-file paths up front with "Stylesheet not found."

Implementation: a static `HttpClient` (30 s timeout, `User-Agent: PhoenixmlDb.Xslt`)
is used for all HTTP fetches. When a `ResourcePolicy` is configured, its
`IsAllowed(uri, ImportStylesheet)` rule fires before the fetch — same gate as for
file imports — so callers can restrict to specific hosts / path prefixes.

Reported by Martin Honnen against the schxslt2 stylesheets hosted on github.io.

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
