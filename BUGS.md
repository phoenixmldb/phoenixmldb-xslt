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


### 32. Required attributes dereferenced with `!` — NullReferenceException — FIXED 2026-09-04
`key-091` omits `name` on `xsl:key` and expects `XTSE0010`. The parser did:

```csharp
ValidateQNameValue(element.Attribute("name")!.Value, "name", GetSourceLocation(element));
```

so the author got `Object reference not set to an instance of an object.` instead of a
diagnosis. The `!` asserts a required attribute is present, which is exactly the thing the
stylesheet is being checked FOR — the assertion is only true when there is nothing to report.

This is a class, not one bug. `Attribute("x")!.Value` appeared **25 times** in
`StylesheetParser.cs`. Searching the conformance logs for the NRE message found 8 reachable
instances, 5 of them this cause:

| test | element | missing attribute |
|---|---|---|
| `key-091` | `xsl:key` | `name` |
| `error-0010u` | `xsl:key` | `match` |
| `error-0010q` | `xsl:attribute-set` | `name` |
| `error-0010ad` | `xsl:call-template` | `name` |
| `error-0010an` | `xsl:variable` | `name` |

All five now raise `XTSE0010` via the existing `?? throw` idiom. Measured: `fn` 1068 → 1069,
`misc` 1815 → 1819. Unit gate 1480, unchanged.

**18 `!.Value` dereferences remain** and are deliberately untouched: no test reaches them, and
several are attributes that are genuinely optional in some contexts (`xsl:output/@name`) or
already guarded by a ternary. Fixing them blind risks rejecting valid stylesheets — the
`!` is only wrong where the attribute is required AND its absence is a diagnosable error. The
enclosing methods are `ParseParam`, `ParseCharacterMap`, `ParseDecimalFormat`, `ParseAccumulator`,
`ParseForEach`, `ParseForEachGroup`, `ParseIterate`, `ParsePI`, `ParseNamespaceInstr`,
`ParseAssert`, `ParseParamInstr`, `ParseAnalyzeString`, `ParseMapEntry`, `ParseWithParam`.

Three NREs remain with OTHER causes and are still open: `accumulator-025`, `output-0501`,
`select-7501`.


### 33. XQTS audit: the suite never hung, and 95.11% was measured with the #28 fail-open
**The timeout was never a hang.** `xqts` is 31,470 cases — an order of magnitude past any XSLT
chunk — and completes in **2,241 s (37 min)**. The script's 900 s per-chunk default killed it
every time and printed `TIMEOUT`, which read as a wedge and went uninvestigated for weeks. This
is the same "not hung, just big" shape the script already documents for `strm`, which was split
into three chunks for exactly this reason. Fixed by giving `xqts` its own budget
(`CONFORMANCE_XQTS_TIMEOUT`, default 3600) rather than raising the global default, which would
let a genuinely wedged XSLT chunk sit for an hour.

**Measured 2026-09-05, honest error-code checking:**

| | |
|---|---|
| Total | 31,470 |
| Passed | **28,887 (91.79%)** |
| Failed | 941 |
| Errors | 1,642 |
| `assert-eq` string-compare rescues | 8 |

**The 95% CI gate fails by 3.21 points.** The last recorded XQTS result was 95.11% GREEN on
2026-08-23 — measured by the runner described in #28, whose `IsExpectedError` read the expected
code from element text when QT3 writes it as an attribute, so the comparison was always against
`""` and passed for ANY exception.

The population that check was auto-passing: **7,403 QT3 cases carry an `<error code=…>`
assertion**, just under a quarter of the suite. The ~1,040-case drop from 95.11% to 91.79% sits
entirely inside that population, which is consistent with the fail-open being the cause — though
engine changes also landed between the two dates, so this is attribution by magnitude, not a
controlled comparison. A controlled one would need the old runner re-run against today's engine.

**The gate needs a decision, not a code change.** At 95% it is now permanently red and therefore
useless as a regression detector; ratcheting it to the measured value (say 91.5%) restores that
function and keeps the real target documented. Lowering a quality gate is a call for a human to
make, so it is deliberately NOT changed here.

Audit of the rest of the XQuery runner found no other fail-open: the assertion dispatch's
catch-all is `_ => false`, `all-of`/`any-of` recurse properly, missing test data raises
`Assert.Skip` rather than returning green, and the `assert-eq` string-compare rescues are counted
and reported (8) rather than hidden.


### 34. OPEN — the 1,001 XQTS cases the fail-open was hiding, clustered
Follow-on to #33, answering "which bugs take it from 95 to 91.79". Nothing *takes* it there —
these were always failing; #28's blanket-pass scored them green because the query threw
*something*.

Of 2,583 unique failing cases (941 failed + 1,642 errors):

| | count |
|---|---|
| expected an error, threw the WRONG one | **1,001** ← previously auto-passed |
| expected an error, returned a result instead | 163 (always failed — nothing to rescue) |
| no expected error: wrong result or crash | 1,419 (always failed) |

1,001/31,470 = **3.18 points**, against an observed drop of 3.32. That closes the attribution
gap left open in #33: it is the fail-open, not engine drift.

**536 of the 1,001 are raw CLR exceptions escaping the engine** — not an XQuery error at all:

| exception | n | should have been |
|---|---|---|
| `OverflowException` | 184 | FOCA0002×83, FORG0001×73, FOAR0002×12 |
| `InvalidCastException` | 148 | XPTY0004×126, FORG0006×16 |
| `FormatException` | 114 | FORG0001×105 |
| `XmlSchemaValidationException` | 71 | XQDY0027×35, XQDY0084×13 |
| `HttpRequestException` | 11 | XQST0059×6, XQST0009×5 (network-dependent, environmental) |

By test-set, three code paths cover ~418 of the 536:

- **casting — 228** (`prod-CastExpr` 178, `prod-CastExpr.derived` 50). `xs:T('bad lexical')` lets
  .NET `FormatException` / `InvalidCastException` / `OverflowException` escape instead of raising
  FORG0001 / FOCA0002 / XPTY0004.
- **duration arithmetic — ~130** (`op-*-dayTimeDurations`, `op-*-yearMonthDuration`, `fn-avg`,
  `fn-abs`). Huge durations overflow `Int32`/`TimeSpan`; the overflow escapes as-is.
- **schema validation — 60** (`prod-ValidateExpr` 35, `prod-SchemaImport` 25).
  `XmlSchemaValidationException` escapes where XQDY0027/XQDY0084 is required.

**Why this was never wrapped, probably:** `CastExpressionOperator.ExecuteAsync` is an
`async IAsyncEnumerable<object?>` iterator, and C# forbids `yield return` inside a `try` with a
`catch`. So the obvious "wrap the cast body" fix does not compile, and the conversion calls sit
directly in the iterator. The fix is to move conversion into a non-iterator helper that CAN
try/catch and translate, then yield its result — not to sprinkle catches at call sites.

The remaining 465 of the 1,001 do raise a proper `XQueryRuntimeException`/`XQueryException` with
the wrong code; those need case-by-case work and are not a single cluster.


### 35. Cast/numeric CLR leaks — PARTIALLY FIXED 2026-09-05
Four fixes against the #34 clusters. **XQTS 28,887 → 28,976 (91.79% → 92.07%), +89.**
XSLT corpus 10,020 → 10,024, no regression. Unit gates 1532 (XQuery) / 1480 (XSLT), unchanged.

| fix | site | gain |
|---|---|---|
| 14 range checks threw raw `OverflowException` | `TypeConstructorFunctions.cs` | ~56 |
| `CastValue` let CLR conversion exceptions escape | `PhysicalOperators.cs` | ~14 |
| `ValidateNumericArg` catch-all passed non-numeric XDM types to `Convert.ToDouble` | `NumericFunctions.cs` | ~4 |
| `fn:avg` on a `BigInteger` crashed | `NumericFunctions.cs` | ~26 |

**The `TypeConstructorFunctions` one is the asymmetric pair again.** Its 14 integer-subtype range
checks threw `OverflowException`; the parallel implementation in
`TypeCastHelper.ValidateIntegerSubtype` has always thrown `FORG0001` for the identical check.
`XQueryRuntimeException` was already used 93 times in that same file — the 14 were the outliers.

**`fn:avg` was a real user-facing bug, not just a code mismatch.** `BigInteger` (how an
`xs:integer` outside `long` range is held) does not implement `IConvertible`, and `avg`'s
accumulation ended in an unconditional `Convert.ToDouble(item)` outside its `else if` chain — so
averaging any large integer crashed with a CLR exception. `fn:sum` is unaffected: its chain is
closed and nothing falls through.

**Where `CastValue`'s wrapper had to go.** `CastOperator.ExecuteAsync` is an
`async IAsyncEnumerable` iterator and C# forbids `yield return` inside a `try`/`catch`, so the
wrapper lives in `CastValue` itself — which also covers its other eight callers. Mapping:
`FormatException` → FORG0001 (bad lexical form), `InvalidCastException` → XPTY0004 (source type
has no conversion), `OverflowException` → FOCA0002 (numeric range). Verified safe: every caller
that relied on a cast failing uses a bare `catch`, so none of them stopped catching.

**I predicted ~300 and delivered 89.** The error was clustering on the wrong axis: I read
"`fn-abs` 48" off a table of ALL CLR leaks and assumed those were the `InvalidCastException`
cases, when they were mostly the `OverflowException` range checks. `InvalidCastException` was
always dominated by duration operators, which none of these fixes touch — it moved 144 → 140.

**Still open, now measured rather than estimated:**

| cluster | count | note |
|---|---|---|
| duration arithmetic operators | ~77 | `op-subtract-dayTimeDurations` 29, `op-add-dayTimeDurations` 18, `op-divide-dayTimeDuration` 18 — `InvalidCastException` in the operator path, untouched by any fix here |
| `XmlSchemaValidationException` | 68 | unchanged; schema-awareness, expects XQDY0027/XQDY0084 |
| proper XQuery error, wrong code | ~350 | case-by-case, not a cluster |


### 36. Wrong-code clusters — PARTIALLY FIXED 2026-09-05
Continuation of #35 into the "raises a proper XQuery error, wrong code" tail.
**XQTS 28,976 → 29,035 (92.07% → 92.26%), +59.** XSLT corpus 10,024 → 10,034. Unit gates
1532 / 1480, unchanged.

| fix | expected | we raised | gain |
|---|---|---|---|
| casting to a gregorian type from a non-permitted source | XPTY0004 | FORG0001 | **50** |
| `fn:avg` operand type | FORG0006 | XPTY0004 | **8** |
| static error code from `XQueryFacade` / CLI | analyzer's code | hardcoded XPST0003 | 0 on XQTS |

**Gregorian casts (50).** XQuery §19.1 permits casting to `xs:gYear` and friends only from
`xs:string`, `xs:untypedAtomic`, `xs:date`, `xs:dateTime` or the SAME gregorian type. The `_ =>`
arm of each of the five dispatches instead stringified whatever it got and handed the text to the
lexical parser, so `xs:time("13:20:00-05:00") cast as xs:gYear` reported
`Invalid xs:gYear: '13:20:00-05:00'` — diagnosing a malformed lexical form for a cast that was
never legal. The `_ =>` catch-all accepting what it cannot handle is the same fail-open shape as
`_ => true` in the harness (#28) and `_ => atomized` in `ValidateNumericArg` (#35).

**`fn:avg` (8).** `fn:sum` raises FORG0006 for all four of boolean/string/anyURI/duration;
`fn:avg` agreed only on string and used XPTY0004 for boolean and anyURI — inconsistent with its
own twin three lines away. Asymmetric pair, again.

**The facade fix gained nothing on XQTS, and that is worth recording.** `QueryEngine.Compile`
already propagated the analyzer's code; `XQueryFacade` and the CLI hardcoded `XPST0003` — a third
instance of the same asymmetric pair. But the XQTS runner goes through `QueryEngine`, so the 37
`Compilation failed` cases are NOT a plumbing problem: the analyzer is producing the wrong static
code (XQST0059 where the test requires XQST0036). That is a real analysis defect and remains
**OPEN**. The facade fix still stands on its own — it fixes the CLI and XSLT-side paths.

**Not fixed, deliberately: SEPM0017 (20 cases).** These looked like a code swap —
we raise XPTY0004 where SEPM0017 is expected — but the tests are about the `use-character-map`
serialization parameter, and we reject the whole parameters element before ever reaching that
check. The message ("serialization parameters element must be
&lt;output:serialization-parameters&gt;") describes a condition the test does not have, so
namespace resolution on the params element is the real defect. Swapping the code would make 20
tests pass for the wrong reason — the exact failure mode #28 exists to remove. **OPEN.**

**Prediction record, since #35 noted the last one:** gregorian was estimated at 50 and delivered
50, but only after a wrong first attempt. The first guard rejected numeric sources, because the
one sample I opened (`CastAs187`) was `xs:float(…) cast as xs:gYearMonth`. It measured **zero**.
The actual population is `xs:time(…)` and cross-gregorian sources; one unrepresentative sample
produced a fix that compiled, passed the unit gate, and moved nothing.


### 37. XQTS ran XQuery 1.0-only tests against a 4.0 engine — FIXED 2026-09-05
`SatisfiesDependency` checked `dep.Value?.Contains("XQ") == true`, which accepts EVERY XQuery
version — `XQ10` included. QT3 marks version-pinned tests with
`<dependency type="spec" value="XQ10"/>`, and those encode behaviour that later specs
deliberately CHANGED, so a 4.0 engine is required to fail them:

- `Axes127` says in its own description "the namespace-node() kind test is new in XQuery 3.0"
  and asserts XPST0017 — it requires the engine NOT to support something we do support.
- `K-SeqExprCast-71a` pins the 1.0 rule that casting `xs:untypedAtomic` to `xs:QName` is an
  error, citing bug 16059 — the same bug our own `CastOperator` comment cites as the thing
  XQuery 3.0 relaxed.
- The `function-call-reserved-function-names-*` family declares `local:function()` then calls
  `function()`. Legal in 1.0; `function` became a reserved function name in 3.0.

Measured: **31,470 → 31,414 tests** (56 excluded), failures 943 → 910, errors 1,492 → 1,471,
**92.26% → 92.42%**. Two of the 56 had been passing.

**This change RAISES the score, so it was held to a higher bar than one that lowers it.** The
filter is deliberately conservative: a trailing `+` means "and later" and applies; an exact
`XQ31` counts as applicable because 4.0 is a superset of 3.1 and excluding it would be
self-serving; only exact `XQ10`/`XQ30` are dropped. A dependency naming no XQuery version at all
is left applicable, exactly as before. Three tests were read in full before the rule was written.

Estimated 123 excluded failures, measured 54 — most version-pinned tests were already not being
run for other reasons.

### 38. OPEN — where the remaining XQTS failures actually are
Prioritisation of the 2,435 failures, so effort goes at causes rather than error labels.

| | count | kind |
|---|---|---|
| schema-aware (`prod-SchemaImport`, `ValidateExpr`, `CastExpr.schema`, `CastableExpr`) | **392** | missing FEATURE |
| module resolution (`fn:load-xquery-module`, `import module`) | **200** | one plumbing gap, #30 |
| everything else | 1,843 | bugs |

Within "everything else", by theme rather than by error code:

| theme | count |
|---|---|
| casting (`prod-CastExpr` 182, `CastableExpr` 81, `CastExpr.derived` 35) | **~298** |
| serialization (`fn-serialize` 62, `method-xml` 36) | ~98 |
| formatting (`fn-format-date` 46, `fn-format-number` 34) | ~80 |
| numeric/duration (`fn-abs` 42, `op-subtract-dayTimeDurations` 30) | ~72 |
| direct-constructor namespaces | 44 |

**Schema awareness and module resolution together are 592 cases — 24% of all failures — and both
are decisions, not bug hunts.** Neither is reachable by the error-code work that has occupied the
last two days.

**Casting remains the largest genuine bug cluster at ~298** even after #35 and #36 took ~120 out
of it.

On the XSLT side the 596 failures cluster in `error` (68), packaging — `accept`/`package`/
`use-package`/`override` (~70) — streaming `si-*` (~36), `accumulator` (22) and `merge` (19).
Those are advanced features; a typical transformation pipeline touches almost none of them, so
the conformance percentage is a poor predictor of what a real stylesheet will hit.


### 39. Constructor functions leaked CLR exceptions the cast path already translated — FIXED 2026-09-05
The casting cluster from #38, attacked with a full-cluster diagnosis first rather than one fix at
a time. **XQTS 29,033 → 29,205 (92.42% → 92.97%), +172** — the largest single change in this
sequence. XSLT corpus unchanged at 10,034/10,630. Unit 1532.

The 399 casting failures split three ways, and only one third was actionable:

| | count | |
|---|---|---|
| crashed where a value was required | 140 | ~105 of them schema types — blocked on the same feature as #38 |
| wrong output | 23 | |
| **wrong error code** | **236** | ← this one |

The 236 were still raw CLR messages *after* #35 wrapped `TypeCastHelper.CastValue`, which looked
impossible until the failing tests were read: they are **constructor function calls**, not cast
expressions. `xs:nonNegativeInteger("--0")` goes through `TypeConstructorFunctions`, which calls
`long.Parse` / `Convert.ToInt32` directly. The spec defines a constructor function as equivalent
to a cast, so the two must report the same codes — and only one half had been wrapped. Asymmetric
pair, now between two halves of a single spec rule.

Fixed once in the base class: `TypeConstructorFunction.InvokeAsync` is now `sealed` and wraps a
new `protected abstract InvokeCoreAsync`, which the 49 subclasses override instead. A wrapper at
the call sites was not possible — every one is an `async IAsyncEnumerable` iterator, where C#
forbids `yield return` inside a `try`/`catch`. That constraint is why #35's wrapper went into
`CastValue` too.

Mapping was measured against the corpus, restricted to cast/constructor context, not assumed:
`FormatException` → FORG0001 (**57 of 57**, unanimous), `OverflowException` → FOCA0002 (78
against FORG0001's 29 — a majority, not a certainty), `InvalidCastException` → XPTY0004. The 29
remain wrong, now with a proper code, until that split is understood.

Measured effect on the raw-CLR messages in cast context:

| message | before | after |
|---|---|---|
| `The input string '…' was not in a correct format.` | 57 | **0** |
| `Arithmetic operation resulted in an overflow.` | 30 | **2** |
| `Value was either too large or too small for …` | 74 | 39 |

`prod-CastExpr` failures fell 182 → 39. What remains in the casting area is
`prod-CastableExpr` (77) and `prod-CastExpr.schema` (72) — both schema-typed, i.e. #38's feature
decision, not this defect.

**The loop change is what produced this.** Diagnosing all 399 before touching anything found the
constructor/cast asymmetry immediately; the previous fix-then-measure rhythm had walked past it
twice, because each individual message looked like something `CastValue` should already have
handled.

---

### 40. One helper, one unswept twin — three instances in one branch — FIXED 2026-09-09/10

Three defects fixed on `fix/err-location-runtime-exception` (XSLT) and
`fix/aggregate-provider-atomization` (XQuery) turned out to be the same structural shape:
**a helper grew a parameter, some call sites were updated to pass it, and the ones that were
not kept the old behaviour silently** — because the parameter had a default.

| # | helper | passed it | did not | symptom |
|---|---|---|---|---|
| a | `FixDotForSurrogatePairs(pattern, singleLineMode)` | 5 XQuery call sites | both XSLT ones | `flags="s"` ignored by `xsl:analyze-string` |
| b | `AtomizeTyped(value)` / `Atomize(value)` | `fn:data`, `fn:number`, type ctors, string fns | `fn:sum` `fn:avg` `fn:max` `fn:min` | store-backed elements atomize to `''` |
| c | scope push around a body | every other construct | `xsl:try` | try-body variables clobbered outer ones |

(b) is the third sweep of one defect: #160 fixed `fn:number`/`fn:data`/type constructors, #163
the string functions, and the aggregates were missed **both** times.

**Why the default is the mechanism, not an incidental detail.** In each case the wrong behaviour
is what you get by *not* thinking about the parameter. Adding it was source-compatible, so the
compiler never enumerated the call sites, and every missed one silently kept the pre-fix
semantics. A required parameter would have turned all three into build errors.

In (a) the flag was even parsed and mapped correctly — `RegexOptions.Singleline` was set — and
then discarded by a *pattern rewrite* applied afterwards that baked `[^\r\n]` into the regex.
Checking that the flag was honoured at the parse site proved nothing.

**Cheap countermeasure.** When a helper gains a parameter that changes semantics, grep every
call site in the same commit and list them in the message. If the parameter can be required,
make it required; where a public signature forbids that (b — the XSLT engine consumes
`AtomizeTyped` cross-repo as a package), document the trap **at the definition of the unsafe
overload**, not only at the fixed call sites.

**A fourth instance, and the one that cost most: `fn:sum` returned 0.** `xs:integer` is
unbounded, so casting TEXT to it yields `BigInteger` even for the value 10. `SumHelper` matched
`int or long`, `double`, `float`, `decimal`, untypedAtomic and the durations — not `BigInteger`.
Unmatched items fell through the whole chain, still counted, and contributed nothing, so
`sum((xs:integer("10"), xs:integer("30")))` returned **0** with no error. `fn:avg` and
`fn:min`/`fn:max` already carried the case. Any query summing integer-typed element text was
affected — no store, no `collection()`, no LINQ needed.

**Two opposite reasoning errors found it, both worth naming.** They are the same mistake in
mirror image, and each cost real time:

> *A reduction that does not reproduce did not clear the code — it dropped a variable.*
> The first minimal repro used a `decimal` selector because that is the natural type for a
> price. It passed on both versions, and absence was reported. The failing test used `int`,
> which is the only thing that mattered.

> *A code path that cannot produce the value does not clear the hypothesis — another path can.*
> BigInteger was the first hypothesis. Two paths that produce `xs:integer` from text were read,
> both correctly returning `long` for in-range input, and the hypothesis was abandoned. A third
> path produces it. An hour went into a single-enumeration theory that measurement then killed.

Both resolve the same way: **instrument and look, rather than reason about what must be true.**
One temporary `else { throw ... item.GetType().FullName }` settled in two minutes what reading
could only have settled by exhaustive enumeration:

```
Runtime error [PROBE]: UNMATCHED item type: System.Numerics.BigInteger value=10
```

A negative conclusion drawn from reading requires exhaustiveness that reading almost never
provides. Treat "I have read the paths that produce this" as "I have read *some* of them".

**Watch the ternary when narrowing a widened numeric type.** The fix narrows the exact total
back to `long` when it fits. `cond ? (long)total : total` silently converts it straight back —
the conditional operator unifies both arms to one type and `BigInteger` defines an implicit
conversion from `long`. Cast BOTH arms to `object`. The CLI printed `40` either way; only the
unit suite, which asserts runtime type rather than numeric value, caught it.

**Corollary about repro cost.** (b) was reported as needing an LMDB store, and that was believed
for a whole exchange. It was wrong: a fake `INodeProvider` harness from #160 already existed in
the XQuery test project and constructs the failing condition with no store at all. Before
accepting "this can only be reproduced in the consumer", grep the test project for an existing
fake of whatever the consumer supplies.

---

### 41. OPEN — eight catalog environment attributes the XSLT runner never reads (2026-09-10)

`xinclude` was found by a single failing case (base-uri-052) and fixed; the engine had supported
XInclude since SP1, so a working capability was being scored as a failure for want of three lines
in the runner. Prompted by the phoenixml engine repo, whose runner DOES read it and scores 052 as
passing, the whole attribute surface was then enumerated rather than waiting for the next case to
surface itself.

Method: every attribute appearing on a catalog **schema** element inside `<environment>` across
all test-sets plus `catalog.xml`, with inline `<content>` bodies stripped (those are document data,
not catalog schema), diffed against what `XsltTestRunner` reads.

| element | attribute | uses | read? | assessment |
|---|---|---|---|---|
| `source` | `streaming` | 176 | **no** | worth a real check — see below |
| `source` | `context` | 165 | **no** | 166 of 166 are the single value `static-expression-context`; a marker, likely benign |
| `schema` | `xsd-version` | 121 | **no** | schema-aware; #38's feature decision, not a defect |
| `source` | `validation` | 115 | **no** | schema-aware; same |
| `source` | `defines-stylesheet` | 15 | **no** | unassessed |
| `resource` | `media-type` | 13 | **no** | unassessed |
| `source` | `xml-version` | 1 | **no** | unassessed |
| `namespace` | `prefix` | 1 | **no** | unassessed |

Everything else the corpus declares IS read: `role`, `file`, `uri`, `select`, `xinclude`,
`ref`, `name`, `value`, `static`, `as`, `encoding`, `media-type`'s siblings, `stylesheet/@file`,
`stylesheet/@role`, `collection/@uri`, `schema/@file`, `schema/@role`, `schema/@uri`.

**`source/@streaming` — MEASURED 2026-09-10, and it costs nothing. Now wired anyway.**

Honouring it is more than reading it: `TransformAsync(string)` is the materialising path and NEVER
streams, so the attribute only means anything if the runner opens the file and hands over a
`TextReader` — the overload that actually selects the streaming engine, the same choice the CLI
makes via `HasStreamableMode`. A runner that read the attribute and kept calling the string
overload would look wired and change nothing.

Wired, then swept the full corpus:

| | before | after |
|---|---|---|
| every one of the 11 chunks | — | **identical** |
| every one of the 221 test-sets | — | **identical** |

Zero movement. The declaration is redundant against this engine, almost certainly because
streaming is auto-selected from the stylesheet's streamable mode — the corpus is telling the
harness something the engine already worked out.

**The instrumentation is what makes that trustworthy, not the numbers.** An inert wiring and a
correct wiring over an insensitive difference produce identical output. The streaming branch was
therefore made to log every case that took it — 29 in `attr` alone (doe-0801/2/3, mode-1406/8/10,
attr/streamable's 24) — proving those cases genuinely executed on the streaming engine before
"no change" was believed.

Kept despite changing nothing: the harness now does what the corpus declares rather than
coincidentally agreeing by another route, and if auto-selection ever changes, these 176 cases
start exercising the path they name instead of silently not.

This also cleared a question raised by the phoenixml engine repo, whose non-streaming floors
contain 68 streaming-declaring cases (`decl/accumulator` 31, `attr/streamable` 24, and seven
smaller sets). Those floors were sound; no re-baselining needed.

The failure mode of every row above is silence — a declared capability the runner ignores scores a
working engine as failing (or, worse for a conformance claim, an unexercised path as passing). That
is why enumerating beats waiting: `xinclude` is declared by exactly ONE case in 10,630, and a
feature the corpus exercises once is a feature nobody notices is unwired.

---

### 42. Core's StringValue could not say "I don't know" — and the work it defers (2026-09-10)

`XdmElement.StringValue` / `XdmDocument.StringValue` were `_stringValue ?? string.Empty`, so a
node whose value had never been computed reported exactly what a genuinely empty node reports.
Found from the database side: paths that WALK children (`fn:string`, explicit casts) saw the text
while paths that READ the cached value (implicit atomization) saw `""`, so the same predicate
returned the right document on its own and `0` inside `count()`, with no error raised. This is
the root cause under #35's `fn:sum/avg/max/min` fix — that treated the symptom without knowing it.

Fixed in `phoenixmldb-core` on `fix/string-value-resolver`:

| commit | what |
|---|---|
| `f4d4b82` | `XdmNode.StringValueResolver` — a delegate invoked on first read and cached. `NodeReader` takes one too, and that is the part that makes it usable: the storage layer never constructs `XdmElement`/`XdmDocument` itself, so an init-only property alone would have been a hook nobody outside the assembly could reach. |
| `898fc14` | `XdmNode.StrictStringValue` — opt-in; raises rather than returning `""` when a node has neither a computed value nor a resolver. Off by default so no consumer changes on upgrade. Core's own suites run with it ON. |

**Measured before enabling strict, not assumed:** with it on globally the only failures across all
516 Xdm tests were the four asserting the empty-string fallback; no production path broke. Those
four now pin the flag off explicitly. A strict mode nobody enables catches nothing, so the
suites that run it are the point.

#### Deferred to the Core pin bump — do not lose these

Both need the released Core and are therefore NOT on any branch today:

1. **`phoenixmldb-xquery` `ElementConstructorOperator` copies the string value by FIELD** at four
   sites (`735`, `958`, `997`, `1072`: `newElem._stringValue = elem._stringValue`). A field read
   bypasses the resolver, so a copy taken before anything read the property inherits `null` AND
   no resolver of its own, and reports `""` for ever. The fix is a
   `CarriedStringValue(source)` helper reading the PROPERTY when the source has a resolver and
   the field otherwise. Written, then reverted — it does not compile against the published Core
   1.6.7. Site `1247` already has a working fallback and was left alone.
   **The resolver must NOT be copied onto the new node**: it resolves by identity in the store it
   closed over, and a copy has a different identity, so it must be resolved through the SOURCE.
2. **Turn `StrictStringValue` on in the XQuery and XSLT test suites** at the same bump, for the
   same reason Core's are on.

#### Open, and not reproduced here

The engine repo reports a direct element constructor whose content comes from a node store
returning an `xs:string` of its own markup, so a path step on it fails with "axis step used when
the context item is not a node". They see it on an LMDB container AND on XQuery's own
`XdmDocumentStore`, at 1.6.9 and on the aggregate branch. **Four tests driving `XdmDocumentStore`
directly — including the parenthesised form — all PASS**
(`phoenixmldb-xquery` `2907c1a`, kept as coverage). Recorded as a measured negative, not as
absence: per #40, a reduction that does not reproduce is evidence the reduction dropped a
variable. Awaiting the distinguishing detail.

Also from the engine side and worth a check nobody has done: whether Core or XQuery anywhere
computes an index key from a stored node's `StringValue`. Their index-removal defect was exactly
that — `RemoveText("")` removed nothing, so full-text search kept finding words a document no
longer contained.

---

### 43. The conformance harness measured Debug, and understated the engine (2026-09-10)

`conformance.sh` defaulted to `CONFORMANCE_CONFIG=Debug`. Debug is not what ships, so every
conformance figure taken from it was a claim about an artifact nobody receives — and it was
**too low**, entirely because of the harness's 10s per-case timeout.

Measured, same commit, same machine, full 11-chunk sweep both ways:

| | cases | rate | timeouts | wall clock |
|---|---|---|---|---|
| Debug | 10044/10630 | 94.49% | **43** | ~38 min |
| Release | **10082/10630** | **94.84%** | **5** | ~25 min |

**21 sets better under Release, ZERO sets worse.** 38 cases, 0.35 points.

`misc/bug-3701` is the clean illustration, timed directly at load ~1.3:

    Debug    10.3s / 10.8s / 10.9s      against a 10s cap
    Release   5.7s /  5.5s /  6.1s      (and that at load ~10)

Not flaky — over the line in one configuration and comfortably under in the other. It also
explains why the phoenixml engine repo's floors, which are measured against published *packages*
(Release), have always shown `bug-3701` passing while it flickered here.

**Default is now Release.** The baseline was rebuilt from the Release sweep.

**Why this belongs in the same register as the fail-open entries.** #28 was a check that passed
when it could not verify anything; this is its mirror — a harness that FAILS cases the product
passes. Both report something other than the truth, and the direction is not the point. A number
that flatters is a lie you get called on; a number that undersells is one nobody checks, which is
worse, because it survives.

It also retires a mystery: the `call-template-1002/1003` stack-depth episode earlier the same day
cost two sessions a long detour, and Debug frames were a live hypothesis throughout
(Debug reached recursion depth 898 vs Release 976 on the same commit). Anything timing-,
stack- or performance-sensitive measured in Debug is measuring the wrong build.

**Cheap countermeasure, general:** before publishing a number from a harness, confirm the harness
runs the configuration that ships. Ours did not, and nobody asked for two months.

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

**Three patterns worth remembering.**

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

*A step that did not run looks exactly like a step that ran and found nothing* — four instances
in one day, in unrelated tooling built by two people who were not coordinating:

| the step | what it produced | what it looked like |
|---|---|---|
| expected-error harness had no code to compare | matched on any exception | a pass |
| chunk-total gate, set-level loss inside a rising chunk | a bigger number | a gain |
| `-c Release` that never reached the projects | Debug results | "configuration makes no difference" |
| a probe that failed to compile (CA1849) | no log file | "the branch never ran" |

The countermeasures were the same move every time — **hash the artifact, log the branch, refuse to
emit a number** — and none of them check the answer. They check that there *was* an answer.

That is why they are cheap and why they generalise. Checking the answer needs an oracle: you must
already know the right result, which for a conformance run is the thing you are trying to find
out. Checking that the step executed needs nothing but the pipeline's own artifacts — a hash, a
log line, a case count.

The related distinction, worth keeping separate: `Build FAILED` printing above a bad probe and the
per-set gate both surface the same fact, but only one FAILS the run. Correctness that depends on a
reader noticing is not a control, because the reader who most needs to notice is the one who
already believes the run succeeded.

*One of a pair had a fix its twin lacked* — the dominant shape in this codebase, and #39
records three more in a single branch. It appears wherever the same job is done in two places:
the two `fn:transform` implementations, the local and global variable paths, the XSLT and
XQuery copies of a regex or atomization helper. Cheap countermeasure: when you fix one, grep for
the other **in the same commit**. A helper that took a new parameter is the highest-yield thing
to grep for, because the default hides every site you missed.
