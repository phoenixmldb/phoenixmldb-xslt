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

### 18. `xslt` CLI blocks forever on stdin when given `-it` and no source document

Found 2026-08-29 while auditing stray processes, not by any test. Two `xslt` CLI invocations
were alive at 0% CPU having produced **zero bytes** of output — one for 21 hours, one for
**3 days 19 hours**.

**Cause, from a managed stack** (`dotnet-stack report -p <pid>`):

```
Thread (0x225235):
  System.Console!Interop+Sys.Read(...)
  System.Console!System.ConsolePal.Read(...)
  System.Console!System.IO.ConsoleStream.Read(...)
  System.Private.CoreLib!System.IO.Stream+<>c.<BeginReadInternal>b__41_0(...)

Thread (0x225228):
  ...TaskAwaiter`1[System.Int32].GetResult()
  xslt!Program.<Main>(class System.String[])
```

The CLI is reading standard input; `Main` is simply awaiting it. When a named template is
invoked with `-it` and no source document is supplied, the CLI still tries to read a source
from stdin. Under an interactive terminal that surfaces as an apparent freeze; under a script,
CI job, or agent harness where stdin is an inherited pipe that never reaches EOF, it blocks
forever.

**Proof:** the identical commands with stdin closed complete immediately and correctly.

```bash
X=src/PhoenixmlDb.Xslt.Cli/bin/Debug/net10.0/xslt.dll
dotnet $X /repos/phoenixml/hang-repro/efr.xsl -it '{http://www.jenitennison.com/xslt/xspec}main'
#   hangs indefinitely, 0 bytes
dotnet $X /repos/phoenixml/hang-repro/efr.xsl -it '{http://www.jenitennison.com/xslt/xspec}main' < /dev/null
#   exit 0, 6545 bytes of correct output
```

Both preserved inputs behave identically (`efr.xsl`, an XSpec-generated stylesheet; `frag.xsl`,
a small unrelated case). Inputs kept at `/repos/phoenixml/hang-repro/`.

**Fix:** when `-it` / `--initial-template` names a template and no input file is given, do not
read stdin at all. More generally, only consume stdin when a source document is actually
required and the user has not supplied one by path. A `--no-input` escape hatch would be
belt-and-braces but should not be necessary.

**Secondary, worth doing anyway:** the CLI has no watchdog. A transform that cannot make
progress should fail loudly rather than wait forever.

**Note on how this was diagnosed.** The first hypothesis recorded here was sync-over-async
deadlock in the lazy variable path — there are seven sites calling
`LazyValue.GetValueAsync().AsTask().GetAwaiter().GetResult()`, and `futex_wait_queue` was
consistent with it. That was wrong. It was also wrong that `dotnet-stack` could not attach to a
.NET 10 runtime: the diagnostics tools version independently of the runtime and 9.0.x attaches
fine. The real failure was selecting a pid with `pgrep -f <script-name> | head -1`, which
returns the **bash wrapper**, not the `dotnet` process. Target the process whose `comm` is
`dotnet`. One correct stack replaced two confident wrong answers.


### 19. `$err:code` has no namespace URI, and fixing it breaks QName rendering

Found 2026-09-02. `xsl:catch` builds `$err:code` with the interned NamespaceId but **no
`ExpandedNamespace`**. The components are otherwise right:

```
local-name-from-QName($err:code)     -> XTDE3086     correct
prefix-from-QName($err:code)         -> err          correct
namespace-uri-from-QName($err:code)  -> ""           WRONG
```

So `$err:code` cannot equal `QName('http://www.w3.org/2005/xqt-errors','XTDE3086')`. This blocks
**9 assertions** in XSpec's `external_global-context_stylesheet` (the err:code/description/module
trio across three scenarios), and is why fixing the *error code itself* — the engine raised
XPDY0002 where XTDE3086 is required — moved the corpus by zero.

**Attaching the URI is measurably worse on its own.** Measured on the 284-suite corpus:

| | Complete | passing | failing |
|---|---|---|---|
| as-is | 137 | 1150 | 209 |
| URI attached | 136 | 1154 | 202 |

Attaching it flips the **XQuery** engine's QName stringifier to the EQName form `Q{uri}local`,
where casting `xs:QName` to `xs:string` must give the lexical `prefix:local` (XPath 3.1 §19.2).
XSpec compares the serialized lexical form, so three suites regress (`yes-no-utils` 14/0 -> 12/2,
`xsl-result-document` 2/4 -> 0/6, `external_xslt-package_arith_private` 2/1 -> 1/2). Patching
`StringValueOf` in the XSLT engine covers `xsl:value-of` but not `fn:string`, which lives in the
XQuery package — so the halves diverge, and that divergence is itself the defect generator.

**Order of operations: fix the QName stringifier in XQuery/Core first, release, bump the pin,
THEN attach `ExpandedNamespace` here.** Doing it in the other order is negative. This is the
highest-leverage open item — it unblocks the 9 assertions plus anything else comparing an error
code against a URI-built QName.

### 20. A node `global-context-item` cannot cross into fn:transform's inner engine

Found 2026-09-02, after making `global-context-item` the focus for global-variable evaluation.
A NODE passed as that option is handed through as-is. Wrapping it as a `CrossStoreNodeRef` — the
way `function-params` are — makes the inner engine re-parse it, which mints a **new node**, and
these suites assert node IDENTITY (`$x:result is $x:context`).

Measured both ways (censuses 45/46): pass-through gives 137 Complete / 1150 passing; wrapping
gives 136 / 1154. Wrapping gains assertions in four `external_*` suites and regresses
`external_multiple-context-items_function` from Complete 1/2 to an XPDY0002 abort. **Neither
dominates**, so do not pick by census total.

The real fix is to wrap only when the stores genuinely differ — the inner engine often shares the
caller's store, where the node resolves natively and identity holds. Establish which paths share
a store before choosing.

### 21. No XML Catalog support — 3 XSpec suites, and it is a feature not a bug

`uri-utils`, `schut-to-xslt` and `generate-xproc-imports` fail with
`FODC0002: No document could be retrieved for URI 'catalog-01:/...'`. They resolve a private URI
scheme through an OASIS XML Catalog, which XSpec supplies to Saxon via processing instructions the
.NET runner does not read (`<?xspec-test saxon-custom-options=-catalog:"..."?>`).

Verified 2026-09-02: the engine has **no** catalog support and **no** URI-resolver hook. (The
`packageCatalog` in `XsltFacade` is the XSLT *package* catalog, unrelated.) Clearing this means
deciding whether the engine should support XML Catalogs and through what API, plus catalog
parsing, `rewriteURI`/`public` entries, and plumbing into `fn:doc`/`fn:document`/module
resolution. **Do not spend triage time treating it as a defect.**

### 22. `x:like` loses a namespace inherited from an IMPORTED x:description

Found 2026-09-02. FONS0004, 2 suites (`threads_description_stylesheet`,
`threads_scenario_stylesheet`), stage Compile. A namespace declared on an imported
`x:description` and used only inside an ATTRIBUTE VALUE is lost when `x:like` expands a
`shared="yes"` scenario, so `x:call/@function="sleeper:sleep"` fails to resolve.

Stage located by probing the real compiler:

| point | in-scope prefixes on `x:call` |
|---|---|
| entering `x:gather-specs` | `xml,x,sleeper` |
| `$specs-doc` (after gather) | `xml,x,sleeper` |
| after `mode="x:unshare-scenarios"` | `xml,x` — **lost** |

**Minimal repro** (needs the real XSpec compiler): an imported `shared.xspec` declaring
`xmlns:sleeper` with a `shared="yes"` scenario whose `x:call/@function` uses the prefix, plus a
`user.xspec` that imports it and references it with `x:like`. Putting both scenarios in ONE file
**passes** — the cross-document import is required, and that is the cheapest lever.

**Ruled out — seven isolations, all pass, do not re-run:** shallow-copy of an element with its
own namespace; with an inherited one; `xsl:copy`; `xsl:copy-of`; `fn:document()` loading;
`xsl:element inherit-namespaces="no"` wrapping copied children; a typed `as="element()+"`
template whose body shallow-copies; and a second copy through an intermediate `xsl:document`.
So this is NOT "copy drops namespace declarations". Probe that seam on the REAL repro — every
synthetic reconstruction so far has failed to reproduce.


### 23. `$v/root()` returns empty as a path step; `root($v)` and `$v/root(.)` do not

Found 2026-09-02. A zero-argument `fn:root()` used as a step does not receive the per-item
context, and yields nothing rather than the root or an error:

```
root($v)         -> 1 item   correct
$v/root(.)       -> 1 item   correct
$v/root()        -> 0 items  WRONG   (0-arg defaults to the context item, so this is root(.))
```

It returns EMPTY rather than raising XPDY0002, so `Root0Function` — which reads
`ctx.ContextItem` and throws when absent — is evidently not being invoked with the step's focus
at all. Path-step evaluation lives in the **XQuery** package, so this needs the same
release-and-repin chain as #19.

Blocks XSpec `select-node`, whose assertion is
`$x:result is $myv:source/root()/conbody[1]/p[1]/text()[1]` — the right operand is empty, `is`
on an empty operand yields the empty sequence, and XSpec terminates on "Non-boolean @test".

**Second, separate question in the same suite:** a variable declared `as="element(conbody)"` from
a constructor body is PARENTLESS here — `root($v)` returns the element, not a document node — so
`root()/conbody[1]` would still find nothing even once the above is fixed. XSLT 3.0 §9.3 says a
typed body constructs a sequence rather than a temporary tree, which argues our behaviour is
right; that the XSpec suite expects otherwise argues Saxon wraps it. **Settle this against the
spec before changing it** — do not infer the rule from the test.

### 24. Not every non-completing XSpec suite is an engine defect

Recorded 2026-09-02 so the published figures are not misread. Of the suites that do not run to
completion, at least these fail for reasons that are **not** engine deficiencies:

| suite(s) | why | tracked |
|---|---|---|
| `uri-utils`, `schut-to-xslt`, `generate-xproc-imports` | need an OASIS XML Catalog; the engine has no catalog support at all | #21 |
| `helper_xslt-package` | needs Saxon's `-config:` package library. **The engine already supports this** (`XsltFacade` takes a `packageCatalog`); phxspec simply does not read `<?xspec-test saxon-custom-options?>` | harness |
| `version-utils` | inherently Saxon-specific — the scenario is labelled "Assume we test this on Saxon versions from 11.7 to 13.x" and tests `$x:saxon-version`, which is empty on any non-Saxon processor. **Cannot pass here, ever.** | inapplicable |

Twelve top-level suites carry `saxon-custom-options`; phxspec ignores all of them. When
reporting XSpec conformance, say how many of the shortfall are engine defects and how many are
harness or inapplicable — a bare "N suites do not complete" reads as N engine bugs and overstates
the case against the engine.


### 25. An `xmlns=` default leaks into `xs:QName()` casts — FIXED 2026-09-03

Found 2026-09-03, exposed by fixing fn:deep-equal for QNames (#19 chain). In a stylesheet
declaring a default element namespace and NO `xpath-default-namespace`:

```xml
<xsl:stylesheet xmlns="urn:default-elem" ...>
  namespace-uri-from-QName(xs:QName('foo'))   ->  "urn:default-elem"   WRONG, want ""
  namespace-uri-from-QName(QName('','foo'))   ->  ""                   correct
```

An `xmlns=` declaration sets the default namespace for **literal result elements**. Unprefixed
name resolution in XPath is governed by `xpath-default-namespace`, which is a different thing;
the two are being conflated.

**This was hiding behind a false pass.** XSpec's `catch_stylesheet` asserts
`?err?code` against `xs:QName('error-code-of-my-template')`. The .xspec source has no default
namespace, but the COMPILED stylesheet carries `xmlns="http://www.jenitennison.com/xslt/xspec"`,
so the expected value came out in the XSpec namespace while the actual error code has none:

```
RESULT -> QName('', 'error-code-of-my-template')
EXPECT -> QName('http://www.jenitennison.com/xslt/xspec', 'error-code-of-my-template')
```

Those three assertions "passed" only because `fn:deep-equal` compared QNames by their **debug
ToString()**, where both render as the bare local name and the namespace difference is invisible.
Once deep-equal compared by value, the pre-existing defect surfaced. `catch_stylesheet` going
18/3 -> 15/6 is therefore **not a regression in capability** — it is three false passes becoming
honest failures, and it should not be "fixed" by reverting the deep-equal change.

**Fixed** by giving XPath its own view of the prefix bindings. The parser records `xmlns=` and
every prefix in one dictionary keyed by prefix; handing that to the XQuery engine unchanged made
the empty key mean "xmlns=", and the xs:QName cast reads that key as the default element/type
namespace. The engine now substitutes `xpath-default-namespace` for the empty key (removing it
when unset), built once per stylesheet.

Verified both directions: `xmlns=` alone no longer reaches a cast, and an explicit
`xpath-default-namespace` now does. Corpus effect: `catch_stylesheet` 15/6 -> **18/3**, no suite
worse — the three assertions it recovers are the false passes described above, now passing for
the right reason.


### 26. `as="xs:integer"` accumulator + `xs:integer()` in the rule = always the initial value — FIXED 2026-09-04

Found 2026-09-04 while clustering the W3C `decl` group. **Two factors, each harmless alone:**

| `as=` | rule expression | result (input 10,20,61) |
|---|---|---|
| `xs:integer` | `$value + xs:integer(.)` | **0** WRONG |
| `xs:integer` | `$value + number(.)` | 91 correct |
| *(none)* | `$value + xs:integer(.)` | 91 correct |
| `xs:integer` | `$value + 1` (counter) | 3 correct |

So the accumulator machinery, the rule matching, and the context item are all fine — verified
directly: inside a rule `name(.)` gives `c`, `string(.)` gives the text, `xs:integer(.)` alone
gives 10/20/61. Only the COMBINATION of a declared `as="xs:integer"` and a rule whose value comes
from the `xs:integer()` constructor collapses to the initial value.

**Prime suspect (unconfirmed):** `xs:integer()` returns an `Xdm.XsTypedInteger` wrapper rather
than a bare `long` — the XQuery engine unwraps exactly that in several places
(`if (a is Xdm.XsTypedInteger tiA) a = tiA.Value;`). The accumulator's `as=` coercion probably
does not, so each rule result fails to become the new value and the accumulator never advances.
Check `CoerceToType` against `XsTypedInteger` before looking anywhere else.

**Reproduce** (`<r><c>10</c><c>20</c><c>61</c></r>`, expect 91):

```xml
<xsl:mode use-accumulators="#all"/>
<xsl:accumulator name="a" as="xs:integer" initial-value="0">
  <xsl:accumulator-rule match="c" select="$value + xs:integer(.)"/>
</xsl:accumulator>
```

**Worth doing:** `accumulator` is the largest single cluster in the W3C `decl` group — **26 of its
93 failures** — and real accumulators commonly declare `as="xs:integer"` and read `xs:integer(.)`,
which is exactly this shape. W3C `accumulator-036` ("sequence constructor in accumulator rule") is
a good end-to-end check: it expects `items=5, cost=91` and currently gives `cost=0`.

**Fixed.** The suspect was right but for a different reason than guessed: the value is a
**BigInteger**, not an `XsTypedInteger`. `xs:integer` is unbounded in XSD, so `xs:integer()` and
the overflow-safe arithmetic can both produce one, and `MatchesAtomicType` accepted only
`int or long`. The XQuery engine's `MatchesItemType` had accepted BigInteger all along — this
separate matcher had not.

Found by surfacing the deferred error rather than by more reasoning; three successive inferences
about the CLR type were wrong, and the engine's own message settled it in one run:

    XPTY0004: Accumulator 'i_xsint' declared as Integer but value is BigInteger

**Measured effect is smaller than hoped: 7 of the 26, not all of them.** W3C `decl` went
987/1080 (91.4%) to **994/1080 (92.0%)**, accumulator failures 26 to 19. The prediction that one
cause covered the cluster was wrong; the remaining 19 are something else.

Still unexplained, seen while isolating: a rule on an `as="xs:string"` accumulator appended SEVEN
entries for three matched elements, and `$value` read empty each time. Not addressed by this fix
— likely part of the remaining 19.

### 27. W3C `decl` failures, clustered (2026-09-04)

The 93 failures in the `decl` group, by test-set prefix, so the next session can pick by size
rather than re-derive this:

| cluster | failures |
|---|---|
| `output` | 20 — now the largest |
| `accumulator` | 19 (was 26; #26 fixed 7) |
| `function` | 10 |
| `use-package` | 9 |
| `package` | 7 |
| `override-*` | 10 combined |
| everything else | 11 |

`decl` is the weakest W3C group at **91.4%**; `strm2` is next at 92.1% (59 failures). Everything
else sits near 98%. Two groups carry 152 of the 406 total failing cases.

`conformance-results/decl.log` records actual-vs-expected per failure and is regenerated by
`./scripts/conformance.sh decl` (~3 min, versus 38 for the full sweep).


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


### 28. Expected-error tests passed on ANY exception — BOTH runners — FIXED 2026-09-04
The third and worst layer of the `_ => true` problem in #7 and #9, and the one that was
actually inflating the published conformance number.

`<error code="XPST0003"/>` and `<assert-serialization-error code="SESU0007"/>` carry the
expected code in an ATTRIBUTE. Both runners read `Element.Value` — the element TEXT, which is
`""` for an empty element — and then treated empty as "any error will do":

```csharp
var expectedCode = assertion.Value;                       // always "" for <error code="..."/>
if (ex.Message.Contains(expectedCode ?? "") || string.IsNullOrEmpty(expectedCode))
    return true;                                          // → ANY exception passes
```

So a test that expected `XTSE0010` passed by throwing `Unknown XSLT instruction: expose`
(`package-903`), and any test whose stylesheet failed for an unrelated reason scored a pass on
the strength of having failed.

The clearest case is `fn:load-xquery-module`, which the engine does not implement: **0/4 after
the fix, 4/4 before it.** A wholly missing function scored 100% because each call threw.

`accept-*` shows the subtler shape — **23 of 23 failures raise the wrong code**, but not because
`xsl:accept` is unimplemented (it is parsed; see `StylesheetParser.cs:1372`). Every one of them
dies earlier on `XTDE3052: Package 'http://localhost/pkg' not found`, so the group was scoring
27/50 while actually testing nothing about visibility at all. Package resolution for the corpus,
not `xsl:accept`, is what that cluster is really blocked on.

The XSLT runner never parsed the `code` attribute at all. The XQuery runner DID parse it into
`XqtsAssertion.Code` and then never consulted it — the parse and the check disagreed silently.

`<any-of>` carried the same blanket-pass one level down in both runners, and this one is pure
logic rather than a field mix-up:

```csharp
if (assertion.Children.Any(a => a.Type == "error")) return true;   // code never examined
```

Fixed with a single recursive `MatchesExpectedError` per runner: match on `Code`, fall back to
element text, and treat a genuinely code-less `<error/>` as "some error must be raised" — which
is what it means. `<any-of>` now recurses instead of pattern-matching on child type.

**This is an asymmetric pair defect (#—see the pattern note) in the harness rather than the
engine, which is why no amount of engine work would have surfaced it.** The two runners had
different halves of the bug: XSLT never parsed the code, XQuery parsed it and ignored it.

Measured effect across the whole W3C XSLT corpus. The engine did not change at any point in
this sequence — only the scoring did:

| | published 2026-09-02 | code checked | + code read from `ErrorCode` |
|---|---|---|---|
| cases passing | 10,224/10,630 (96.2%) | 9,740/10,630 (91.6%) | **10,020/10,630 (94.3%)** |
| failures | 406 | 890 | **610** |
| of which "wrong code" | (scored as passes) | 511 | **222** |

Checking the code properly cost 4.6 points; reading it from the right place gave 2.6 back. The
net correction to the published figure is **1.9 points and 204 previously invisible failures**.
`decl`, the group this started from, went 994/1080 → 944/1080 (136 failures, 66 wrong-code).
The final column also includes two engine fixes this exposed (XTMM9000, XTDE0700 — below).

11 of the change's own fixes went the other way: `output-0182`..`0192` expect
`assert-serialization-error`, the engine raises exactly those codes, and the XSLT runner had no
case for that assertion at all — it hardcoded `false` while the XQuery runner had handled it
all along. Those were the engine being right and the harness scoring it wrong.

**Half of the resulting "wrong code" failures were not wrong at all.** `XQueryException` and
several relatives carry the code in a structured `ErrorCode` property and deliberately keep it
out of `Message`; the XSLT layer then re-wraps those with file and line information. So the
engine reported `FORX0002` correctly and the runner, comparing against `Message`, could not see
it:

```
Error: [file:///…/re.xsl:33] [line 1, col 52] Invalid regular expression '^(+a)$': …
                                              ^ correct diagnosis, code held in ErrorCode
```

Both runners now also consult `ErrorCode` down the whole inner-exception chain. That alone
recovered **96 cases in the `misc` group** (1718 → 1814) and cut its wrong-code count from 162
to 67 — no engine change, just reading the code from where the engine put it.

The two clusters that looked next-largest — `Function g#1 not found` (expects `XPST0017`) and
`The context item for '/' is not in a tree rooted at a document node` (expects `XPDY0050`) —
turned out to be the SAME cause, not a separate one: both are already raised as
`XQueryRuntimeException("XPST0017", …)` / `("XPDY0050", …)` at `PhysicalOperators.cs:8784` and
`:173`, structurally correct and invisible to a message comparison. They should be cleared by the
same fix. **Re-cluster what is left before treating any of it as a missing-code defect** — the
first pass at reading this list mistook structural codes for absent ones.

Results lines now report the split, because "raised the wrong code" and "raised nothing" are
different defects and only the second is a missing check:

```
Results: 27/50 passed (54.0%) — 23 of 23 failures raised an error with the wrong code
```

**Consequence: every conformance figure published before 2026-09-04 is overstated**, including
the 96.2% in the README, which was itself a correction of a stale 97.9%. The XQTS 95% CI gate
is measured by the same code and is overstated for the same reason.


### 29. Two error sites omitted the code their siblings carried — FIXED 2026-09-04
Found by the wrong-code split that #28 made visible. Both are the asymmetric-pair shape, and in
both cases the twin sitting a few lines away had the code all along:

| site | was | now | sibling that was already right |
|---|---|---|---|
| `XsltTransformer.cs:22137` | `Transformation terminated: {message}` | `XTMM9000: …` | `XTDE0030` at `:22122`, same method |
| `XsltTransformer.cs:10221` | `Required parameter $x not supplied` | `XTDE0700: …` | `XTDE0700` at `:10257`, `:11057`, `:12172` |

`:10221` is the explicitly `required="yes"` parameter; `:10257` is the case where an `as=` type
makes a parameter effectively required. Same error, same spec code, and only the derived one
named it.

Measured: `decl` 943 → 944, `insn` 1494 → 1497. Unit gate 1480, unchanged.

The diagnosis was never wrong in either case — the prose said exactly what happened. Only the
code was missing, which is invisible to a human reading the message and decisive for anything
matching on it. Cheap to fix, and worth doing for users independently of the score.


### 30. `fn:load-xquery-module` flattened every failure to FOQM0002 — FIXED 2026-09-04
The `fn` fixture went red once #28 made expected-error checks real: the
`load-xquery-module` test-set scored 0/4 and tripped the `passed > 0` gate (#7).

All four cases were failing for one reason. The implementation synthesizes
`import module namespace __lxqm = "…"` and compiles it with the real engine — so the analyzer
had already classified the failure precisely, `XQST0059` for an unresolvable module namespace —
and then the wrapper threw away that code and reported `FOQM0002` for everything:

```csharp
var msg = string.Join("; ", compResult.Errors.Select(e => e.Message));
throw new XQueryRuntimeException("FOQM0002", $"Module '{moduleUri}' cannot be loaded: {msg}");
```

"No such module", "the module has a syntax error" and "the module imports something missing"
were all reported identically. Now the underlying `AnalysisError.Code` is preserved.
`load-xquery-module-001` expects `XQST0059` and passes; the fixture is green on one real pass
rather than on a suppressed check.

**The other three still fail and are still counted.** They need `<resource uri= file=
media-type="application/xquery">` from the test environment to be registered as a module
mapping, and nothing carries it: `XsltTestRunner.ParseEnvironment` handles `stylesheet`,
`source` and `collection` but not `resource`, and there is no path from XSLT transform options
to the XQuery `CompilationOptions.ExternalModules` the analyzer reads. `fn:load-xquery-module`
also builds its sub-engine with only `BaseUri`, so it would drop a host-supplied module map even
if one were available — a real defect independent of the harness. **Left OPEN deliberately:**
8 `<resource>` elements exist corpus-wide, 1 of them XQuery, which does not justify new public
API on the XSLT engine.

### 31. `conformance.sh` double-counted every failing test-set — FIXED 2026-09-04
When a fixture assertion fails, xunit echoes that test's output a second time behind an
`[xUnit.net …]` prefix. `nfail` anchored on `^ FAILED: ` and was right; the case tally used an
unanchored `grep -oE "Results: …"` and counted the test-set twice. `fn` reported 1135 cases
instead of 1131 the moment `load-xquery-module` went red — the inflation was exactly the size of
each failing test-set, and it appeared only when something was already wrong. The two numbers
disagreed silently because only one of them was anchored.

Every corpus total measured while a fixture was red is affected, including the intermediate
figures in entry 28: the 10,634 case totals there should read 10,630.

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
