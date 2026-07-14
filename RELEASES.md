# Release History

## Unreleased

### Serialization

- **`xsl:result-document` targeting the principal output now honours its own `standalone`, `output-version`, and XML-declaration settings.** An href-less `xsl:result-document` claims the primary output, but its serialization attributes were being dropped in favour of the (often absent) stylesheet-level `xsl:output`: `standalone="yes"`/`standalone="no"` never reached the XML declaration (so `<?xml … standalone="yes"?>` was emitted without the `standalone` pseudo-attribute), `output-version` was neither parsed nor applied, and a bare `<xsl:result-document/>` with no attributes emitted no XML declaration at all (falling through with `null` output declaration). The result-document now builds a synthetic output declaration even when it names no `method` (defaulting to the `xml` method so the declaration is still emitted), carrying its evaluated `standalone` (with `standalone="omit"` correctly suppressing the pseudo-attribute) and `output-version`; the same values also flow through the secondary (href) result-document path. `omit-xml-declaration="yes"` still suppresses the declaration, and the SEPM0009/SEPM0004 conflict checks are unaffected (`insn/result-document/result-document-0206`, `-0229`, `-0230`, `-0231`, `-0234`).
- **`xsl:result-document` now honours its own `byte-order-mark` and `escape-uri-attributes` serialization attributes.** Both attributes were validated at parse time but never captured into the result-document AST, so they were silently dropped on the instruction: a truthy `byte-order-mark` (`yes`/`true`/`1`, with surrounding whitespace tolerated, or an AVT that evaluates to one) emitted no leading U+FEFF, and `escape-uri-attributes="no"` still percent-encoded non-ASCII characters in URI-valued `html`/`xhtml` attributes. The instruction now parses both (as AVTs), threads them into the synthetic principal-output declaration and the secondary (href) result-document declaration, and lets the shared `FinalizeOutput` pipeline apply them — so a BOM is prepended when requested and `escape-uri-attributes="no"` leaves a non-ASCII `href` untouched. The result-document value wins over the referenced `xsl:output`, and `byte-order-mark="no"` still suppresses the BOM (`insn/result-document/result-document-0256`, `-0258`, `-0260`, `-1203`, `-0264`, `-0266`, `-0268`).
- **`xsl:result-document` now honours its own `cdata-section-elements` serialization attribute.** The attribute was never captured into the result-document AST, so the named elements' text was normally escaped (`a &amp; b`) instead of being wrapped in a CDATA section (`<![CDATA[a & b]]>`). The instruction now parses `cdata-section-elements` (as an AVT, per XSLT — `result-document-0401` uses `cdata-section-elements="{foo[1]} my:{elem} {item}"`), evaluates it at run time, splits it on whitespace and resolves each token to a QName against the result-document element's in-scope namespaces (a prefixed name against its prefix, an unprefixed name against the default namespace). The effective set is the **union** of the result-document's own list and the `cdata-section-elements` of the referenced `xsl:output` (`format=`), and is applied at content-emission time so an element whose expanded name matches is CDATA-wrapped — including the encoding-aware splitting around unrepresentable characters that the principal path already performs (`insn/result-document/result-document-0217`, `-0240`, `-0401`).
- **The `html` output method on a principal `xsl:result-document` now applies the full HTML serialization.** An href-less `xsl:result-document` with `method="html"` (or defaulted to html by the default-output-method rules) claims the principal output and routes through the shared `FinalizeOutput` pipeline, but two HTML specifics were still wrong. First, `media-type` and `include-content-type` were neither parsed off `xsl:result-document` nor carried into its synthetic output declaration, so the injected `<meta http-equiv="Content-Type">` always used `text/html` instead of the requested media type — `media-type="application/xhtml-xml"` now yields `content="application/xhtml-xml; charset=UTF-8"`, and an existing head Content-Type meta is replaced with the computed value. Second, an empty non-void element such as `<title/>` (expanded to `<title></title>`) was split across lines by the HTML indenter (`<title>`⏎`  </title>`); an empty block element now serializes inline as `<title></title>`, with no indentation whitespace inserted between the start and end tags. The empty-element inline rule applies to all `html`-method output, principal and result-document alike (`insn/result-document/result-document-0209`, `-0214`, `-0223`, `-0224`).
- **`xsl:result-document` now honours its own `html-version` serialization attribute, emitting the HTML5 DOCTYPE for the `xhtml` (and `html`) output methods.** The attribute was validated at parse time but never captured into the result-document AST, so an href-less `xsl:result-document method="xhtml" html-version="5"` (which claims the principal output and routes through the shared `FinalizeOutput` pipeline) dropped the value and emitted no `<!DOCTYPE html>`. The instruction now parses `html-version` (as an AVT — `result-document-0244` supplies it dynamically via `html-version="{$param}"`), evaluates it at run time, and threads it into the synthetic principal-output declaration and the secondary (href) result-document declaration; the shared serializer's existing `html-version >= 5.0` rule then emits the HTML5 DOCTYPE for both the `html` and `xhtml` methods. The result-document value wins over the referenced `xsl:output` (`insn/result-document/result-document-0242`, `-0244`).
- **`item-separator` is now inserted around comment and processing-instruction nodes, not only between adjacent atomic values.** When an explicit (non-absent) `item-separator` is in effect on the top level of a result sequence (an `xsl:result-document`, or the principal output), §5.7.2 sequence normalization inserts a copy of the separator between *every* pair of adjacent items — including where a comment or PI abuts an atomic value or another node. Previously the separator was only emitted between adjacent atomic values (via the atomic-adjacency tracking), so a top-level sequence such as `<xsl:comment>start</xsl:comment>` `(11,…,15)` `<xsl:comment>middle</xsl:comment>` … serialized as `<!--start-->11~…~15<!--middle-->…` with no separator around the comments; it now serializes as `<!--start-->~11~…~15~<!--middle-->~…`. Separation is scoped to the top level of the result sequence (not element children, attribute content, or comment/PI bodies), and the legacy single-space default when no `item-separator` is present is unchanged (`insn/result-document/result-document-1408`, `-1409`, `-1410`).
- **A named `xsl:output` no longer seeds the principal result sequence's `item-separator`, and an href-less `xsl:result-document` with no `format` now uses the unnamed (default) `xsl:output`.** Two href-less `xsl:result-document` defects on the principal-output resolution path are fixed. First, the principal result sequence's §5.7.2 `item-separator` was seeded from `Outputs.FirstOrDefault()`, which picks up a *named* output definition when that is the stylesheet's only `xsl:output` — so a named `<xsl:output name="f" item-separator="|"/>` wrongly injected `|` between the principal sequence's items even though a named output is only ever referenced via `@format`. The seed now comes solely from the unnamed (principal) `xsl:output`, so a result-document overriding with `item-separator="#absent"` correctly resets to the default single space (`<!--begin-->1 2<!--end-->`, not `<!--begin-->|1|2|<!--end-->`). Second, an href-less `xsl:result-document` with no `format` attribute ignored the unnamed `xsl:output` declaration and defaulted to `method="xml"`; it now inherits the unnamed output's serialization parameters, so an unnamed `<xsl:output method="text"/>` makes the result-document serialize as text (string value only, no XML declaration or element markup) (`insn/result-document/result-document-0305`, `-0202`).

## 1.4.24 (2026-07-14)

A focused output-serialization release: the HTML, XHTML, and JSON output methods and the serialization parameters. Requires PhoenixmlDb.Core 1.2.2 and PhoenixmlDb.XQuery 1.5.5. No breaking API changes.

### Serialization

- **URI-attribute escaping now works for the `xhtml` output method and honours `escape-uri-attributes="no"`.** Under the `html`/`xhtml` methods a URI-valued attribute (`href`, `src`, `cite`, `action`, `data`, `formaction`, `poster`, `srcset`, `usemap`) is percent-encoded when `escape-uri-attributes` is `yes` (the default): every character outside printable ASCII — including one the serializer emitted as a numeric character reference, such as U+0096 written as `&#x96;` — is NFC-normalized and emitted as its UTF-8 octets (`href="&#x96;"` → `href="%C2%96"`), while an existing `%xx` sequence is left untouched (no double-encoding) and the XML-significant characters `<`, `>`, `&`, `"` stay XML-escaped. Two defects are fixed: the `escape-uri-attributes` parameter was never read from `xsl:output`, so `escape-uri-attributes="no"` was silently ignored and non-ASCII was percent-encoded regardless (it now leaves the value to normal serialization, e.g. `href="¡"`); and the XHTML path left character-reference-encoded non-ASCII unescaped instead of percent-encoding it (`output-0102a`, `output-0102b`, `output-0102c`, `output-0103a`, `output-0103b`, `output-0141`/`0141a`/`0141b`). A bare `"` inside a URI-valued attribute is emitted as the numeric character reference `&#34;` rather than the named entity `&quot;`, even under `escape-uri-attributes="no"` — this is scoped to URI attributes only (an ordinary attribute's `"` still serializes as `&quot;`), so under `escape-uri-attributes="no"` the URI-attribute pass runs solely to re-express `&quot;` as `&#34;` while leaving `%xx` sequences, character references, and non-ASCII untouched (`output-0103c`).
- **The HTML/XHTML empty (void) element set now includes the classic HTML 4.01 / XHTML 1.0 elements `basefont`, `frame`, and `isindex`.** The `html`/`xhtml` output methods recognise the union of the classic empty set (`area, base, basefont, br, col, frame, hr, img, input, isindex, link, meta, param`) and the HTML5 additions (`embed, source, track, wbr`), matched case-insensitively. Previously only the HTML5 set was recognised, so `basefont`, `frame`, and `isindex` were serialized with an end tag (`<basefont></basefont>`) instead of as empty elements (`<basefont>` for `html`, `<basefont />` for `xhtml`). Non-void empty elements still expand to a start+end tag pair (`output-0116`, `output-0116a`, `output-0116b`).
- **`cdata-section-elements` now matches result elements by expanded QName (namespace-aware).** A `cdata-section-elements` list on `xsl:output` is a list of QNames: an unprefixed name is resolved against the default namespace in scope on `xsl:output` (XSLT 2.0+), and a prefixed name against its prefix's namespace. A literal result element (or `xsl:element`) whose *expanded* name matches a list entry now has its text wrapped in a CDATA section regardless of the prefix actually used — so with `cdata-section-elements="h1 my:h3 h5"` under `xmlns="…xhtml"` and two prefixes (`my`, `one`) bound to the same namespace, both `<my:h3>` and `<one:h3>` are CDATA-wrapped while an unprefixed `<h3>` in the xhtml default namespace is not. Previously the element name was pushed with its namespace discarded and unprefixed list names never got the default namespace, so matching degraded to a namespace-blind local-name compare that silently missed every prefixed case (`output-0138`).
- **A CDATA section splits around characters the target encoding cannot represent.** For a `cdata-section-elements` element serialized under an encoding that cannot represent some of its text (e.g. `ª` U+00AA or `ç` U+00E7 under `US-ASCII`), the character can no longer be written literally inside the CDATA section; the section now splits around it and the character is emitted as a numeric character reference outside the CDATA — `<![CDATA[foo ]]>&#170;<![CDATA[ bar]]>`. Character maps still do not apply inside CDATA (so a mapped-but-unrepresentable character is emitted as an NCR, not its mapping), and Unicode normalization runs first (an NFD-decomposed base character stays inside the CDATA while its combining mark is referenced out). Representable characters — including everything under UTF-8 — stay verbatim inside the section. Mirrors PhoenixmlDb.XQuery's encoding-aware CDATA splitting (`output-0115b`, `output-0115c`, `output-0115d`, `output-0115e`).
- **`suppress-indentation` no longer hangs on element text content (and preserves significant whitespace).** With `indent="yes"` and `suppress-indentation` naming an element that contains text (e.g. `suppress-indentation="p"` over a long `<p>Lorem … laborum.</p>`), the post-indentation suppressor entered an infinite loop on the first non-whitespace character inside the suppressed element, so the `html`/`xhtml`/`xml` methods never returned. The suppressor now advances over text content correctly and runs in linear time, and when it removes the indentation the indenter inserted before a suppressed element's closing tag it strips only the inserted `\n`+indent, preserving any significant trailing whitespace of the source content (`output-0725` [html], `output-0726` [xhtml]; also unblocks the mixed-content `output-0232` [xml]).
- **HTML5 DOCTYPE for the `html`/`xhtml` output methods.** When `html-version` is 5 or greater and the document element is the `html` element, serialization now emits an HTML5 `<!DOCTYPE …>` immediately before the document element (after any XML declaration and any leading comment/PI). The doctype name preserves the element's exact spelling — `<HTML>` yields `<!DOCTYPE HTML>`, `<HtMl>` yields `<!DOCTYPE HtMl>` — and it is emitted even when only `doctype-public` (not `doctype-system`) is set (W3C bug 20264 ruling). No DOCTYPE is emitted when the document element is not `html` (e.g. a `<body>` or `<input>` root) (`output-0208`…`0210`, `0212`, `0229`, `0233`).
- **Foreign-namespace XHTML documents serialize by XML rules.** For the `xhtml` output method, a document element in a namespace other than `http://www.w3.org/1999/xhtml` is treated as foreign: no HTML empty-element handling, no injected Content-Type `<meta>`, and no HTML5 DOCTYPE (`output-0214`). A document element in no namespace keeps the previous lenient handling.
- **The `item-separator` serialization parameter on the principal `xsl:output` is now honoured.** During §5.7.2 sequence normalization, a specified (non-absent) `item-separator` is inserted between every pair of adjacent items of the result sequence with no extra whitespace — so `item-separator="~"` over `(11,…,20)` serializes as `11~12~…~20`, and a whitespace or newline separator is used verbatim. When `item-separator` is absent, adjacent atomic values keep the legacy single-space default. Previously the declared separator was ignored and a single space was always used (`output-0703`, `output-0709`, `output-0718`, `output-0719`).
- **`script` and `style` content is now raw text (unescaped) under the `html` output method.** Per XSLT/XQuery Serialization 3.0, `script` and `style` are CDATA (raw-text) elements in HTML: their text content is emitted verbatim, so `<`, `>`, and `&` stay literal (e.g. `<script>document.write("<EM>…</EM>")</script>` no longer becomes `&lt;EM&gt;…&lt;/EM&gt;`). Element-name matching is case-insensitive (`SCRIPT`/`Style`). This applies only to the `html` method and only to no-namespace elements — the `xhtml` method still serializes `script`/`style` by XML rules, and ordinary elements still XML-escape their content (`output-0154`, `output-0159`).
- **DOCTYPE identifier quoting and empty-`doctype-system` handling.** A `doctype-public`/`doctype-system` identifier that contains a `"` is now delimited with single quotes so the emitted declaration stays well-formed — `doctype-system` `ABC"DEF` serializes as `'ABC"DEF'` rather than a `"`-broken `"ABC"DEF"` (identifiers without a `"` keep double quotes) (`output-0311`). A zero-length `doctype-system` value is treated as if the parameter were absent, so no `<!DOCTYPE>` is emitted; this lets an empty `doctype-system` (from a higher-import-precedence `xsl:output`, or from `xsl:result-document doctype-system=""`) override an inherited non-empty value back to "none" (Serialization erratum E31; `output-0312`, `output-0313`). `xsl:result-document` now also reads the `doctype-public`/`doctype-system` attributes (AVTs) and applies them over the referenced `xsl:output`.
- **XHTML default-namespace (prefix-stripping) serialization for HTML5 content namespaces.** Under the `xhtml` output method, an element in one of the HTML5 content namespaces — XHTML (`http://www.w3.org/1999/xhtml`), SVG (`http://www.w3.org/2000/svg`), or MathML (`http://www.w3.org/1998/Math/MathML`) — that is bound via a namespace *prefix* in the result tree is now serialized in the conventional default-namespace form: the element name is emitted as its local name with the namespace declared as the default `xmlns` (dropping the now-unused `xmlns:prefix` declaration), and the HTML5 DOCTYPE uses the local name. So a `<h:html xmlns:h="…xhtml">…<s:svg xmlns:s="…svg">` result serializes as `<!DOCTYPE html>` / `<html xmlns="…xhtml">…<svg xmlns="…svg">` with no `h:`/`s:` prefixes. Foreign-namespace elements keep their prefixes (XML rules), an element already using the default namespace is left untouched, and the `xml` output method is unaffected (`output-0211`, `output-0221`, `output-0225`, `output-0226`).
- **The `html` output method now performs the same HTML5-content prefix normalization as `xhtml`, including on ancestor and attribute declarations.** Under the `html` method (with `html-version="5.0"`), a prefixed SVG/MathML/XHTML element is re-expressed with a default `xmlns` and its prefix dropped, and — matching the W3C decl/output ruling (MHK 2019-04-11, "treat the XHTML rules as definitive; the HTML spec is inadequate here") — the namespace declarations for those three namespaces are removed wherever they appear, including on an *ancestor* element such as `<body xmlns:svg="…">` (previously the ancestor `xmlns:svg` survived). A prefixed attribute in one of the three namespaces keeps its prefix (attributes have no default namespace) and its `xmlns:prefix` declaration is re-established on the owning element, following the attributes. Foreign (non-HTML5) namespaces keep their prefixes and declarations, and plain HTML output with no foreign namespaces is unchanged (`output-0602a`, `output-0602b`, `output-0603a`).
- **A whitespace-padded `method` value on `xsl:output` is normalized.** The `method` token is now trimmed before matching, so `method=" xhtml "` (or any padded built-in method) selects the intended output method instead of silently degrading to `xml`. Validation already trimmed the value; the mapping now does too (`output-0221`).
- **The default output method now resolves from an `html` document element (Serialization 4.0 §Default Output Method).** When `xsl:output` specifies no explicit `method`, a serialized non-streamed result whose document element is named `html` (case-insensitive) now selects the `html` output method (the `html` element in no namespace) or the `xhtml` output method (the `html` element in the XHTML namespace) instead of falling back to `xml`. That in turn applies the resolved method's serialization: the injected Content-Type `<meta http-equiv="Content-Type" content="text/html; charset=…">` as the first child of `<head>`, the HTML5 DOCTYPE, the URI-attribute escaping, and the `html`/`xhtml` indentation default (`output-0715`, `output-0130`). An explicit `method` always wins, and a document element that is not `html` — or an `html` in a foreign namespace — keeps the `xml` default. The resolution is deliberately confined to the normal non-streamed primary result: the streamed / for-each-group buffered serialization path is unchanged, so streamed `html`-rooted output keeps its prior serialization byte-for-byte.
- **A NODE serialized inside JSON now honours `json-node-output-method` (XSLT 3.0 §26.1).** When the `json` output method serializes a sequence that contains a node and `json-node-output-method="html"` (or `"xhtml"`) is in effect, the node is now serialized using that output method — producing HTML/XHTML markup (void-element handling and the injected Content-Type `<meta http-equiv="Content-Type" content="text/html; charset=…">` as the first child of `<head>`) which becomes the JSON string value — rather than being reduced to the node's string-value. The default `json-node-output-method="xml"` still serializes the node as XML markup, and a non-node atomic value is unaffected (`output-0716`). Nodes captured as map-entry LRE content are pre-serialized to a markup string during construction and remain outside this path (`output-0702` is unchanged).
- **The `parameter-document` attribute on `xsl:output` is now read and applied (XSLT 3.0 §26.1).** When `xsl:output` (named or unnamed) carries `parameter-document="…"`, the referenced `output:serialization-parameters` document (namespace `http://www.w3.org/2010/xslt-xquery-serialization`) is loaded and its serialization parameters are merged in — simple `<output:…value="…"/>` parameters (e.g. `method`, `omit-xml-declaration`) fill in any attribute not written directly on the `xsl:output`, and inline `<output:use-character-maps><output:character-map character="…" map-string="…"/></output:use-character-maps>` character maps are applied at serialization time. The document href is resolved against the base URI of the module that declared the `xsl:output`, so a parameter document sitting beside an `xsl:include`d module in a subdirectory resolves correctly. Previously the attribute was ignored entirely, so the requested method and character maps had no effect (`output-0706`, `output-0706a`, `output-0720`, `output-0722`).

## 1.4.23 (2026-07-12)

A large conformance release across pattern matching, modes and accumulators, XSLT 3.0 packages, `xsl:copy` namespaces, and XPath string literals. Requires PhoenixmlDb.Core 1.2.2 and PhoenixmlDb.XQuery 1.5.5 (for the inherited-namespace `namespace::` axis and raw-`&` XPath string literals). No breaking API changes.

### Patterns

- **Chained predicates in a match pattern now use XPath filter semantics.** A pattern with two or more predicates — e.g. `match="x[(position() mod 2)=1][position() &gt; 3]"` — now applies each predicate to the sequence that survived the earlier predicates, so `position()`/`last()` in a later predicate re-index against the survivors. Previously every predicate was tested against a single position, so a chained positional pattern matched the wrong nodes (`match-021`…`match-028`). Single-predicate patterns are unchanged.
- **More pattern forms now match.** `union`/`intersect` as pattern operators are disambiguated from a leading lone `/` (`/ union /*`); `child::document-node()`; a variable-reference pattern carrying a predicate (`$v[@att1='a']`); a parenthesized pattern with a positional predicate (`(doc/descendant::foo)[2]`); `root()` (matching any root — a document node or a parentless element) with a predicate (`root()[self::A]`); and XPath comments or whitespace inside a parenthesized pattern.

### Modes

- **`initial-mode="#default"` selects the declared default mode.** `#default` now resolves to the mode named by the package/stylesheet `default-mode` (rather than collapsing to the unnamed mode); `#unnamed` selects the unnamed mode. A package-level `xsl:mode` with no explicit visibility is private and is not an eligible initial mode unless the package's `@default-mode` names it (**XTDE0045**).
- **Mode-scoped accumulators are evaluated on the streamed initial-mode path.** When a streamable initial mode's `match="/"` template reads `accumulator-after('…')` against the document node, the mode's applicable accumulators (`use-accumulators`) are now evaluated over the tree instead of returning their initial value. Reading an accumulator that is not applicable to the initial mode's source tree now raises **XTDE3362** (rather than the generic XTDE3340) (`mode-1107a`/`b`/`c`).

### Expressions

- **A raw `&` in an XPath string literal no longer fails to parse.** An XPath string literal such as `select="'Special characters$&amp;'"` — where the XML parser has already decoded `&amp;` to a literal `&` before the expression reaches the XPath parser — previously raised `XPST0003: token recognition error`, because the underlying XQuery lexer treated `&` as the start of an entity reference. The stylesheet XPath parser now opts into `AllowRawAmpersand`, so a bare `&` is a literal ampersand (XPath, unlike XQuery, has no entity references). Fixes the whole W3C `misc/regex` test `regex-070` (modes a–l), which previously failed compilation as a unit because of a single `&` in one mode's default parameter.

### Serialization

- **`xsl:copy-of copy-namespaces="no"` no longer drops a copied element's own default namespace.** When a deep copy retained only the namespaces used by each element's name, a descendant (or a copy root) whose default namespace was *inherited* rather than locally declared lost that namespace entirely and silently adopted an ancestor's default namespace in the new output context. A dropped (unused) namespace is no longer recorded in the serializer's in-scope tracking — so it can no longer make a descendant believe a namespace it never emitted is already in scope — and namespace fixup now covers the default namespace (prefix `""`), not only prefixed elements. Fixes copy-of of SOAP-style payloads with `copy-namespaces="no"` where a body element in a distinct default namespace was re-parented under a differently-namespaced envelope (insn/copy-4901, copy-5201).

### Packages

- **`xsl:original` is resolved for every overridden component kind.** An `xsl:override` of a function (including when taken as a function item or partially applied), a variable (`$xsl:original`), an attribute-set (`use-attribute-sets="xsl:original"`), or a template rule can now invoke/reference the component it overrides — previously only some kinds worked.
- **Accepted components are visible and validated.** `xsl:accept` is applied with correct specificity (explicit name &gt; partial wildcard &gt; `*`), validates that an accepted name exists in the used package, and accepted variables, attribute-sets, and templates now resolve in the using package.
- **`xsl:override` is statically validated.** A disallowed child of `xsl:override` (**XTSE0010**), an overriding component that does not correspond to an overridable component of the used package (**XTSE3050**), and an incompatible attribute-set override (**XTSE3060**) are now diagnosed instead of silently accepted.
- **Component references resolve within their own package's scope.** A component's reference to a variable, attribute-set, or accumulator is resolved against its own package (including that package's private components) rather than a flattened global registry, so same-named components and diamonds across packages resolve correctly.
- **More package/entry-point errors are detected.** An initial named template invoked as an entry point must be public (**XTDE0040**); `xsl:mode/@name` rejects reserved `#`-tokens (**XTSE0020**); `xsl:import`/`xsl:include` of an `xsl:package` is rejected (**XTSE0165**); a `visibility` attribute on a template *rule* is rejected (**XTSE0500**); and an implicit mode exposed as private is ineligible as an initial mode (**XTDE0045**).
- **`xsl:use-package/@package-version` selects the correct version.** Version-range matching now picks the correct available package version by the spec's ordering — including pre-release ordering (`2.0.0-alpha` &lt; `2.0.0-beta` &lt; `2.0.0`) — rather than the first declared; the highest match is chosen (or the lowest, per a caller-supplied resolution policy).
- `xsl:key` declarations are now local to the package that declares them (XSLT 3.0 §3.6.2). A key name declared in a used (library) package is no longer visible to the using package, and two packages that each declare a key of the same name now index independently instead of merging into one shared index. `key()` resolves definitions against the package owning the executing template or function.
- Three more component kinds are now resolved package-locally, matching the same rule:
  - **Global variables.** When a diamond of packages contributes a same-named global with different values — an `xsl:override` on the same variable along two routes, or two used versions of the same package — each package's components now see their own package's value instead of one shared binding.
  - **Named `xsl:output` + `xsl:character-map`.** An `xsl:result-document`/`@format` naming an output declared in a used package now resolves that output, and its `use-character-maps`, against the declaring package (previously `XTDE1460`). A namespace that is the result namespace of an `xsl:namespace-alias` is now retained in the serialized result even when its prefix is in `exclude-result-prefixes`.
  - **`xsl:namespace-alias`.** An alias now rewrites only literal result elements produced by components of the package that declared it, so two packages can alias the same stylesheet namespace to different result namespaces without interfering.
- Template-rule conflict resolution now honours cross-package import precedence (XSLT 3.0 §6.6.2). A template rule brought in via `xsl:use-package` has lower import precedence than the using package's own rules, including one supplied inside `xsl:override`. Import precedence dominates priority, so an override rule now wins over a used-package rule even when the used rule declares a higher priority; ties at the same precedence still fall back to priority then document order.
- **Cross-package component conflicts are now detected (`XTSE3050`).** When two distinct `xsl:use-package` instances each contribute a visible component of the same kind and name — two independent used packages that each expose the same (e.g. unnamespaced) symbol, or the same package reached along two routes in a diamond (an `xsl:include`d module that also uses the package plus another `xsl:use-package`) — the using package now raises `XTSE3050` instead of silently keeping the first. A component resolved by `xsl:override`, one that only one package exposes, and one hidden by `xsl:accept visibility="hidden"` do not conflict; a public global inherited transitively through an intermediate package (diamond over different versions/overrides) is resolved package-locally and does not conflict. Relatedly, an `xsl:accept` naming a function with its required arity (`p:f#0`, erratum E36) is now correctly matched and applied — previously the arity suffix left the accept silently inert.
- A **private global variable of a used (library) package** is no longer visible to the using package. A top-level component of an `xsl:package` is private by default, and only `public`/`final`/`abstract` (or components exposed as such via `xsl:expose`) are visible across the `xsl:use-package` boundary. A reference to a private used-package variable from the using package now raises **XPST0008** instead of silently resolving (`use-package-006`, `use-package-007`). The variable is still resolvable inside its own package — a public function or global of the used package may reference its private siblings, and `$xsl:original` bindings, `xsl:override`, and diamond/per-version package-local resolution are unaffected — and plain `xsl:import`/`xsl:include` cross-module references are untouched.

## 1.4.22 (2026-07-09)

Serialization character-maps, pattern/mode conformance, and a concurrency-robustness fix. Requires PhoenixmlDb.Core 1.2.2 and PhoenixmlDb.XQuery 1.5.4. No API changes.

### Character maps

The character-map serialization path was reworked: astral (beyond-BMP) `xsl:output-character/@character` values are kept and mapped (the map is keyed on code point, not `char`); maps are applied *before* XML-escaping, so a map for `<`, `>`, or `&` fires; a mapped character outside the declared output encoding emits a numeric character reference; and mapped output is protected from Unicode normalization of the surrounding text.

### Serialization

- `xsl:output` declarations with the same expanded-QName name but different prefixes now merge (previously keyed on the lexical prefix, so `one:temp` and `two:temp` in the same namespace didn't).
- A `>` in a processing instruction serialized under the HTML method raises **SERE0015** (HTML terminates a PI with a bare `>`).

### Patterns and modes

- `xsl:mode` enumerated attribute values (`on-no-match`, `on-multiple-match`) are whitespace-normalized before validation, so surrounding whitespace no longer raises a spurious `XTSE0020`.
- Whitespace between a pattern's node test and its predicate is permitted (e.g. `letters (:comment:)[true()]`, where comment-stripping leaves a space).

### Robustness

- `ScopedOutputBuffer` is now safe when `_output` is cleared underneath an open scope (an `xsl:result-document` or finalize/flush path). The written length clamps to zero and disposal only shrinks the buffer, so this no longer throws `ArgumentOutOfRangeException` (which surfaced intermittently under concurrent load) or pads the buffer with NUL characters.

## 1.4.21 (2026-07-09)

Serialization output-method conformance — the HTML/XHTML method and serialization-error validation. Requires PhoenixmlDb.Core 1.2.2 and PhoenixmlDb.XQuery 1.5.4. No API changes.

### HTML / XHTML output method

- **URI-attribute escaping.** A URI-valued attribute (`href`, `src`, and the rest) is now NFC-normalized before its non-ASCII octets are percent-encoded, so a decomposed character encodes as its composed UTF-8 octets (matching the XQuery serializer).
- **XHTML empty elements.** The XHTML method emits well-formed empty elements (`<br />`) rather than the HTML minimized form.
- **Content-Type meta.** An existing `http-equiv="Content-Type"` `<meta>` is replaced with the computed media-type and charset, rather than skipped, so the document carries a single correct declaration.

### Character maps on attribute values

A character map now rewrites attribute values as well as text content (per §20), excluding URI-valued attributes under the HTML/XHTML methods (where `escape-uri-attributes` governs instead).

### Serialization-error validation

Invalid serialization parameters now raise the correct W3C Serialization 4.0 errors instead of silently producing output: **SESU0007** (an `encoding` the runtime can't produce), **SESU0011** (an unsupported `normalization-form`), **SEPM0004** (`standalone`/`doctype-system` with a result that isn't a single well-formed document element), **SEPM0009** and **SEPM0010**. The `undeclare-prefixes` output attribute is now parsed and honored.

### Control characters in attribute values

Control characters (DEL, `U+0080`–`U+009F`, `U+2028`) appearing in an attribute value now serialize as numeric character references instead of being written literally (which silently lost them on re-parse).

## 1.4.20 (2026-07-09)

Non-streaming type and serialization correctness, plus more streaming breadth. Requires PhoenixmlDb.Core 1.2.2 and PhoenixmlDb.XQuery 1.5.4. No API changes.

### Fix: `fn:available-system-properties` returns a sequence of `xs:QName`

The function returned a single non-QName item, so binding its result to `as="xs:QName*"`/`xs:QName+` raised `XTTE0570`. It now returns each system-property name as an `xs:QName`, matching the XSLT 4.0 signature.

### Fix: atomic `as=` atomizes a temporary-tree or node body

An `xsl:variable`/`xsl:param`/`xsl:with-param`/`xsl:function` declared with `as="ATOMIC-TYPE"` whose body is a sequence constructor (a temporary tree) or a `select` yielding nodes now atomizes that value and casts it to the declared atomic type — `xs:date`, `xs:time`, `xs:dayTimeDuration`, `xs:anyURI`, `xs:untypedAtomic`, and the rest — instead of leaving a raw result-tree-fragment (which raised `XTTE0780`, or made `instance of` return false). A subtype guard keeps `xs:dayTimeDuration` from being widened to `xs:duration`; a node/`item()`/map/array `as=` type keeps its nodes unchanged. All atomic coercion now routes through one shared caster.

### Fix: invalid serialization-parameter values are rejected

Invalid `yes`/`no`, enumerated, and pseudo-boolean values on `xsl:output`, `xsl:result-document`, and `disable-output-escaping` (e.g. `indent="TRUE"`, `standalone="1"`, `disable-output-escaping="YES"`) now raise `XTSE0020` at compile time instead of being silently ignored.

### Streaming: namespaces preserved when copying a streamed element

A streamed `xsl:copy-of` / `fn:copy-of` / `fn:snapshot` that selects an element by a striding path — `copy-of(/*/*:description)` against a document whose namespaces are declared on ancestors — now preserves the copied element's prefix and its in-scope namespace declarations (the streamed event pipeline previously carried only local names). `copy-namespaces="yes"` emits the full in-scope set; `copy-namespaces="no"` emits just what the element and its attributes use.

### Streaming: conditional and absorbing `xsl:for-each` select, and more

An `xsl:for-each` whose `select` is a conditional (`if(C) then … else …`) now streams the selected branch (the condition is evaluated once and the matching branch is driven). A wildcard-attribute existence climb (`ancestor::*/@*`) streams. A consuming attribute sequence such as `data(account/@value)` atomizes correctly inside a streamed `xsl:copy`.

## 1.4.19 (2026-07-08)

Streaming breadth. Requires PhoenixmlDb.Core 1.2.2 and PhoenixmlDb.XQuery 1.5.4. No API changes.

### Streaming: forwarded parameters into a materializing template body

A streamed template body that materializes its subtree (e.g. a rule whose body copies the matched node) now receives the caller's forwarded `xsl:with-param` values — both tunnel and non-tunnel — instead of seeing only parameter defaults. A tunnel parameter that gates an `xsl:copy-of` inside such a body now works.

### Streaming: whole-subtree `xsl:copy-of` forward

`xsl:copy-of` that copies a streamed document's whole subtree — `select="child::node()"`, or `select="."` on the document node — now forwards the live reader's events into the output at the lexical position, instead of evaluating against a closed synthetic document and yielding empty. This works with the copy-of wrapped in a literal result element, `xsl:copy`, `xsl:element`, `xsl:document`, or `xsl:result-document`.

### Streaming: §5.7.2 atomic-value separator in shallow `xsl:copy`

A shallow `xsl:copy` of adjacent atomic values on the streaming path now inserts the single-space separator that sequence normalization (§5.7.2) requires between adjacent atomic values, matching the non-streaming output (previously the values ran together). A text node correctly breaks the atomic run.

## 1.4.18 (2026-07-07)

Streaming robustness and performance. Requires PhoenixmlDb.Core 1.2.1 and PhoenixmlDb.XQuery 1.5.3. No API changes.

### Streaming: observe cancellation

A streamed transform now polls the `CancellationToken` across its streaming and whole-input-buffer loops, so a long-running or unbounded transform cancels promptly when the caller's token fires, instead of running to completion and holding the thread. A streamed transform given an already-cancelled token throws `OperationCanceledException` without doing the work.

### Fix: `xsl:try` output checkpoint is O(1), not O(N)

`xsl:try` previously snapshotted the entire accumulated output buffer (`ToString()`) on every attempt so it could roll back the try body on a caught error. Inside a streamed `xsl:for-each` that accumulates all matches into one buffer, the k-th item copied the ~k already-emitted items — O(N²) over the whole pass. It now checkpoints by buffer length and, on the rollback path, truncates back to that length (discarding exactly the failed try body's output). A per-item `xsl:try` over a large streamed input is now linear; a 100K-item catch-per-item transform drops from tens of seconds to a few.

### Linear large-element string value (via Core 1.2.1)

Picks up PhoenixmlDb.Core 1.2.1, whose XML parser resolves child nodes through an id→node index instead of a per-child linear scan, so computing (or atomizing) the string value of an element with many children is linear rather than quadratic.

## 1.4.17 (2026-07-06)

Streaming breadth and correctness. Requires PhoenixmlDb.Core 1.2.0 and PhoenixmlDb.XQuery 1.5.2. No API changes.

The streamability analysis is now **compositional** — a posture/sweep classification computed over the expression tree — rather than a fixed catalogue of recognized shapes. A single streaming plan is derived from that classification and drives the buffer/stream decision at both the template and document level. The practical effect is that many more genuinely-streamable constructs are accepted and executed as true streaming, instead of falling back to whole-input buffering or (worse) producing empty output.

### Streaming: apply-templates dispatch

`xsl:apply-templates` with a downward `select` now dispatches the matched template's body per selected node during the forward pass, threading tunnel and non-tunnel `xsl:with-param` values into the matched rule. Multi-step striding-descent selects (e.g. `select="root/item"`) dispatch correctly.

### Streaming: general comparisons over a per-item attribute tail

A general comparison whose operand is a simple map ending in an attribute-arithmetic tail — e.g. `(account/transaction/(@value*2)) = 8.64`, or `abs(@value)` — now streams: each matched node is captured and its tail evaluated per item, cheaply, without buffering the input.

### Streaming: absorbing and windowing functions

`head`, `tail`, `subsequence`, `remove`, and `insert-before` over a streamed sequence now apply their positional/window arguments during the forward pass, and `sum(seq, $default)` emits the default when the sequence is empty. `fn:unordered`/`fn:trace`/`one-or-more`/`exactly-one` pass-throughs no longer hide the streamable path beneath them.

### Streaming: atomization, separators, and sinks

`value-of`, `xsl:attribute`, and `data()` over a streamed sequence atomize each item and join with the correct separator instead of emitting raw markup. `xsl:message` and `xsl:assert` are recognized as grounding sinks (they emit nothing into the result tree). Namespace-wildcard steps (`*:name`) register a streaming match instead of bailing to empty. `xsl:result-document` with motionless content driven from a streamed pass captures its secondary output.

### Fix: `xsl:attribute` simple-content merging

`xsl:attribute select="…"` over a run of adjacent text nodes now merges them without a separator (per §5.7.2) before applying the separator to remaining items — a correctness fix shared with the non-streaming path.

### Streaming: xsl:iterate over a grounded atomic crawl

An `xsl:iterate` whose `select` grounds each crawled node to an atomic value — for example `.//*/name()` — is now correctly accepted as streamable and streamed. The streamability classifier previously rejected it with a spurious `XTSE3430` ("crawling select expression has a consuming body") because it saw the descendant crawl together with a body that reads the context item (as a map-lookup key). When the per-item result is atomic (the select ends in a grounding step such as `name()`, `string()`, `data()`, `copy-of()`), the context item is a value, not a streaming node, so accumulating a grounded map parameter and emitting it from `xsl:on-completion` is streamable — the canonical streaming histogram. A genuinely consuming iterate body over a streaming-node crawl (navigating children/descendants of the per-item node) is still rejected.

## 1.4.16 (2026-06-30)

Thread-safety completion, base-URI correctness, and streaming path-matching fixes. Requires PhoenixmlDb.Core 1.2.0 and PhoenixmlDb.XQuery 1.5.1. No API changes.

### Fix: thread-safe namespace interning (completed)

1.4.15 made `StylesheetParser.ResolveNamespaceUri` concurrent. The remaining QName-parsing intern sites in the parser now route through that same thread-safe path, so no namespace interning remains on the old non-atomic dictionary/counter.

### Fix: base URI on constructed temporary trees

A constructed document-node temporary tree now carries the stylesheet base URI, so `base-uri()` and relative-URI resolution against such a node behave correctly (previously the constructed node had no base URI).

### Streaming: path anchoring

A relative match/select pattern in streaming mode is anchored to its runtime context-root depth rather than matching at any descendant depth. A downward step such as `ITEM/PAGES` evaluated from the document node no longer spuriously matches deeper elements — which had skewed streaming aggregates such as `sum`/`avg`/`min`/`max`.

### Streaming: result-document capture

`xsl:result-document` with motionless content, driven from a streamed `apply-templates`, now captures its secondary output instead of producing nothing.

## 1.4.15 (2026-06-25)

Thread-safety fix and more streaming correctness for consuming expressions over a streamed source. Requires PhoenixmlDb.Core 1.1.9 and PhoenixmlDb.XQuery 1.4.7. No API changes.

### Fix: thread-safe namespace interning

`StylesheetParser.ResolveNamespaceUri` interned namespace URIs into a process-wide plain dictionary with a non-atomic id counter, so concurrent transforms/parses could corrupt the table, hand the same id to two URIs, or throw while another thread enumerated it. It now uses a concurrent dictionary with an interlocked counter — lock-free and safe under concurrent use.

### Streaming: consuming expressions over a streamed source

Each of these now produces correct results under `xsl:mode streamable="yes"` (the engine materializes the input where it cannot drive the consuming expression off the forward reader, rather than silently yielding empty):

- A consuming `copy-of` / `for-each` inside **`xsl:on-empty` / `xsl:on-non-empty` / `xsl:where-populated`** (including an `xsl:fork` or a consuming `xsl:comment` / `xsl:processing-instruction` in the populated content).
- A `copy-of` / `value-of` whose select is a **conditional** (`if (…) then … else …`) with a consuming branch.
- A `copy-of` / `value-of` / `xsl:variable` selecting a **climbing axis** — an attribute or ancestor path (e.g. `…/@value`).
- A consuming function call in an **attribute value template** on a constructed element (e.g. `{count(//*)}`).
- A `for-each` over a **generic `node()` kind test** (e.g. `//node()[name() = $param]`).
- An `apply-templates` over a **function-wrapped select** (e.g. `copy-of(outermost(//p))[…]`).

## 1.4.14 (2026-06-22)

Streaming correctness for consuming expressions over a streamed source. Requires PhoenixmlDb.Core 1.1.9 and PhoenixmlDb.XQuery 1.4.6. No API changes.

- **`outermost(//X)` with an inspection-only body** streams. A streamable `xsl:for-each` / chained simple-map whose source is `outermost(//X)` and whose body only inspects each matched node (ancestor/parent/self/attribute navigation, atomization) now dispatches per match without buffering — outermost deduplication is decided on the live ancestor stack.
- **Per-item set/sequence operators over a wrapped aggregation** stream — e.g. `path ! (* union $grounded)` and the `outermost`/`remove`-wrapped value-of/copy-of cases with an outer positional predicate.
- **`xsl:for-each select="snapshot(path)"`** streams: the `snapshot()`/`copy-of()` wrapper is recognized so the iteration runs over the snapshotted nodes (with attribute/text leaf kinds and multi-level ancestor navigation).
- **Intermediate positional predicate in a streamable for-each path** — e.g. `works/department/employee[1]/empnum/text()` — is now honored (a forward-countable-positional or motionless predicate on a non-leaf step), instead of being dropped.
- **Arithmetic over a streamed text node** — `head(//PRICE/text()) + 1` — works: a streamed text node's value is typed `xs:untypedAtomic` (matching a non-streamed text node), so it promotes to numeric instead of raising a type error.

## 1.4.13 (2026-06-21)

Streaming error reporting. Requires PhoenixmlDb.Core 1.1.9 and PhoenixmlDb.XQuery 1.4.6. No API changes.

### Fix: XTSE3430 surfaces for a striding-union-then-step select under streaming

A streamable select that unions two striding paths and then takes a step — `(/BOOKLIST/ITEM | /BOOKLIST/MAGAZINE)/PRICE` — is not guaranteed streamable (the union of two striding expressions is crawling). The engine already classified this as XTSE3430 at compile time, but when the stylesheet was invoked through a named initial template the streamable-mode fast path skipped the template and never reached the point where the deferred error is raised, so the transform silently produced no output. The error now surfaces. Stylesheets whose streamed input belongs to an inner `xsl:source-document` continue to stream normally.

### Fix: unknown collation raises FOCH0002 under streaming

`fn:distinct-values`, `fn:max`, `fn:min`, and `fn:index-of` raise `FOCH0002` for an unknown collation URI. When the collation argument was an expression navigating the streamed input (e.g. `distinct-values($seq, /special/unknownCollation)`), under streaming that argument evaluated to nothing and the call silently fell back to codepoint collation instead of erroring. Such a call now resolves its collation argument against the input and raises `FOCH0002`, matching non-streaming behaviour. Calls with a grounded or absent collation argument are unaffected.

## 1.4.12 (2026-06-19)

Grouping fix + streaming correctness. Requires PhoenixmlDb.Core 1.1.9 and PhoenixmlDb.XQuery 1.4.6. No API changes.

### Fix: `xsl:for-each-group group-by` with an empty grouping key (Martin Honnen)

When an item's `group-by` expression atomizes to the empty sequence, that item contributes no grouping key and is assigned to no group (XSLT 3.0 §19.2). The engine previously formed a single group with an empty (`""`) grouping key, so the body ran once over all items. It now forms no group for such items (matching Saxon). Non-empty-key grouping — distinct keys, document order, multi-value sequence keys, `current-grouping-key()` — is unchanged. Applies to both streaming and non-streaming `for-each-group`.

### Streaming: consuming expressions inside surrounding construction now stream in place

A consuming expression that sits inside surrounding constructed output — rather than as the bare top-level content of a streamable body — previously dropped its wrapper or iterated empty, because streaming was two special-cased mechanisms that didn't compose with construction. These now execute in place within linear body execution, driving the input at the expression's lexical position:

- A consuming `xsl:for-each` or `xsl:apply-templates` wrapped in literal result elements, `xsl:element`/`xsl:copy`, or `xsl:if`/`xsl:choose`.
- Per-item consuming operators in an XPath simple-map — `path ! local-name(.)`, `path ! name(..)`, `path ! (* union/except/intersect $grounded)`, `path ! (*, $grounded)`, `path ! [*, $grounded]`, and conditional/atomizing right-hand sides.
- Bounded-window functions over a streamed path used as the source of a streamable `xsl:for-each`/simple-map — `head(path)`, `tail(path)`, `remove(path, n)`, `subsequence(path, s, l)`.
- Fixed-depth wildcard steps in a streamable `xsl:for-each` select (e.g. `/*/*`).

(Continues the streaming work begun in 1.4.11. Some advanced shapes — descendant-axis windows, `outermost()`, secondary `xsl:result-document`, and a few grouping/snapshot cases — remain follow-ups.)

## 1.4.11 (2026-06-18)

Streaming correctness. Requires PhoenixmlDb.Core 1.1.9 and PhoenixmlDb.XQuery 1.4.6. No API changes.

### Fix: consuming expressions at the top of a streamable body now stream

When a consuming (input-navigating) expression sits **directly** in a `streamable="yes"` `xsl:source-document` body — or directly in a streamable `xsl:template match="/"` — it had no document-level streaming dispatch: the engine's streaming execution only engaged inside templates reached by the streaming processor, so at the document level the expression was evaluated against an empty synthetic input and produced empty, seed-only, or partial output (and in some cases an eager type error or a non-terminating walk over an unread reader).

These constructs are now recognized at the document level and routed through the engine's whole-input materialization path when — and only when — their operand actually navigates the input (a grounded operand such as a literal, range, or variable stays on the incremental streaming path, so large-input transforms are unaffected). Covered constructs:

- `xsl:try` (a consuming expression in the try body)
- `xsl:fork` (prongs that re-traverse the input, including `fork` → `for-each-group`)
- `xsl:iterate` (select navigating the input)
- `xsl:for-each-group` (population from `group-by`/`group-adjacent`/`group-starting-with`/`group-ending-with` navigating the input)
- aggregation and higher-order functions over the input — `fold-left`/`fold-right`, `sum`/`count`/`avg`/`max`/`min`/`string-join`, `boolean`/`not`/`exists`/`empty`, `one-or-more`/`exactly-one`/`zero-or-one`
- expression operators over the input — `treat as`, `instance of`, `castable as`, `union`/`except`/`intersect`, sequence construction `(a, b)`, and the square-array constructor `[ … ]`

### Fix: intermediate-step predicate in streaming aggregation

A predicate on an **ancestor** step of a streamed aggregation path — e.g. `avg(BOOKLIST/BOOKS/ITEM[@CAT='P']/PRICE)`, where the filter is on `ITEM` rather than the matched `PRICE` — was dropped, so the aggregate ran over the unfiltered set. Motionless ancestor predicates (and forward-countable positional ones such as `ITEM[3]` / `ITEM[position() lt 4]`) are now applied during the streaming pass.

### Fix: `xsl:for-each` select ending in `copy-of()`/`snapshot()` (Martin Honnen)

A streamable `xsl:for-each` whose `select` ends in a trailing zero-argument `copy-of()` or `snapshot()` step (e.g. `select="records/record/copy-of()"`) produced no output — the trailing snapshot step is now peeled and the subscription is driven off the head path.

## 1.4.10 (2026-06-17)

Two Martin Honnen fixes. Requires PhoenixmlDb.Core 1.1.9 and PhoenixmlDb.XQuery 1.4.6.

### Fix: streaming `xsl:template match="/"` fires under `streamable="yes"`

With `<xsl:mode streamable="yes"/>`, a global `<xsl:template match="/">` never fired — the streaming processor began at the root element and skipped the document node, so the built-in document rule (a text-only copy) ran and all constructed/copied element output was lost. The streaming entry now dispatches the document node to the matching user template and executes its body via the streaming subscription/active-processor machinery, falling back to the built-in crawl when no document-node template matches (existing streaming stylesheets unaffected). Known limitation (tracked follow-up): a `match="/"` body that *wraps* the streamable `xsl:for-each`/`xsl:apply-templates` in an outer constructed element is not yet covered.

### Fix: base URI preserved across temporary-tree copy boundaries (DocBook xslTNG)

`base-uri()` (and the `resolve-uri()`/`doc()` resolution that depends on it) now returns the correct base URI for nodes copied into a temporary tree and for `fn:transform` result documents. Copied nodes preserve their source base URI across the engine's serialize/reparse temp-tree boundary; constructed temp-tree document nodes and `fn:transform` results take a non-null base URI (per XSLT 3.0 §11.9.1). This fixes `FORG0002: The base URI '' is not a valid absolute URI` seen transforming DocBook xslTNG (mediaobject `@fileref` resolution).

## 1.4.9 (2026-06-15)

Fix (Martin Honnen): `fn:xml-to-json` honors the `indent` option.

`xml-to-json($x, map { 'indent': true() })` produced compact, single-line JSON — the two-argument overload validated the `indent` option but ignored it. It now pretty-prints the result (conventional two-space, one-member-per-line layout; the serialization spec leaves exact whitespace implementation-defined). String content and escaping are unchanged, so the indented output parses to the same JSON.

### Internal: unified output serialization pipeline

Output post-processing (text-strip, HTML handling, indentation, character maps, Unicode normalization, XML declaration, DOCTYPE, BOM, escape-uri, sentinel restore) now flows through a single `FinalizeOutput` path for every delivery route — node source, `XdmSequence`/initial-context-item, buffered streaming, and `xsl:result-document`. Previously each route applied a divergent subset, which had caused indentation/post-processing to be silently skipped on some paths (e.g. JSON/map input via the `XdmSequence` overload). No behavior change for output that was already correct; the previously-divergent routes now receive the complete, identical treatment. Backed by a golden serialization test matrix (method × delivery-path × indent).

### Internal: shared JSON serialization primitives

The two JSON emitters — the `method="json"` value serializer and the `fn:xml-to-json` element-tree serializer — now share their primitives: string escaping, newline/indentation, and duplicate-key detection are single-sourced rather than reimplemented per emitter. `fn:xml-to-json` indentation is produced inline during emission; the separate re-indent post-pass that previously reformatted already-serialized JSON has been removed. The two emitters remain distinct where the spec requires it (`xml-to-json` preserves `<number>` lexical form verbatim and raises `FOJS0006` on duplicate keys; the value serializer reformats numeric values and raises `SERE0022`); only the shared mechanics are unified. JSON layout parity (value-method output vs the equivalent `xml-to-json` tree) is covered by the golden matrix.

### Internal: single-sourced character escaping

XML text, XML attribute, and JSON string escaping now flow through one `CharacterEscaper` helper instead of the several near-duplicate copies that had accumulated across the serialization paths. As part of this, attribute-value serialization on every path now escapes tab/newline/carriage-return as numeric character references (`&#x9;`/`&#xA;`/`&#xD;`) — previously two of the paths emitted them literally, where XML attribute-value normalization would have silently collapsed them to spaces on re-read. JSON string escaping keeps its three distinct entry contracts (raw, lenient pass-through, and validating with `FOJS0006`); only the shared per-character escape rules are unified.

This `CharacterEscaper` now lives in `PhoenixmlDb.Core` (`PhoenixmlDb.Xdm.Serialization`, Core 1.1.9) and is shared with the XQuery engine, so both engines escape serialized output identically. XSLT output is unchanged by the move. Builds against PhoenixmlDb.Core 1.1.9 and PhoenixmlDb.XQuery 1.4.5.

No API changes. Builds against PhoenixmlDb.XQuery 1.4.4.

## 1.4.8 (2026-06-14)

Fix (Martin Honnen): JSON array as the context item for `apply-templates`.

Continuing the 1.4.7 JSON-`XdmSequence` work: when a parsed JSON **array** is fed as the initial context item to a stylesheet whose template applies via `apply-templates` (e.g. a named `xsl:initial-template` with `match="."`, common in grouping stylesheets), the array was iterated as its members — the template fired once per member, so `?*` and lookups saw a single map and failed with "Lookup requires a map or array, got String".

`apply-templates` now treats an XDM array (`List<object?>`) and an XDM map (`IDictionary`) as a single item; only the engine's sequence representation (`object?[]`) is iterated. A `for-each-group` grouping pipeline over parsed JSON now produces the expected grouped JSON.

This release also sweeps the same class of issue across the item-processing instructions: `xsl:for-each`, `xsl:iterate`, `xsl:for-each-group`, `xsl:perform-sort`, and `xsl:merge` (`for-each-item` / `for-each-source`) now treat a selected array/map as a single item rather than flattening it into members/entries (shared `SelectResultItems` helper, alongside the already-fixed `xsl:sequence` and `apply-templates`). Atomizing contexts such as `xsl:value-of` continue to flatten an array to its members, as required.

No API changes. Builds against PhoenixmlDb.XQuery 1.4.4.

## 1.4.7 (2026-06-14)

Fix (Martin Honnen): JSON input round-tripped through `TransformAsync(XdmSequence)`.

When JSON is parsed to an `XdmSequence` (e.g. via `TransformToSequenceAsync`) and fed to a stylesheet with `xsl:output method="json"` — for example an identity template `<xsl:template match="."><xsl:sequence select="."/></xsl:template>` — the result now serializes as JSON. Previously:

- A map (or array) was returned raw and the string-returning overload rendered it as its CLR type name (`PhoenixmlDb.XQuery.Execution.OrderedXdmMap`). The non-node initial-context-item path now serializes collected map/array/atomic items as JSON when the principal output method is json/adaptive/csv, sharing the same emission as the main transform path.
- A top-level JSON **array** was unwrapped to its first member. `xsl:sequence` no longer flattens the `List<object?>` array representation (an XDM array is a single item); arrays and maps also no longer flatten in the shared sequence-builder. `?*` over an array now yields its members as JSON rather than a `System.Object[]`.

No API changes. Builds against PhoenixmlDb.XQuery 1.4.4.

## 1.4.6 (2026-06-13)

Three engine fixes. No API changes.

### Fix: deeply-recursive stylesheets raise a catchable error instead of crashing the host

A recursive `xsl:function` (or deeply nested apply-templates) expands into many .NET frames per logical call, so runaway recursion could exhaust the native execution stack and abort the hosting process — a `StackOverflowException` is uncatchable in managed code. The engine now probes the remaining stack at each stylesheet-function call and raises a catchable `XTDE0000` (surfaced as `XsltException`) before the hardware limit is reached, so unbounded recursion fails cleanly and the host survives. Intentionally deep recursion can be accommodated by running the transform on a thread with a larger stack.

### Fix: key() matches attribute/text use-values against string lookup keys

`key('k', 'a')` returned no nodes when the key's `use` expression produced an attribute or text value. Such values atomize to `xs:untypedAtomic`, which was not being compared against the `xs:string` lookup key. Untyped use-values are now compared as the lookup value's type (string-to-string here), so keys defined with `use="@attr"` resolve correctly. `xs:anyURI` use-values, promotable to string, are handled the same way.

### Fix: parse-json arrays and indentation on JSON/map-sourced output (Martin Honnen, 2026-06-12)

- `parse-json('[…]')` of a top-level JSON array now yields an `array(*)` rather than a flattened sequence, so array lookup (`?N`) and grouping over the result behave as written.
- `indent="yes"` now applies when the serialized output is built from a JSON/map initial context item; the `TransformAsync(XdmSequence)` path previously skipped indent post-processing.

## 1.4.5 (2026-06-07)

### XSLT 4.0 ordered maps

`xsl:map` / `xsl:map-entry`, map constructor expressions, `xsl:record`, and JSON-object maps (`fn:parse-json` / `fn:json-to-xml` object results) now iterate in entry/insertion order as a structural guarantee, backed by the engine's `OrderedXdmMap` (requires PhoenixmlDb.XQuery 1.4.3). XSLT 3.0 left map order unspecified; XSLT 4.0 makes it a contract, and PhoenixmlDb now honors it.

Grouping into a map — `xsl:for-each-group` building map entries — emits groups in first-seen order, the same order the XML-target equivalent produced. The "don't write code that relies on map order" caveat no longer applies.

XSLT map key equality is unchanged by this release (only iteration order is now guaranteed). 469/469 XSLT unit tests green.

## 1.4.4 (2026-06-08)

Profile-driven streaming-allocation overhaul. No behavior or API changes.

### Bench: streaming-identity, 1M items

| | alloc | elapsed | peak RSS |
|---|---|---|---|
| 1.4.3 baseline | 3328.6 MiB | 4921 ms | 16 MiB |
| 1.4.4 | **368.4 MiB** | **3412 ms** | 16 MiB |
| delta | **−88.9%** | **−30.7%** | flat |

### What changed

`dotnet-trace --profile gc-verbose` plus a small `GCAllocationTick` aggregator on the 1M-item `streaming-identity` bench surfaced a sequence of distinct hotspots. Each was attacked one at a time, with the bench and a re-trace gating the next step.

- **Match-context delegates cached.** `CreateMatchContext()` rebuilt seven `Func<>` delegates on every per-element template-match call (method-group conversions allocate a fresh delegate each call). The seven delegates plus the `XsltContext` instance together accounted for ~55% of the original streaming-identity allocation. Delegates are now built once lazily on the transformer / processor and reused.
- **XsltContext pooled via lease pattern.** Introduced a `MatchContextLease` ref struct with `Dispose`; 17 callsites (`FindMatchingTemplate`, `FindImportedTemplate`, `pattern.Matches`, etc.) migrated to `using var mc = AcquireMatchContext(...)`. The allocating `CreateMatchContext()` is kept as a fallback for callers that need the context out of scope.
- **Scope pooled.** `PushScope` / `PopScope` draw from a per-context `Stack<Scope>` (cap 32); the lazy `Variables` / `TunnelParameters` dictionaries are `Clear()`'d in place so variable-heavy scopes reuse their hashtable rather than reallocating.
- **`Scope.Variables` and `TunnelParameters` lazy-initialized.** Most template fires bind no variables; the eager pair of empty `Dictionary<QName, object?>` per `PushScope` was ~28% of post-delegate-cache alloc. Backing fields are now nullable; readers use new `VariablesOrNull` / `TunnelParametersOrNull` short-circuits in the three hot lookup paths.
- **`StreamingNodeContext` pooled (element + per-attribute).** Properties switched from `init` to `set` so a single per-processor `Stack<StreamingNodeContext>` (cap 256) can mutate fields on reuse. `CleanupStreamingNode` releases both the inner attribute contexts and the outer element context.
- **`XdmElement` pooled with backing lists.** `_elementPool` (cap 64) with `UnsafeAccessor` setters for the init-only `Namespace` / `LocalName` / `Prefix` / `Attributes` / `Children` / `NamespaceDeclarations` fields. The `List<NodeId>` attrIds and `List<XdmAttribute>` materializedAttrs are pooled in step, so their backing arrays are reused too. Safe under streaming mode — XSLT 3.0 §19 prohibits streamable templates from retaining non-grounded node refs across element boundaries.
- **`XdmAttribute` pooled.** Mirrors the existing `XdmText` pool; per-processor `Stack<XdmAttribute>` (cap 128) with `UnsafeAccessor` field setters. `MaterializeElement` stashes the materialized refs on the streaming node context so cleanup can release them.
- **Per-element collections lazy.** `StreamingNodeContext.Attributes` / `NamespaceDeclarations` are now nullable; `MaterializeElement` and the watcher dict builder treat null as empty. Most elements have one but not both, so the empty-list / empty-dict churn disappears.
- **`List<StreamingNodeContext>` pooled.** The per-element attribute carrier (plus its backing `T[]`) is now drawn from `_nodeCtxListPool` (cap 32); `Clear()` on release retains the array.
- **Allocation-free attribute enumeration.** `XdmInMemoryStore.EnumerateAttributes` returns a struct enumerator that walks `elem.Attributes` by index (no boxed `IEnumerator`) and looks up each attribute in the store — replaces the `yield`-iterator state machine on the streaming shallow-copy hot path. The legacy `GetAttributes(IEnumerable<XdmAttribute>)` is kept for non-hot consumers.

### Notes

- Pool reuse is only correct under streaming mode. Non-streaming materialization paths (`MaterializeLeafElement`, `MaterializeSubtreeFrame`, `StreamingSubtreeMaterializer`) still allocate fresh instances — those are intentionally retained.
- Cumulative output of the 1M-item identity transform is unchanged (30.3 MiB output, byte-identical).
- 463/463 XSLT unit tests stable across multiple runs.

## 1.4.3 (2026-06-06)

Two Martin Honnen 2026-06-06 reports fixed.

### Fix: streamed JSON output — string values now quoted

When `xsl:output method="json"` was driving a streaming transform, top-level `xsl:map` constructs were emitted with unquoted string values — `{"name":item 1}` instead of `{"name":"item 1"}` — producing invalid JSON. Root cause: `TransformStreamingCoreAsync` never called `BeginSequenceCollection`, so the streaming path's top-level `CreateMapAsync` fell into its no-accumulator branch which called `SerializeItemAsJson(map, adaptive: true)` — adaptive mode is by design quote-stripping. The non-streaming entry point set up sequence collection at the start and called the proper finalizer at the end; the streaming entry didn't. Both paths now share a single `FinalizeJsonOutput` helper that honors the output declaration's `method`, `indent`, `allow-duplicate-names`, and `json-node-output-method`.

### Fix: streamed JSON output — `indent="yes"` honored

Same root cause as above. Once the streaming entry point routes through `FinalizeJsonOutput`, the existing indent-threading inside `SerializeItemAsJson` (added in 1.4.2 for the non-streaming case) takes effect for streaming output too.

### Fix: stray empty array at end of `xsl:array` content

A wrapping `xsl:map-entry` containing `<xsl:array><xsl:for-each ...><xsl:map>...</xsl:map></xsl:for-each></xsl:array>` emitted an extra `[]` member at the end of the array — `[map1, map2, map3, []]`. `CreateArrayAsync` was not redirecting `_sequenceAccumulator` to the array under construction, so top-level items produced by the body (xsl:map, xsl:sequence, atomic value-of) leaked to the outer accumulator and the array itself ended up empty (only xsl:array-member contributions reached it). Per XSLT 4.0 §22, every top-level item produced by the body becomes a member of the new array. `CreateArrayAsync` now saves the outer accumulator and points it at the new array for the body's execution; `xsl:array-member` continues to write directly to the array peek as before. Affects both streaming and non-streaming paths.

## 1.4.2 (2026-06-04)

Two Martin Honnen 2026-06-04 reports fixed, plus continued streaming conformance gains and a small perf improvement.

### Fix: `xsl:output method="json" indent="yes"` honored

The JSON serializer was emitting compact output regardless of `indent="yes"`. `SerializeItemAsJson` now threads an indent flag and current depth through its recursive map/array/object-sequence branches, emitting pretty-printed JSON with two-space indentation when the output declaration requests it. The XQuery side (`XQueryResultSerializer.SerializeAsJson`) already had this wired; only the XSLT path was missing.

### Fix: `xsl:for-each-group group-by` under streaming mode

A template body-level `<xsl:for-each-group select="item!copy-of()" group-by="category">` under `<xsl:mode streamable="yes"/>` was emitting `{}` followed by raw text content of each item. `StreamingSubtreeBufferDetector` only recognized for-each-group nested inside `xsl:fork`; bare for-each-group at template body level fell into the default case and never materialized the matched subtree. The detector now requests a subtree buffer when `xsl:for-each-group` has `group-by` and a select expression that navigates the matched subtree. The existing `_streamingSubtreeBufferConsumed` path picks this up and the non-streaming `group-by` branch evaluates correctly against the buffered element.

Native streaming `group-by` inside `ForEachGroupStreamingAsync` remains a deferred follow-up; the buffer-fallback resolves the reported case and any group-by-over-downward-axis pattern that fits in memory.

### Streaming conformance: +69 since 1.4.1

`StreamingExpressionScanner` now recognizes `outermost(downward-path)` and `innermost(downward-path)` wrapping a SimpleMap with a grounded per-item constructor RHS. The matched nodes materialize as a Snapshot watcher; the wrapping SimpleMap evaluates against the materialized result. Closes ~26 tests across `sf-empty`, `sf-exists`, `sf-not`, `sf-boolean` plus a cascade in adjacent sets. Conformance moved from 1898/2358 to 1967/2358 (83.4%).

### Perf: `XdmText` pooled in streaming processor

`StreamingXmlProcessor` now reuses `XdmText` instances across the per-event Register/Remove cycle via a small instance-scoped pool. Reduces allocation by ~2.2% on a 1M-item streamed identity transform. Snapshot materialization and body-capture paths bypass the pool so captured snapshots never alias pooled instances.

## 1.4.1 (2026-06-01)

### Fix: Blazor WebAssembly DocBook XSLT regression (Martin Honnen)

DocBook TNG pipelines running under Blazor WebAssembly were broken in 1.4.0
with errors like `Transformation terminated: No template for X` (from DocBook's
own `unhandled.xsl` `<xsl:message terminate="yes">`). CLI 1.4.0 worked fine for
the same example. Two related bugs contributed:

1. **`XsltTransformProvider.LoadStylesheetAsync` bypassed `PreloadedResources`.**
   The XQuery-side `fn:transform()` provider went straight to
   `HttpResourceLoader.GetStringAsync` for the `stylesheet-location` URL. On
   WASM (where sync-over-async HTTP isn't supported) this throws FOXT0001,
   making the dynamically-invoked DocBook module fail to load. Now mirrors the
   engine-internal pattern at `XsltTransformer.cs:28485-28505` — consults
   `PreloadedResources` first.
2. **`HttpImportPreloader`'s pre-walker missed `fn:transform()` URLs.** The
   walker only matched literal `doc()` / `document()` calls. DocBook TNG uses
   `transform(map{'stylesheet-location':'http://...'})` to chain its pipeline
   modules. Those URLs were never seeded into the auto-preload set, so a host
   relying on the walker to know what to fetch missed the pipeline modules.
   Walker now also recognises `(fn:)?transform()` with literal-URL
   `stylesheet-location`.

Net effect: DocBook XSLT pipelines under Blazor WebAssembly resume working with
the same explicit-preload pattern that worked before 1.4.0.

## 1.4.0 (2026-06-01)

W3C XSLT 3.0 streaming conformance jumped from **71.3% → 80.5% (1681 → 1898 tests passing)** via a sustained scanner/processor/engine push. **24 commits** across the streaming pipeline. **11 new W3C streaming sets reached 100%**: si-element, si-LRE, si-value-of, si-copy, si-document, sf-copy-of, sf-fold-right, sf-zero-or-one, sf-innermost, plus all six sx-GeneralComp-* variants.

### Streaming for-each inside `xsl:source-document`

`<xsl:for-each select="...">` inside `<xsl:source-document streamable="yes">` now drives the streaming pass via a new `ForEachSubscription` mechanism. Supports:

- Absolute child-axis paths (`/BOOKLIST/BOOKS/ITEM/PRICE`)
- Relative-from-root paths (`account/transaction/...`)
- Mixed sequences (`100, 101, /path` or `data(/path/@attr), 101, 102`)
- `text()` KindTest at the path tail
- Attribute axis tail (`/path/@attr`) with the matched attribute pushed as context
- Predicates on the last element step (`transaction[@value < 0]`)
- `fn:data()` unwrap around the path

Conditional wrappers (`xsl:on-empty`, `xsl:on-non-empty`, `xsl:where-populated`) correctly suppress subscription dispatch — for-each inside falls back to the buffered path that honours the wrapper's gate.

### Streaming watcher infrastructure

- Scanner descends `xsl:element/@name` and `xsl:attribute/@name`/`@namespace` AVTs for consuming expressions (e.g., `head(//AUTHOR)`).
- Scanner recognises `SimpleMapExpression(downward-path, per-item-atomic-casts)` for `path!xs:NMTOKENS(.)!xs:decimal(.)` patterns — closes all 6 sx-GeneralComp streaming sets.
- Scanner recognises `descendant::name` axis (crawling traversal) with nested same-name elements.
- Scanner recognises `snapshot(streamable-path)//tail` — closes sf-innermost and cascades into ~30 other tests across multiple sets.
- Scanner recognises positional `[1]` predicate for Head watcher.
- Scanner extracts grounded operands (literals, variable refs) from mixed comma sequences.
- Watcher resolver in `TryResolveExprFromWatchers` handles `NamedFunctionRef` and `InlineFunctionExpression` so higher-order functions (`filter`, `fold-right`) work with user `f:` function arguments.
- Aggregation watchers honour motionless predicates on the last element step (e.g., `zero-or-one(/path/ITEM[@CAT='H'])`).
- Processor materialises leaf and subtree element snapshots so `copy-of`/`snapshot` accumulators yield real `XdmElement` trees.
- Processor dispatches text-node children separately when the path tail is `text()`, preserving `..` parent navigation.

### Engine fixes

- `XdmElement._stringValue` is now populated by the `xsl:variable as="element()*"` construction path (was returning empty for `value-of select="."` on variable-constructed elements).
- `xsl:copy/@select` selecting more than one item raises `XTTE3180` per XSLT 3.0 §5.6.
- Streaming subtree materialiser precomputes element string-value bottom-up so `value-of` on snapshot elements works.

### Dependencies

- CPM pin bumped to `PhoenixmlDb.XQuery 1.4.1` (was 1.3.15) — picks up the QT3 production sweep fixes (EQName parser, deep-equal collation, fn:format-number isolation, module copy-namespaces scoping, XPTY0004 for date/time comparisons, function(*) matches map/array, etc.).

## 1.3.23 (2026-05-29)

### New: incremental streaming output + direct stream input (Martin Honnen memory report)

`TransformAsync(XmlReader, TextWriter)` (engine) and `TransformAsync(Stream, TextWriter)`
(facade) stream the serialized result incrementally to the caller's sink rather than
buffering the entire result in a `StringBuilder`. The streaming processor drains the
engine's internal output buffer at each event-loop boundary, so peak working-set delta
for a 1M-item streamed identity transform measured at **17.2 MiB** (vs. hundreds of MiB
to multi-GiB before, depending on result size).

`TransformAsync(Stream, Stream)` also gained a fast path: when the stylesheet's initial
mode is streamable, the input `Stream` is wrapped directly by `XmlReader.Create` instead
of being fully read into a UTF-16 string via `ReadToEndAsync`. Non-streaming transforms
keep their existing buffered semantics.

The total allocation figure on very-large streams (~97 GiB for a 10M-item input) is
dominated by per-event XdmText/XdmAttribute/XdmComment node-store churn and is addressed
in a separate follow-up.

### New: `xsl:for-each` with absolute streamable paths inside `xsl:source-document`

`<xsl:source-document streamable="yes">` containing `<xsl:for-each select="/path">`
now drives the streaming pass per-iteration via a new `ForEachSubscription` mechanism
in the streaming scanner + processor. Previously the for-each evaluated against the
synthetic empty document and returned no items.

The scanner skips for-each inside `xsl:on-empty`, `xsl:on-non-empty`, and
`xsl:where-populated` because those wrappers have conditional execution semantics
that the unconditional subscription dispatch can't honor; those cases fall back to
the buffered path.

W3C streaming conformance gained +4 tests across `si-copy`, `si-document`,
`si-element`, `si-LRE` (1681/2358, 71.3%). Critical 100% sets (`sf-deep-equal`,
`si-attribute`, `si-apply-templates`) held.

### Internal: `StreamingSubtreeMaterializer` precomputes element string-value

Materialized subtrees from the streaming snapshot path now have their `StringValue`
precomputed bottom-up during finalization. Previously `xsl:value-of select="."` on a
snapshot returned empty because `XdmElement.StringValue` is non-lazy (`_stringValue ?? ""`).

## 1.3.22 (2026-05-23)

### Fix: `TransformAsync(XdmSequence)` preserves map/array head across JSON-chained transforms (Martin Honnen)

`XsltTransformer.TransformAsync(XdmSequence)` silently discarded non-node
head items and substituted a synthetic empty document, causing the
consumer stylesheet's `match="."` template to see an `XdmDocument`
instead of the map produced by `parse-json` in the previous transform.
`?key` lookups then tripped:

```
XQueryRuntimeException: Lookup requires a map or array, got XdmDocument
```

`TransformToSequenceAsync` already handled this case (fix shipped in
1.3.20); the string-returning `TransformAsync(XdmSequence)` overload
was missed and continued to fall back to
`engine.TransformAsync("<empty/>", options)`.

Now `TransformAsync(XdmSequence)` mirrors `TransformToSequenceAsync`'s
non-node-head handling, threading the head as the initial context item
via `TransformRawWithInitialContextItemAsync`. For Martin's repro
(parse-json → consume-with-lookups), the consumer now correctly
serializes the lookup-derived output XML.

Regression test in `tests/PhoenixmlDb.Xslt.Tests/JsonChainingTests.cs::
TwoStage_TransformAsyncStringOverload_PreservesMapAcrossChain`.

For JSON-output-method workflows where the consumer produces a typed
map/array intended for further chaining, use `TransformToSequenceAsync`
— `TransformAsync`'s string return type can only faithfully represent
serialized output.

## 1.3.21 (2026-05-22)

### JSON serializer conformance fixes (CPM bump to PhoenixmlDb.XQuery 1.3.15)

No XSLT library code changes from 1.3.20. The XQuery dependency pin moves
1.3.14 → 1.3.15, picking up five JSON serializer fixes that bring the W3C
QT3 `method-json` suite from 64/74 to 73/74 (98.6%):

* Character maps now apply per-character inside JSON string content (not as
  a global Replace on final output), with mapped characters bypassing
  further JSON escaping — matches XSLT/XQuery Serialization 3.1 §11.4.
* Per-string Unicode normalisation moved inside `EscapeJsonString` so
  char-map replacements survive without re-normalisation.
* `json-node-output-method` now parsed from parameter-document maps and
  propagated into `SerializationOptions`.
* `XdmText` nodes embedded in JSON output character-reference-encode
  literal `#xD` as `&#13;` per XML 1.0 §2.11 end-of-line handling.

XSLT result-document JSON output picks this up automatically.

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
