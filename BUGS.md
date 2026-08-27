# Known bugs and open work

> Lives in `phoenixmldb-xslt` because the workspace root is not a git repository and an
> unversioned register is one hardware failure from gone. It spans repos deliberately —
> the engines are split but the defects are not, and several entries below are precisely
> about a fix in one repo being blocked on a package from another.

Findings from the 2026-08-22/24 conformance push. Entries stay until fixed AND measured.
Numbers are measured, not estimated; where a count is a guess it says so.

Companion reading: `.remember/` for session history, and the memory notes
`harness-defects-hide-behind-error-messages` and `conformance-suite-does-not-gate`.

---

## Open — engine

### 1. `xslt` CLI writes output in the wrong encoding
`<xsl:output encoding="iso-8859-1"/>` is honoured for *escaping* — characters the encoding
cannot represent become numeric references — but the bytes written to stdout/file are UTF-8
regardless. Visible as `Straßen` emerging as `Stra\xc3\x9fen` under an ISO-8859-1
declaration, i.e. a file whose declaration and bytes disagree.

Found while chasing normalize-unicode. Distinct from the READ-side encoding bug (fixed).

### 2. Unicode normalization forms beyond the four handled
`normalization-form` accepts NFC/NFD/NFKC/NFKD. Any other value is silently ignored
(`_ => null`), including `fully-normalized`, which the spec defines. Silent no-op rather
than an error.

### 3. `element(*, s:type)` — schema-defined types in node tests
~24 QT3 cases. Needs typed value annotations on validated nodes (the PSVI), which the
engine does not carry. Level 2 of the schema-types work; Level 1 (cast/castable against
schema simple types) shipped 2026-08-23.

### 4. `staticTyping` claimed but not implemented
`XqtsTestRunner.SupportedFeatures` asserts the XQuery Static Typing Feature. We do not
implement it, and neither does Saxon. Costs ~43 guaranteed QT3 failures. Removing it is
what the `<dependency>` mechanism is for, but it IS a conformance claim — needs a decision,
not a quiet edit.

### 5. XSLT defects newly visible (figure superseded — see #16)
Exposed by removing the runner's blanket `_ => true` (see below). Clustered by feature:
`mode` 12, `copy` 12, `namespace` 7, `try`/`error`/`current-output-uri`/`accumulator` 6
each, `output`/`key`/`coco`/`as` 5 each. Not yet triaged — cluster before chasing; every
bucket resolved so far was a single cause.

### 6. QT3: 1187 non-passing cases declare NO optional feature
The honest backlog, and where 100% is a legitimate goal. Largest remaining error clusters:
compilation `AnalysisError` 135, `not a recognized atomic type` 66, module load 40,
`serialization-parameters` element 30, reserved function names 17.

### 10. `object?[]` vs `List<object?>` — array and sequence are told apart only by container type
**PARTIALLY MITIGATED 2026-08-24. The real fix is not done.**

The engines represent an ARRAY as `List<object?>` and a SEQUENCE as `object?[]`. Nothing in
the type system says so: both are containers of `object?`, same shape, compiler cannot help.
Measured **~90 unique discrimination sites** across the two engines (111 + 172 `is object?[]`,
48 + 62 `is List<object?>`, counting the symlinked files twice).

Five bugs came from it, all shipped and all now fixed:

- `xquery` CLI `ResultSerializer` — printed `12` for `[1,2]`
- `SerializeItemAdaptive` — gave sequences array brackets
- `XqtsTestRunner.AsXdmSequence` — flattened arrays; 56 array-sort failures
- `xsl:array` spread — would have flattened a nested array member
- `xsl:array` result — `.ToArray()` handed an array on as a sequence

**Done:** `XdmShape` (PhoenixmlDb.XQuery) states the convention once and names the three
decisions that went wrong — `SequenceItems`, `ArrayMembers`, `AsSequence`, `AsArray` — with
10 tests, each written as the smallest question that would have caught one of the five.
`SequenceHelper` now points at it. The misleading `case object?[] array:` in the CLI
serializer, which is how bug 1 got written, is renamed.

**Not done:** a wrapper type, so the compiler carries the distinction instead of the reader.
That is the actual fix and it is a refactor of ~90 sites across two shipping engines.

**Why this keeps happening, for whoever picks it up.** `SequenceHelper.Flatten` already
encoded the rule correctly — "XDM arrays are single items, do not flatten" — and people
hand-rolled the discrimination anyway. A helper alone has already failed once to prevent
this. Three of the five bugs were in code where the local variable NAME contradicted the
value's shape, and the reader followed the name.

The `xsl:array` instance is the one to remember: `.ToArray()` produced the RIGHT answer for
`select="1 to 5"` and broke only at one element, where a single-member array collapsed to its
member and `composite="yes"` became a silent no-op. The reported symptom was the one input
where the wrong code looked right.

### 11. Adaptive serialization renders a sequence-valued array member with brackets
`<xsl:array select="1 to 5" composite="yes"/>` gives `[[1,2,3,4,5]]`; the member is a
SEQUENCE, so it should be `[(1,2,3,4,5)]`. Same family as #10, on the XSLT serializer side.
Narrow — composite="yes" is rare — but it misreports the member's type.

### 12. `inherit-namespaces` appears not to be honoured on `xsl:element`
Constructing `<xsl:element name="p:outer" namespace="urn:p" inherit-namespaces="no">` with a
nested `xsl:element` child leaves the child with the parent's namespace in scope — the child
reports 2 namespace nodes (xml + p) for both `yes` and `no`. Per XSLT 3.0 the namespace nodes
of the constructed element are not copied to descendants when `no`.

Found while writing a regression test for the import/shadow-attribute fix: the test drove
`_inherit-namespaces` and could not see the parameter reaching the imported module, because
the attribute it was driving had no effect either way. The W3C cases that fix cleared
(copy-0617..0627) all use `xsl:copy`, which does honour it — so this is specific to
`xsl:element` and is not covered by the corpus cases that currently pass.

Not investigated further. Narrow, but silently produces a document with the wrong in-scope
namespaces, which is only observable through the namespace axis.

### 13. XPath 4.0 surface — largely CLOSED 2026-08-25, two items remain
`test/generators` is Dimitre Novatchev's Generator Function Library (Balisage 2026): real
XPath 4.0 code, verified by its author against BaseX and Saxon. It exposed TEN gaps in about
two hours, none of which 31470 QT3 cases had reached. Eight are fixed and the library now
runs — `gn:take(3) => gn:to-array()` gives `[2,3,4]`, and `take(10000000) => value()` gives
`2` without materialising ten million items.

Fixed: digit separators (`1_000_000`, and in fraction/exponent); `import module` binding its
prefix; `declare record NAME(...)` with its constructor; imported record types; `fn` as a
TYPE keyword; named parameters in function types; the `=?>` mapping arrow; `array:empty($a)`
as a predicate distinct from the zero-arity constructor; `fn:while-do`.

**Both remaining items were RETRACTED on investigation — the engine is right in both.**
Kept here because the wrong diagnosis is the useful part.

**13a. `local:f(2)` against `$n as xs:nonNegativeInteger` correctly raises XPTY0004.**
I recorded this as a bug on the reasoning that parameter binding is governed by function
CONVERSION rules rather than instance-of, and asserted "Saxon and BaseX both accept it"
without checking. The conversion rules (XPath 3.1 §3.1.5.2) promote numeric→double/float,
anyURI→string, and cast untypedAtomic — they never NARROW a supertype to a subtype. The
value 2 has dynamic type xs:integer; xs:nonNegativeInteger is a subtype of it, so matching
fails and XPTY0004 is correct. The existing comment in MatchesType says exactly this and is
right. Neither the QT3 corpus nor the Generators library declares a derived-integer
parameter type, so the case for changing it rested entirely on the unverified claim.

**13b. `let $f as fn(item()) as xs:integer := fn($x) { 1 }` correctly raises XPTY0004.**
A `let` binding MATCHES its declared type (XQuery 3.1 §3.10.2); it does not convert. That is
why `let $x as xs:double := 1` is an error while `local:g(1)` against `$x as xs:double`
succeeds — and our engine already draws that line correctly. Function-type matching is
contravariant in parameters and covariant in return: `fn($x) { 1 }` is
`function(item()*) as item()*`, and `item()*` is not a subtype of `xs:integer`, so it does
not match. No coercion is owed.

**What was actually wrong was the error message, and it is now #15.** Both investigations
were launched by messages that named the wrong type. Had the first said "expects
xs:nonNegativeInteger but got xs:integer" instead of "does not match parameterized type
Integer", there would have been nothing to investigate.

### 14. `let` bindings are evaluated eagerly
    let $unused := 1 div 0 return "ok"      raises FOAR0001; Saxon and BaseX return "ok"
    let $unused := name()  return "ok"      raises XPDY0002 with no context item

The spec permits an implementation to raise errors from expressions whose value is never
used, so this is not a conformance violation — but it makes working real-world code fail on
this engine and not on the others. Found because the XPath 3.1 Generators test binds a map
with an unquoted key (`end-reached : false()`, a path step needing context) that is never
evaluated on Saxon or BaseX.

Worth deciding deliberately rather than by accident: laziness for unused `let` bindings is
observable, and the current behaviour is the strict end of the latitude.

---

## Open — harness

### 15. Diagnostics printed CLR type names instead of XQuery ones — FIXED 2026-08-25
`XdmSequenceType.ToString()` rendered the CLR enum member, so every message interpolating a
sequence type named a type the user never wrote. Declaring `$n as xs:nonNegativeInteger` and
passing 2 reported *"does not match parameterized type Integer"* — wrong three times over:
it is not parameterized, the declared type is not xs:integer, and "Integer" erases precisely
the derived/base distinction that caused the mismatch. Companion sites printed
`{value.GetType().Name}`, giving "but got Int64".

The same root cause reached a shipped function: `fn:type(xs:byte(1))` returned the CLR name
**`"XsTypedInteger"`** with kind `"item"`, because tagged subtypes matched no arm of its
switch. That is user-visible output, not a diagnostic.

Fixed by one renderer, `XdmShape.TypeOf`, which `fn:type` and the engine's diagnostics now
share so they cannot drift. `ToString()` renders source syntax and honours
DerivedIntegerType / LocalTypeName; mismatch messages say both what was expected and what
arrived.

This is the sixth pile in the "error names the wrong thing" family, and the first where the
wrong name cost an investigation into correct behaviour rather than merely slowing one down.
A message is not cosmetic when it is the only evidence available.

### 16. Streaming: the flag was stale; the real gate is schema-awareness
`SupportsStreaming = false` was nearly a no-op. Flipping it admitted 186 cases, not the 402
predicted — the test-set heuristic was already letting most streaming tests through, so the
flag gated far less than its name suggested. Of the 186, **132 pass**.

Streaming itself largely works: **2276/2373 = 95.91%** in the `strm` group, against a 12,286
line implementation (`StreamingXmlProcessor`, `StreamWatcher`, `StreamabilityChecker` and
friends). The flag was stale, not protective.

The 54 newly-visible failures are real defects, clustered: accumulator 15, merge 10, mode 9,
streamability analysis 5. `StreamabilityChecker` says of itself that it is "conservative …
does not implement the full posture/sweep classification from the spec", which is also why
`sweep_and_posture` is hard-coded false in `SatisfiesDependency`.

**The larger locked-out population is not streaming.** Of the 2759 cases in test sets
declaring `feature=streaming`:

| gate | cases |
|---|---|
| `feature schema_aware` | **247** |
| `feature streaming` (per-case) | 83 |
| blocked by a set-level feature | 86 |
| `feature dtd` | 8 |

`sx-*` is *schema-aware* streaming. Getting those 247 needs schema validation, which is a
different and much larger piece of work than streaming — and BUGS.md #4 already records that
we claim `staticTyping` without implementing it, so adding a `schema_aware` claim would need
the implementation first, not the flag.

Headline moved 96.66% -> 96.21% because 186 more real tests now run. Same trade as removing
the runner's `_ => true` this morning: a lower number that is true beats a higher one that
is not.

### 17. Absent-focus handling works by accident — do not "fix" it
Four sites read the XQuery context item as

    try { node = context.ContextItem; } catch (InvalidOperationException) { /* absent focus */ }
    node ??= _context.ContextItem;

The catch is UNREACHABLE. `ExecutionContext` is an interface with two implementations and
neither throws that type: `DefaultXsltExecutionContext.ContextItem` returns the `AbsentFocus`
SENTINEL, and `QueryExecutionContext.ContextItem` throws `XQueryRuntimeException` (XPDY0002).

Making the guard work breaks W3C `accumulator-061`, **both** ways it can be made to work:

| attempted fix | effect |
|---|---|
| fold the `AbsentFocus` sentinel to null | `?? _context.ContextItem` fallback fires where it did not before |
| catch `XQueryRuntimeException` instead | same, by a different route |

Either way the accumulator is then read at a different node and the output duplicates. So the
escaping XPDY0002 and the non-null sentinel are both **load bearing**: they exist to STOP the
fallback. The dead catch was harmless precisely because it never fired.

The misleading catches are removed (see `XQueryFocus.ItemOrNull`) so nobody reads them as
working guards, but the behaviour is unchanged and deliberately so.

**Before making absent focus explicit here, work out what the fallback is FOR** — it is
reachable only when the XQuery focus is present, which is not what its `??=` shape suggests.
Two attempts at tidying this produced a regression that the unit suite did not catch; only the
conformance run did.

### 7. XSLT fixtures assert only `passed > 0`
A test-set can fail 76 of 1026 cases and still report green. This is the SECOND layer of
the same problem as `_ => true`: one meant assertions could not fail, the other means
test-sets cannot. Fixing the first was necessary and is not sufficient. Real tallies come
from `scripts/conformance.sh`, which parses the per-test-set `Results:` lines.

### 8. Serialized results that are fragments cannot be asserted on
7 cases in the `fn` group alone. An XDM result tree may have several top-level elements; an
XML document may not, so a serialized fragment will not re-parse. Not wrapped in a
synthetic root deliberately — the corpus writes absolute paths (`/out/a[3]/@att`) and a
wrapper shifts all 11024 of them by a step to rescue 7. Needs fragment parsing that keeps
children at the top level of a document node.

### 9. `assert-posture-and-sweep`, `assert-warning` fail rather than skip
919 and 6 occurrences. The runner cannot reach streamability analysis or collect warnings,
so it cannot judge these either way. They FAIL, because a check you cannot perform is not a
check you passed — but the right answer is a real "not applicable" state, which the runner
has no concept of.

---

## Fixed 2026-08-22/24 — kept for the pattern

**Engine.** `fn:partition` two-arg split · `fn` lambda shorthand · `fn:parse-html` raising
FODC0006 instead of returning escaped input · prefixed `key()` across modules (XTDE1260) ·
typed-body reparse namespaces (XTTE0505) · `fn:highest`/`fn:lowest` signature + a
process-killing `Convert.ToDouble` crash · `all-equal`/`all-different`/`duplicate-values`
comparing lexical forms · `fn:QName` rejecting `xs:anyURI` · `xsl:array` ignoring its `select` attribute · XSD regex POSIX guard
false-positiving on `[\i-[:]]` · `import schema` never binding its prefix ·
schema-defined simple types as cast targets · `round-half-to-even` deciding ties from the
decimal spelling · XPath `&#xD;` normalized to LF · `xslt` CLI reading files as UTF-8 ·
Unicode normalization applied to element and attribute NAMES.

**Harness.** QT3 `<module>` declarations never parsed (314) · static base URI never set ·
arrays flattened into sequences · `<serialization-matches>` unimplemented (576) ·
`assert-serialization`/`assert-serialization-error` unimplemented (70) · `<test file=>` and
`<assert-xml file=>` never read (41 each) · XSLT `_ => true` passing ~12800 assertions ·
assertions parsed with query-source line-ending normalization.

**Two patterns worth remembering.**

*The error message named the wrong thing* — five separate piles hid this way:
`FORX0002: POSIX character class` for a pattern containing no POSIX syntax;
`XPST0081: Unbound namespace prefix` for a prefix that was bound (three layers from the
cause); `mismatched input '<EOF>'` for a query never loaded; `Could not find a part of the
path` for a file that was present; `XTTE0505: type mismatch` for a namespace failure.
Cheap countermeasure: confirm the input actually uses the feature the error names.
`grep '\[\[:'` would have found the regex bug in ten seconds.

*The corpus declares something the runner never reads* — every harness defect above.
Cheap countermeasure: enumerate what the input format can express, then check the parser
handles each. One command compared every `<assert*>` element against the runner's switch
and found three gaps at once.
