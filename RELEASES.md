# Release History

## 1.3.20 (2026-05-20)

### `xslt` CLI `--timing` now reports memory (Martin Honnen)

Brings the `xslt` CLI to parity with the `xquery4` CLI's `--timing` output by
appending a `memory:` line with peak working-set bytes and total managed
allocations alongside the parse/compile/transform breakdown:

```
  parse:     12 ms
  compile:   47 ms
  transform: 134 ms
  memory:    peak=86.3 MB  allocated=1.2 GB
```

No library code changes from 1.3.19.

### CPM bump: PhoenixmlDb.XQuery 1.3.13 → 1.3.14

Picks up:

* `fn:load-xquery-module` closures evaluate body in *captured* static context,
  so transitively-imported functions (e.g. f1:foo invoked from f2:bar) resolve
  correctly when called from outer XSLT. Verified on Martin's
  `load-module-with-import1.xsl` repro.
* Library-module `decimal-format` declarations now propagate to the importing
  module's runtime `DecimalFormats`, fixing `FODF1280` for `format-number(…,
  "lib:euro")` inside lib:* functions.
* Three QT3 fixes: cross-module declaration visibility (#57), general
  comparison `untypedAtomic` → `xs:QName` cast (GenCompEq-22), and
  `element()` / `attribute()` kind-test EQName preservation
  (K2-DirectConElemNamespace-78).

## 1.3.19 (2026-05-20)

### `fn:transform` accepts `source-location` (Martin Honnen / XPath 4.0 draft)

Saxon's `fn:transform` accepts a `source-location` map entry as an alternative
to `source-node` — a URI string pointing at the principal input. This is also
in the [XPath 4.0 function draft](https://qt4cg.org/specifications/xpath-functions-40/Overview.html#func-transform)
and is what enables streamed transforms from XQuery without materialising the
whole input as an `XdmNode` first.

`XsltTransformProvider` now reads `source-location`, resolves the URI against
the caller's static base URI (relative URIs work the way they do in Saxon and
in the existing `stylesheet-location` branch), and feeds the loaded XML to
the transformer. Schemes handled:

- `file://` or any URI whose `IsFile` is true → `File.ReadAllTextAsync` on the
  local path.
- `http://` / `https://` → `HttpResourceLoader.GetStringAsync`. On Blazor
  WebAssembly we raise a clear `FOXT0001` instead of attempting blocking I/O.
- Bare relative paths with no static base → resolved against the current
  working directory, then read as a file.

Precedence: `source-node` beats `source-location` when both are supplied (the
spec is silent; this matches Saxon).

Two regression tests added (`XsltTransformProvider_source_location_with_file_uri_loads_input`
and `XsltTransformProvider_source_location_over_http_is_fetched`) — the HTTP
test spins up an in-process `HttpListener`.

## 1.3.18 (2026-05-19)

### `fn:transform` raw-delivery node results re-anchored in caller's store (Martin Honnen follow-up)

Closes three related symptoms Martin reported after 1.3.17 landed, all rooted
in the same lifecycle gap: `fn:transform` with `delivery-format='raw'` returned
`XdmNode` values whose `Children` / `Parent` `NodeId`s were allocated in the
inner XSLT engine's `XdmInMemoryStore` — which is discarded once the engine's
`TransformAsync` returns. From the caller's vantage:

- Subtree serialization stripped descendants: a returned element walked as
  `<root/>` instead of `<root>This is an example.</root>` because the outer
  store couldn't resolve the child text node's `NodeId`.
- `path()` walked a garbage ancestor chain like
  `/Q{…}schema[1]/@queryBinding/Q{}root[1]` because the returned element's
  `Parent` `NodeId` happened to collide with an unrelated node in the outer
  store (the `.sch` file's `@queryBinding`).
- Multi-hop `env:evaluate` (XQuery Schematron impl): the second hop's
  context-item appeared correctly typed but child navigation returned empty,
  so the report-test `. = 'This is an example.'` atomized to `""` and the
  `<svrl:successful-report>` was silently dropped.

Fix has two halves:

1. **Engine** — at the four places `RawResult.Value` / the engine's return is
   set on a raw-delivery path (InitialFunction in both `TransformAsync(string)`
   and `TransformRawAsync`, the JSON sequence-collection path, and the
   `rawItems` collection fallback), node items are now run through a new
   `WrapNodesForCrossStoreTransport` helper that uses
   `context.SerializeXdmNodeToXml(node)` to capture the subtree as XML while
   the inner store is still alive, then substitutes a `CrossStoreNodeRef`.
2. **`XsltTransformProvider`** — after `TransformToValueAsync` returns, a new
   `ReanchorCrossStoreResult` walks the value (handles bare wrappers and
   `object?[]` sequences), feeds each `CrossStoreNodeRef.Xml` through
   `ParseXmlFunction.ConvertToXdm(..., builder)` with the caller's
   `INodeBuilder` (the outer XQuery store), and substitutes the freshly
   re-anchored `XdmElement` / `XdmDocument` into `resultMap["output"]`.

Two non-fixed cosmetic issues observed in the same output: redundant
`xmlns:` declarations on every child element (Schematron-pipeline
serialization detail, separate item), and the existing `path()` form for
no-namespace elements (`/Q{}root[1]`) which is correct per XPath but visually
verbose compared to Saxon's `/root[1]`. Neither blocks Martin's workflow.

## 1.3.17 (2026-05-19)

### `fn:transform` HTTP `stylesheet-location` now fetched, not File.ReadAllText'd (Martin Honnen WASM repro)

`XsltTransformProvider` (the bridge used when XQuery's `fn:transform()`
calls into XSLT) only handled `file://` URIs in its stylesheet-location
branch. `http(s)://` URIs fell through to a fallback that
`Path.Combine`'d the URL onto the static base and called
`File.ReadAllTextAsync` — produced `FileNotFoundException` for
`https://host/…` (Path normalization collapses the double slash). The
in-engine `XsltTransformFunction` already had HTTP handling; this brings
the public `ITransformProvider` to parity. Blazor browsers still get a
clear "synchronous HTTP I/O is not supported" message instead of a
mangled path.

### `fn:transform` initial-function node arguments now re-anchored in inner store

When `fn:transform` is called with `function-params` that include
`XdmElement`/`XdmDocument` items from the outer XQuery store, the inner
XSLT engine couldn't navigate them — children/attributes resolve via
NodeIds that only exist in the originating store. The function-param
arrived typed correctly (`instance of element()` returned true) but
`name()` was the only thing that worked; `string-value`, axis steps,
and `xsl:evaluate`'s context-item were all empty.

Mirroring the in-engine path: serialize XdmElement/XdmDocument args to
XML and wrap as `CrossStoreNodeRef`. The receiving side
(`TranslateNodeArgumentsToLocalStore`) re-parses into its own
`XdmInMemoryStore` so XPath can walk the tree. Applied on both the
public `XsltTransformProvider.TransformAsync` path and the engine's
direct `CallXsltFunctionAsync` path at line 260 (which previously
passed `options.InitialFunctionArguments` straight through without
translation, leaving the wrapper visible to xsl:evaluate as an opaque
item that tripped XPTY0020).

### XTSE0080 diagnostic includes the function name

The reserved-namespace error now names the offending function:
`XTSE0080: The name of stylesheet function 'mf:evaluate' is in a reserved namespace`.

## 1.3.11 (2026-05-13)

### Fix: `fn:transform` from XQuery now honors `initial-function` + `function-params` (Martin Honnen report)

`XsltTransformProvider` (the bridge that lets XQuery's `fn:transform()`
drive an XSLT stylesheet) only read `initial-template` and `initial-mode`
from the options map — it ignored `initial-function` and
`function-params`. Calls like:

```xquery
transform(map {
  'stylesheet-node' : $xslt,
  'initial-function' : QName('http://example.com/mf', 'evaluate'),
  'function-params' : [$context, $expr],
  'delivery-format' : 'raw'
})?output
```

fell through to the default apply-templates path with no source document
and returned an empty `?output`, even though the underlying engine and
the `TransformToValueAsync` C# API both supported the call shape.

Fix: provider now reads `initial-function` (as `xs:QName`) and
`function-params` (as an array), wires them through
`SetInitialFunction` / `AddInitialFunctionArgument`, and the existing
`delivery-format='raw'` branch surfaces the typed result.

Regression test:
`XsltTransformProvider_honors_initial_function_with_raw_delivery_returning_boolean`
— invokes the provider directly with Martin's exact options shape and
asserts `?output` is `true` (typed `xs:boolean`), not empty.

XSLT suite 398/398 (was 397, +1 Martin regression).

## 1.3.10 (2026-05-13)

### Fix: `TransformToValueAsync` returns typed map/array from initial-template (Martin Honnen report)

`TransformToValueAsync` returned `null` for both map-producing
`initial-template` invocations and array-producing apply-templates runs,
even though the equivalent CLI commands produced the right JSON output.

Root cause: `ReturnRawXdm` was only honored by the `InitialFunction`
code path. For initial-template / apply-templates with output method
`json`/`adaptive`/`csv`, the engine's JSON serialization branch called
`EndSequenceCollection()` to grab the typed items, serialized them to
JSON text, and discarded the typed list — so `RawResult.Value` stayed
null and `TransformToValueAsync` returned its default null.

Fix: in the JSON-output branch, when `ReturnRawXdm` is set, capture
the typed items into `RawResult.Value` BEFORE serialization (single
item → that item; multi → `object?[]`; empty → `null`). Same shape
contract as the existing `InitialFunction` path.

Regression tests:
- `TransformToValueAsync_returns_typed_map_from_initial_template_with_json_output`
- `TransformToValueAsync_returns_typed_array_from_apply_templates_with_json_output`

### Source-location audit Phase D + E: actionable LSP diagnostics

Builds on the 1.3.9 Phase A/B/C foundation. Errors raised from XPath
embedded in XSLT now report file-absolute `(line, col)` pinned to the
offending token, not to the start of the containing XSLT element.
Required for an LSP server to squiggle the right span.

**D1 + D5 — file-absolute coordinates for embedded XPath**
- `ParseExpr` accepts an optional source `XAttribute`. When supplied,
  every parsed sub-expression's `SourceLocation` is shifted from
  XPath-relative to file-absolute via the new `WalkExpressions` post-order
  visitor. Multi-line XPath expressions are handled too: only the first
  XPath line gets the value-start column offset; subsequent lines use the
  file column directly (matches XML attribute-value continuation).
- 51 `ParseExpr` call sites in `StylesheetParser` now thread the source
  attribute (45 of the `xxxAttr.Value` form + 6 of the
  `element.Attribute("name")!.Value` form).

**D2 — AVT inner-expression positions**
- `ParseAvt` now takes an optional source attribute. Per-inner-expression
  base position is computed from `OffsetToLineColumn` (newline counter
  inside the AVT text) plus the AVT's value-start column. Every `{…}`
  inner XPath is parsed via the new `ParseExprAt(line, col, moduleUri)`
  overload so its sub-expression locations land at the brace, not the
  attribute start.
- 41 `ParseAvt` call sites threaded.

**D3 — TVT inner expressions in element text content**
- New `ParseAvtFromText` overload uses the first descendant `XText`'s
  IXmlLineInfo as the base position. Handles `<xsl:text expand-text="yes">`
  and other text-content TVT cases.
- Refactored: `ParseAvtCore(value, ctx, baseLine, baseCol, moduleUri)`
  is the shared backend; the attribute and text-content overloads
  feed into it.

**D4 — module-URI completeness verification**
- Already covered by D1's per-node Module stamping + the existing
  `LoadOptions.SetBaseUri` on import/include paths. Added regression test:
  errors raised from XPath inside an imported `modules/inc.xsl` carry
  the imported module's URI in `XQueryException.Module`, not the
  principal stylesheet's.

**D6 — streamability checker audit**
- All 12 `throw new XsltException(...)` sites in `StreamabilityChecker.cs`
  already pass `location`. No sweep needed.

**D7 + D8 — pulled in via new XQuery 1.3.6 dependency**
- `SourceLocation.Length` computed property and documented coordinate
  conventions; `XQueryException.RelatedLocations` for dual-location
  errors. See PhoenixmlDb.XQuery 1.3.6 release notes.

**Phase E — LSP-readiness verification suite**
- New `SourceLocationLspReadinessTests` (7 tests) covers each
  (XSLT element shape × error site) combination across D1-D5,
  asserting structural properties (expected source line, column past
  element start, correct module URI). Catches regressions in either
  the typed `Line`/`Column` properties or the formatted-message path.

XSLT suite 397/397. Bumps `PhoenixmlDb.XQuery` dep to 1.3.6.

## 1.3.9 (2026-05-12)

### Source-location audit: 122 runtime-error sites now carry `(module, line, col)`

Foundation for upcoming LSP work — XSLT side. Mirrors the 1.3.5 work in
`PhoenixmlDb.XQuery` (now required as 1.3.5).

**Infrastructure on `XsltExecutionContext`:**
- New virtual `PushInstructionLocation(SourceLocation)` and
  `PopInstructionLocation()` (default no-op). Mirrors the existing
  `PushVersion` / `PushCollation` / `PushStaticBaseUri` API.
- `DefaultXsltExecutionContext` overrides them with a real
  `Stack<SourceLocation?>`.
- `Error(msg)` and `Error(msg, inner)` factories on the runtime context
  that auto-attach the current instruction location to `XsltException`.

**Wiring:**
- `XsltSequenceConstructor.ExecuteAsync` now wraps every instruction's
  execution with `PushInstructionLocation` / `PopInstructionLocation`,
  alongside the existing version/collation/base-URI stacks. Covers the
  three execution paths: no-conditional fast path, on-empty single-pass,
  on-non-empty two-pass.

**Sweep:** 122 bare `throw new XsltException("...")` sites in
`DefaultXsltExecutionContext` converted to `throw Error("...")`. Only
the 1-arg sites without explicit location info were swept; multi-arg
sites that already pass an instruction's location were left alone, and
sites in static helpers (where the instance `Error` method isn't
reachable) were build-iteratively reverted.

**What this means for callers:** runtime errors raised by the XSLT
engine now populate `XsltException.Location` with the
currently-executing instruction's `(module, line, col)`. Existing
callers that don't read `Location` are unaffected.

**Compatibility:** purely additive. Sites not yet swept (deeper static
helpers, parser-side errors, streamability-checker errors) retain their
prior behavior. No public API changed.

Regression test:
`XsltException_carries_instruction_location_via_PushInstructionLocation`
(an `xsl:message terminate=yes` raises an exception whose `Location`
is set to the message instruction's source position).

## 1.3.8 (2026-05-12)

### Fix: `static-base-uri()` in a global variable now returns the declaring module's URI

When a global `xsl:variable` or `xsl:param` declared in an imported/included
module evaluated `static-base-uri()` (directly, or transitively via
`resolve-uri('foo.xml', static-base-uri())`), the engine returned the
**principal stylesheet's** URI instead of the URI of the module that
contained the declaration.

Discovered via Martin Honnen's Docbook xslTNG report on Windows. With the
URI form already corrected by 1.3.7, the file URI was well-formed but
pointed at the wrong directory:

```
WARNING: Can't get default templates from templates.xml:
  No document could be retrieved for URI 'file:///C:/.../xslt/templates.xml'.
```

`templates.xml` actually lives at `xslt/modules/templates.xml`. The
relevant code in `xslt/modules/templates.xsl`:

```xml
<xsl:variable name="vp:default-templates" as="element()*">
  <xsl:variable name="uri" as="xs:string" select="
      if (starts-with($default-templates-uri, '/')) then
        $default-templates-uri
      else
        resolve-uri($default-templates-uri, static-base-uri())"/>
  …
```

`$default-templates-uri` defaults to `'templates.xml'`, so the lookup
hinges on `static-base-uri()` returning the URI of `templates.xsl`
(`…/xslt/modules/`). The engine was returning `…/xslt/docbook.xsl` (the
import root), so `resolve-uri` produced `…/xslt/templates.xml` — wrong
directory by one level.

Fix: push the declaring module's URI onto the static-base-uri stack while
each global variable's / parameter's `select` or body is being evaluated.
Applies to both the dependency-ordered eager initialization path and the
lazy on-demand path used when `GetVariable` discovers a still-pending
global. `XsltParam` now carries a `BaseUri` (mirroring `XsltVariable`)
populated from the parser's effective base URI.

Regression test:
`Static_base_uri_in_global_var_returns_declaring_module_uri` (an imported
`modules/inc.xsl` whose global `static-base-uri()` is observed from the
principal stylesheet — must end with `/modules/inc.xsl`, not `/main.xsl`).

### Fix: template-default `xsl:param` body preserves typed items

When a template's `xsl:param` has a body default (no `select` attribute,
content like `<xsl:sequence select="…"/>`), the engine evaluated it via
the naive "execute body to output buffer, atomize the text" path. Any
typed item — node, map, array — was destroyed: the item serialized to
text, the text was wrapped as `xs:untypedAtomic`, and downstream code
expecting `as="map(*)"` / `as="element()"` / etc. failed `XPTY0020` on
the first axis step or map operation.

Three sites were affected:
- `ApplyTemplatesCoreAsync` (default-binding loop after with-param matching)
- `ExecuteMatchedTemplateAsync` (apply-imports / next-match dispatch)
- `CallTemplateAsync` (xsl:call-template)

All three now route through `EvaluateBodyContentToValueAsync` — the same
helper that fixed the `xsl:next-iteration` with-param case in 1.3.x. The
helper installs a fresh sequence accumulator around body execution so
`xsl:sequence` items are captured as typed values; literal text falls
through to the output buffer as before.

The `xsl:with-param` argument-side path (`EvaluateWithParamAsync`) was
already accumulator-isolated; this release just brings the matching
default-side paths in line with it.

Regression tests:
`Template_param_default_body_with_xsl_sequence_preserves_typed_value`
(map(*) default body),
`Template_param_default_body_emits_node_via_apply_templates`
(element() default body via apply-templates).

## 1.3.7 (2026-05-12)

### Windows: `static-base-uri()` no longer returns a bare drive path

`UriString` had a workaround for .NET's URI scheme mangling (e.g. `d://tests/`
gets normalized to `file:///d://tests/`). The detection — "scheme is file
AND original doesn't start with `file:` AND original contains `:`" — also
matched **Windows drive paths** (`C:\Users\…` or `C:/Users/…`), so on
Windows `static-base-uri()` returned the path form instead of the proper
`file:///C:/Users/…` URI.

Manifested in Martin's Docbook xslTNG run:
```
[fn:trace] localization-base-uri: C:/Users/marti/…/locale/
xsl:message: WARNING: Can't get default templates from templates.xml: 
  No document could be retrieved for URI 'C:/Users/marti/…/xslt/templates.xml'.
```

Fix: distinguish drive-letter paths (`[A-Za-z]:[/\]`) from non-standard
URI schemes by inspecting `OriginalString`. Drive paths now return the
canonical `AbsoluteUri` form (`file:///C:/...`); the existing handling
for non-standard schemes (`d://tests/` etc.) is preserved.

### New: chaining API — pass typed XDM values between transformations

Two new overloads on `XsltTransformer` let callers chain transformations
without round-tripping through serialized XML markup:

```csharp
// Run, get a typed sequence back (atomic, node, or mixed)
var step1 = await t1.TransformToSequenceAsync(input);

// Feed that sequence directly into the next transformer
var step2 = await t2.TransformAsync(step1);
```

The sequence carries the engine's `XdmInMemoryStore` so the receiving
transformer can navigate any node items without re-parsing.

**New public types**:

- `PhoenixmlDb.Xslt.XdmInMemoryStore` (promoted from internal
  `InMemoryNodeStore`) — implements `INodeBuilder` / `INodeStore` /
  `INodeProvider`. Lets external code construct or consume an XSLT
  engine's XDM tree.
- `PhoenixmlDb.Xdm.XdmSequence` (in `PhoenixmlDb.Core 1.1.2`) — Saxon-style
  ordered sequence wrapper that carries a backing store for its node
  items. See Core release notes.

**New facade methods**:

- `Task<string> TransformAsync(XdmSequence?)` — accept a sequence
  (single node, atomic value, or any sequence) as the principal source.
  Null/empty runs source-less.
- `Task<XdmSequence> TransformToSequenceAsync(XdmSequence?)` — return
  the typed XDM result wrapped in a sequence that carries the engine's
  store, ready to feed into another transformation.

**Implementation notes**:

- Backed by the engine's existing `TransformRawAsync` path, which sets
  up sequence collection across all invocation forms (initial-function,
  initial-template, initial-mode, default apply-templates) and preserves
  typed values where possible.
- For initial-template / default-mode invocations whose templates use
  plain LREs (no `xsl:document` wrapper, no `xsl:output method="adaptive"`),
  the engine still produces serialized markup — `TransformToSequenceAsync`
  re-parses that into a navigable `XdmDocument` so the receiving
  transformer sees a node, not a string.
- Defensive guard: `TransformAsync(XdmSequence)` throws
  `InvalidOperationException` if the sequence has node items but no
  matching store, instead of silently producing empty output.

Regression tests:
`TransformAsync_chains_XdmSequence_between_two_transformations`,
`TransformAsync_with_null_XdmSequence_runs_source_less`,
`TransformAsync_rejects_XdmSequence_with_node_but_no_store`,
`TransformToSequenceAsync_via_initial_function_preserves_atomic_value`.

Full XSLT suite 380/380. Docbook xslTNG samples + 5/7 test articles
unchanged in byte-for-byte output (this release is purely additive on
the public API; no engine-semantics changes).

## 1.3.6 (2026-05-12)

### Three Docbook xslTNG fixes — 5/7 test articles now pass (was 1/7)

After cutting 1.3.5, ran the full Docbook xslTNG 2.8.0 test corpus and
found three more bugs that were stopping every sample with a `<personname>`
in `db:info`. With these three landed, article.001/002/004/016/018 all
produce clean HTML; only article.003/005 still fail (different root
cause — string-vs-numeric general comparison).

**1) Global `xsl:variable as="element(name)"` binds to the element, not a doc.**

The global-var binding's `needsNodeOrphan` check required `ElementName == null`,
so NAMED element types (`element(l:l10n)`) skipped the sequence-accumulator
path and fell through to the no-as RTF construction. Downstream `$locale/l:group`
then looked for `l:group` children of the document wrapper and found none.

Found in Docbook chunk-cleanup name-style locale lookup. Removing the
`ElementName == null` guard routes named element types through the
accumulator like the unnamed `element()` form already did.

Regression test:
`Global_variable_with_named_element_type_binds_to_element_not_doc`.

**2) Local `xsl:variable as="xs:string?"` with empty body is the empty sequence.**

When the body executed (e.g. `xsl:for-each` over an empty selection)
but produced no items, the variable was bound to the empty STRING `""`
instead of the empty SEQUENCE `()`. `empty($style)` returned false,
masking the no-match path that should have fallen through to the
locale lookup. Found in Docbook info.xsl personname `name-style` chain.

Fix: in the no-`as=` body path, when content is empty AND no accumulator
items AND occurrence allows empty (`?` or `*`), bind to `null`.

Regression test:
`Local_variable_optional_atomic_with_empty_body_is_empty_sequence`.

**3) Function returning string for `as="xs:integer?"` casts at boundary.**

Saxon-compatible function-conversion: when a function's body produces a
string (typically via `xsl:number` or `xsl:value-of`) and the declared
return type is a strict atomic type (xs:integer, xs:double, etc.), cast
the string before final validation. Without this, the tightened XTTE0780
validator rejected the string return; Docbook `fp:number` (which routes
through `<xsl:number/>` in matched templates) failed on every numbered
article.

Fix: in `CallXsltFunctionAsync`, after every result-determination
branch converges, attempt `TryCoerceStringToType` for string-to-atomic
when the declared type is a strict atomic. Sequences are coerced
element-wise.

Regression test:
`Function_return_string_coerces_to_declared_atomic_type`.

Full XSLT suite 376/376 (was 373); Docbook xslTNG 2.8.0 cleanly handles
samples/article.xml plus 5 of 7 test corpus articles.

## 1.3.5 (2026-05-11)

### `as=` validator now enforces element name AND namespace

Hardening pass on the function/template return validators after a multi-day
Docbook conformance push surfaced three separate bug shapes (xsl:break,
xsl:copy copy-namespaces="no", function-with-text-body) where the engine
produced a *value* of the wrong XDM shape and the validator let it
through, only to blow up downstream as XPTY0020 axis-step errors.

**Changes**:
- `ValidateValueMatchesType` (used by function returns and with-params)
  now checks element NAME and NAMESPACE against `element(Q{ns}local)`,
  not just "is it a node?". Same treatment for attribute names and
  processing-instruction targets.
- `ValidateTemplateReturnType`'s element-name matcher now also checks
  the declared namespace, not only the local part.
- `MatchesElementName` is namespace-aware (was: local-name only).
- `CallXsltFunctionAsync` calls `ValidateFunctionReturnType` once after
  every result-determination branch converges. Two branches (text-output
  parse-to-XDM and combined accumulator+text) previously skipped
  validation entirely, which is how the wrong-shape values leaked out.
- StylesheetParser's `element(...)` / `attribute(...)` / `document-node(element(...))`
  type-name parsers now route through `SplitPrefixedName`, which
  correctly handles EQName syntax (`Q{ns}local`). The earlier
  split-on-`:` swallowed any `:` inside the namespace URI — turning
  `Q{urn:expected}root` into `ElementName="expected}root"` with no
  namespace recorded, so the validator had nothing to check against.

Errors now look like:
`XTTE0780: Function f:wrong-ns return value requires type
Element(Q{urn:expected}root) but got element root in namespace ""`

Regression tests:
`XsltTransformerIntegrationTests.Function_return_validator_rejects_wrong_namespace_element`
and `…_accepts_correct_namespace_element`.

Full XSLT suite 373/373; Docbook xslTNG 2.8.0 still runs end-to-end with
identical output (proving the fix doesn't change correct-stylesheet
behavior, only catches incorrect ones earlier).

### `xsl:copy copy-namespaces="no"` preserves the element's own namespace

Per XSLT 3.0 §11.10.1 + §5.7.3.4 (namespace fixup), `copy-namespaces="no"`
lets the engine drop the source's *additional* namespace bindings, but
the binding that defines the copy's *own* namespace must be preserved —
otherwise the copy ends up in the wrong namespace and downstream
path-step matchers can no longer find it.

Our implementation skipped the entire namespace-bindings block when
`copy-namespaces="no"`, dumping default-namespaced elements into the
null namespace. Found in Docbook `mp:remove-ghosts` (whose only job is
stripping `@ghost:*` attributes), which silently re-namespaced every
xhtml element and broke the entire chunk-output dispatch — `/h:html/h:html`
matched zero nodes, so the engine fell back to "sequence the whole
input", producing a doubled `<html><html>…</html></html>` output.

Fix: when `copy-namespaces="no"`, still register the element's own
namespace binding before namespace fixup runs.

Regression test:
`XsltTransformerIntegrationTests.Copy_with_copy_namespaces_no_preserves_elements_own_namespace`.

### HTML serializer no longer duplicates `Content-Type` meta

Per XSLT 3.0 §27.6.4 (HTML/XHTML output): the serializer adds a
`<meta http-equiv="Content-Type" …>` only if one isn't already present in
the head. `InsertContentTypeMeta` inserted unconditionally, so a
stylesheet that emitted its own meta (e.g. Docbook TNG's XHTML-style
`<meta http-equiv="Content-Type" content="text/html; charset=utf-8" />`)
ended up with two of them.

Fix: scan the head's content for an existing `Content-Type` meta tag
before inserting, and skip the insertion when one is found.

Regression test:
`XsltTransformerIntegrationTests.Html_serializer_skips_duplicate_content_type_meta`.

### `xsl:function as="node()*"` wraps plain-text body output as text node

`CallXsltFunctionAsync`'s text-output branch only parsed function output
into XDM nodes when the captured text contained `<` (i.e. XML markup).
For functions declared `as="node()*"` whose body produced plain text via
`<xsl:apply-templates/>` over text-only content, the function returned
the raw string instead of a text node — and the caller's
`descendant-or-self::text()` axis then raised XPTY0020.

Found in Docbook chunk-cleanup `f:chunk-title` (`as="node()*"`).

Fix: also enter the parse-to-XDM branch when the function's declared
return is a node type, regardless of `<`. With this, an apply-templates
result that's just text becomes a single text node, satisfying both the
`as=` constraint and downstream `text()` axis access.

Regression test:
`XsltTransformerIntegrationTests.Function_with_node_return_type_wraps_text_body_as_text_node`.

**End-to-end milestone**: with this fix, Docbook xslTNG 2.8.0 runs
end-to-end against `samples/article.xml` and produces HTML output
(exit 0). Several smaller post-rendering issues remain (a duplicated
`<html>` wrapper, etc.), tracked separately.

### `xsl:break select="X"` preserves typed/node values across the iteration boundary

`xsl:break` always atomized its `select` value to a string, because `Break`
called the simple `OutputValue` helper (which routes through `StringValueOf`).
Inside an `xsl:function` declared `as="element()?"`, an iterate that
broke with `select="."` returned the matched element's *string value*
rather than the element itself. Discovered while transpiling Docbook
xform-locale.xsl: `fp:lookup-localization-template` returned a whitespace
string instead of the matched `l:template` element, breaking every
downstream `$template/lt:label` access.

Fix: `Break` now mirrors `xsl:sequence` semantics — when a sequence
accumulator is active, append items to it; otherwise serialize via
`SerializeResult` (which preserves nodes as nodes).

Regression test:
`XsltTransformerIntegrationTests.Iterate_break_select_dot_returns_element_not_string_value`.

### `xsl:copy-of` of a namespace node emits an `xmlns:` declaration, not text

`SerializeNode` lacked a case for `XdmNamespace`, so namespace nodes from
the `namespace::*` axis fell through to the `default` branch and were
serialized as text — `WriteText(node.ToString())` emits the namespace
URI as element content. Found while transpiling Docbook xform-locale.xsl,
whose `<xsl:copy-of select="@*,namespace::*[…]"/>` poisoned every
generated `l:template` with a leading `"http://docbook.org/ns/docbook"`
text node, which then dominated the element's string value.

Fix: added an `XdmNamespace` case to `SerializeNode` that emits an
`xmlns[:prefix]="…"` declaration on the containing element (writing into
`_collectedAttributes` while the start tag is still open). Skips the
implicit `xml` prefix and any binding already in scope. Throws XTDE0440
when copying a namespace node onto a document, and XTDE0410 when copying
after the start tag has been closed.

Regression test:
`XsltTransformerIntegrationTests.CopyOf_namespace_node_emits_xmlns_declaration_not_text`.

### Untyped `xsl:variable` captures typed-template results from `xsl:apply-templates`

Martin's SchXslt2 report: importing `transpile.xsl`, the wrapper

```xml
<xsl:variable name="transpiled-schematron">
  <xsl:apply-templates select="doc($schema-uri)/node()"/>
</xsl:variable>
```

ended up empty, so the downstream `fn:transform` raised `FOXT0001:
Stylesheet node has no content`. Saxon produced the transpiled
stylesheet correctly.

**Root cause.** `xsl:variable` with no `as=` constructs a result-tree
fragment by reading the engine's text buffer. The variable also installs
a fresh sequence accumulator as a leak barrier (so `xsl:sequence` inside
doesn't escape to a parent function's accumulator — see 1.3.4 fix #3).
But the matched template inside used `as="element(...)"`, and that path
routes its validated result into the accumulator instead of writing
serialized XML to the buffer. The RTF construction never read the
accumulator, so the typed result was silently discarded.

**Fix.** After the body of the no-`as=` `xsl:variable` runs, drain any
captured accumulator items into the redirected output buffer (via
`SerializeResult`) before the buffer is read. The accumulator stays as a
barrier for `xsl:sequence`-inside-function correctness; what changes is
that items captured by the barrier now flow into the RTF instead of
being thrown away.

Regression test:
`XsltTransformerIntegrationTests.Untyped_variable_captures_typed_template_result_via_apply_templates`.

### `fn:transform` with `delivery-format='raw'` returns the typed XDM value

Martin reported that `fn:transform(...)?output` came back empty when the
called stylesheet used `initial-function` returning `xs:boolean` (e.g.
from `xsl:evaluate`). Both `'document'` and `'serialized'` delivery
formats produced empty output, because the boolean was being routed
through the result-document serializer — `"true"`/`"false"` doesn't
re-parse to a useful XDM document.

**Fix.** Added a raw-value return path that bypasses the serializer
entirely when the caller asks for typed output:

- `XsltTransformOptions.ReturnRawXdm` + `RawResultBox` — engine writes
  the initial-function's return value into the box instead of feeding
  it to the output serializer.
- `XsltTransformer.TransformToValueAsync(string?)` — public façade
  returning `Task<object?>` (single item, `object?[]` for sequences,
  `null` for empty).
- `XsltTransformProvider` (XQuery's `fn:transform` adapter) calls the
  new façade when `delivery-format='raw'`. `'document'` and
  `'serialized'` are unchanged.

Honored only on the `initial-function` path, where the transformation
has a single well-defined return value. Template-based invocations
still go through document serialization (the spec doesn't define a
typed-value semantics for them).

Regression test:
`XsltTransformerIntegrationTests.TransformToValueAsync_returns_typed_boolean_from_initial_function`.

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
