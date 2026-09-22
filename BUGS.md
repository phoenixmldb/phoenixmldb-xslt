# Known bugs and open work

> Lives in `phoenixmldb-xslt` because the workspace root is not a git repository and an
> unversioned register is one hardware failure from gone. It spans repos deliberately —
> the engines are split but the defects are not, and several entries below are precisely
> about a fix in one repo being blocked on a package from another.

Findings from the 2026-08-22/24 conformance push. Entries stay until fixed AND measured.
Numbers are measured, not estimated; where a count is a guess it says so.

**Convention for an OPEN entry, learned the hard way (see #79 and #85).** An entry recorded
without a fix must carry **the command that reproduces it and the literal output**, so the next
person re-runs rather than re-reasons. Record the **symptom you observed**, not the **site you
suspect**: a symptom stays true, while a named site decays silently the moment someone changes
that code — including you, fixing something else. Anything asserted about a *location* should be
re-verified before it is acted on, and the entry should say so. **Date every number** — a
two-day-old cost estimate on a fast-moving tree is a different kind of claim from a fresh one,
and a number without a date invites being quoted as current. And **record what did NOT
reproduce it** — the next person's first instinct is the instinct that already failed, and an
hour spent ruling out the obvious minimal cases should be spent once (#84).

**Before reporting a finding as new, grep this file for the construct (#89).** This register is
the only memory that survives a context compaction, and it has already had the same defect
discovered twice — by the same person, two days apart, reported the second time as new. A finding
you derived yourself does not feel like something to look up, which is exactly why the check has
to be a habit rather than an instinct.

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

### 44. OPEN — the harness ledger, and why these keep happening (2026-09-10)

Asked whether anyone tracks *why* the test harnesses fail on a near-daily basis, the honest
answer was **no**. This file records WHAT — 43 entries, at least 11 of them harness defects —
but nothing recorded the rate, the causes, or that it was a pattern at all. This entry starts
that ledger.

#### The structural fact

| | lines | tests covering it |
|---|---|---|
| conformance runners (`tests/PhoenixmlDb.Conformance.Tests`) | 8,115 | **0** |
| release/gate scripts (`scripts/*.sh`) | 528 | **0** |
| PhoenixmlDb.XQuery engine | — | 1,554 |
| PhoenixmlDb.Xslt engine | — | 1,483 |
| PhoenixmlDb.Core | — | 1,018 |

~8,600 lines decide whether the engines are correct, and have no tests, no review path and no
owner. They are written mid-investigation by whoever is chasing an engine defect, and they fail
the way untested code fails.

**And their failure mode is silence.** An engine bug shows up as a red test. A harness bug shows
up as nothing — a wrong score reads as green. That asymmetry is why these survive for months
while engine defects are caught in hours.

#### Incidents on 2026-09-10 alone

| # | incident | origin |
|---|---|---|
| 1 | expected-error match read element text, not the attribute | pre-existing, fixed |
| 2 | `conformance.sh` defaulted to Debug — understated by 38 cases | pre-existing, fixed |
| 3 | QT3 ran as one opaque 31,414-case test | pre-existing, fixed |
| 4 | 419 of 428 QT3 sets unreachable (hard-coded list) | pre-existing, fixed |
| 5 | 8 catalog environment attributes never read | pre-existing, open (#41) |
| 6 | chunk-total gate could not express a per-set loss | **mine** — found by a consumer |
| 7 | baseline rule "maximum observed" fires on every ordinary run | **mine** |
| 8 | re-baseline taken while probing silently lowered 3 sets | **mine** |
| 9 | `MemberData` placeholder: 1 skipped test instead of 428 | **mine** |
| 10 | publish gate referenced a job that does not exist, in 3 repos | **mine** — broke CI |
| 11 | scaling test wrong three separate ways in one day | mixed |
| 12 | **QT3 per-set results depend on execution order** | **mine** — see below |

Five of twelve were introduced while fixing the others. That is the honest shape of it.

#### Incident 12, in full, because it is the newest and least obvious

Making the QT3 theory per-set (#43's companion fix) traded one defect for another. All 428 sets
share a single `IClassFixture`, and `XqtsTestRunner` accumulates real state across them:

    _engine, _documents, _documentCache, _schemas, _loadedSchemas,
    _registeredUris, _resourceMappings, _globalEnvironments

The monolith ran in catalog order, so the contamination was *stable* — wrong, but repeatable, and
therefore invisible. Per-set runs in xunit's order, which follows the assembly path, so two
checkouts of the same commit disagree. Measured by the parsers2 session: six sets differed between
two worktrees, and **every one was identical when re-run in isolation** (`method-html` 49/64 on
both). `qischema001` fails or passes depending on which schemas loaded before it.

Consequences: a figure from one machine is not comparable to another's, and a per-set gate can
fire or stay silent from order alone.

Fix in progress: a fresh runner per set. A `TestCaseOrderer` pinned to catalog order would restore
reproducibility while leaving each result dependent on its predecessors — reproducibly wrong.

**The XSLT runner does NOT have this.** `XsltTestRunner` holds only `_testDataPath` and `_config`,
both immutable, and constructs a transformer per test. Same repository, same idea, two runners,
only one stateful — which is exactly what an unreviewed, untested component looks like.

#### What would actually change the rate

1. **Tests for the runners**, starting with scoring. Feed a known-passing and a known-failing case
   and assert the verdict. Incidents 1 and the `passed > 0` gates both die instantly to that.
2. **An owner** for the conformance harness, treated as product code rather than scaffolding.
3. **This ledger**, kept current, so the rate is visible instead of anecdotal.

Items 1 and 2 are not done. This entry is item 3.

---

### 45. Group wrong-error-code failures by the ACTUAL message, not the (expected, actual) pair (2026-09-11)

The triage in phoenixmldb-xslt#13 found 217 of 548 failing W3C cases marked `[wrong-error-code]`
— the engine detects the condition and raises, but reports a different code. I recommended
grouping by (expected, actual) pair and fixing pairs rather than cases, on the strength of #36
where that worked.

**It does not work here.** The 217 failures are **133 distinct pairs** — a long tail, roughly 1.6
cases per group, so grouping by pair barely beats fixing them one at a time.

Grouping by the **ACTUAL** message alone does work, because that is what identifies a *throw
site*. One wrong constant serves many expected codes, so the pairs scatter while the sites
cluster. Batch 1 was **seven throw sites for 43 cases** — and two of the seven were the
asymmetric-pair shape from #40, one twin carrying a fix the other lacked.

The general lesson, which is not about error codes: **group by the thing you will EDIT, not by
the thing the test reports.** A test failure names a symptom pair; the fix has one location. When
those are not in correspondence, grouping by the symptom produces a long tail and grouping by the
site produces clusters. I had the right instinct from #36 and applied it to the wrong axis.

Measured: 10,083 → 10,126 of 10,630 (+43), same-checkout A/B sweep, 43 newly passing, 0 newly
failing, no set lower. `misc/error` 438 → 466. (phoenixmldb-xslt#15.)

---

### 46. OPEN — tests that measure the machine, not the engine (2026-09-11)

A third instance of the same shape, after the parser scaling test (#43 and its three corrections)
and the `bug-3701` timeout that set a baseline from a lucky run.

#### `sf-not-107` — a flicker that triggers on LOAD, not on ordering (2026-09-13)

Found by parsers2 when an A/B reported it as the sole loss. **It is not a real loss.** It streams
100,000 transactions and asserts `count(/out/t) = 100000`; **under a two-arm sweep it times
out.** Two clean `strm1` re-runs on the branch, and it runs correctly under the CLI.

It belongs on the known-flicker list with `call-template-1002/1003`, but **the trigger is a
different species and the distinction matters:**

| flicker | triggered by |
|---|---|
| `call-template-1002/1003` | test **ordering** |
| `sf-not-107` | **machine load** — it fails because the sweep runs two arms on a shared box |

The remedy is the same (re-run the chunk). The hazard is not:

> **It is more likely to appear the busier the machine is — which is exactly when someone is
> least inclined to re-check a single-case loss.**

A flicker that correlates with the conditions under which you are least likely to verify it is
strictly worse than a random one. Worth naming rather than filing alongside the ordering
flickers.

`tests/PhoenixmlDb.Xslt.Tests` `StreamingCancellationTests` cancels at 3 s and then asserts the
transform is still running. On a fast machine it finishes in ~2 s, so 2 of the 4 tests fail
**deterministically** — the assertion encodes an assumption about how slow the host is.

That is the same defect as asserting a wall-clock bound for linearity: the property under test is
"cancellation is observed", and elapsed time is a proxy that stops holding when the hardware
changes. Now that `publish` is gated on tests, such an assertion is a release blocker on any box
faster than the one it was written on.

**The shape to watch for:** a test that passes on the author's machine and encodes its speed. It
is not flakiness — flaky tests fail intermittently. These fail *reliably*, on the wrong hardware,
which reads as a real defect and costs a diagnosis every time.

### Update 2026-09-18 — the rewrite moved the failure to the other end of the range

`StreamingCancellationTests` has since been rewritten, and the description above no longer matches
the code. There is no "still running after 3 s" assertion left; all four tests now assert **upper**
bounds:

```csharp
cts.CancelAfter(TimeSpan.FromMilliseconds(500));
sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));   // over 400,000 iterations
```

That fixes the fast-box failure this entry was filed for — and introduces the mirror image.
`StreamedTransform_TokenCancelledMidRun_ThrowsBeforeCompletion` now fails on a box that is too
**slow**, or merely busy. Observed twice on `palukerjr` (8 cores, load average 10 from concurrent
builds), both times inside the full suite, both times passing **4/4** when re-run alone on the same
checkout. It passes on `mechapaluker` and on CI, so the same commit yields a different unit result
on two machines.

The proxy was never the direction. It is that **elapsed wall-clock stands in for "cancellation was
observed"**, and any bound on a proxy has two ends: assert a floor and fast hardware fails, assert a
ceiling and slow or loaded hardware fails. Flipping the inequality moves which machines are wrong,
not whether the assertion measures the property.

What would actually test the property: assert that the transform stopped **before consuming all
400,000 items** — a count the streamed path can report — rather than before a clock reading. That
holds on any hardware, because it is the thing cancellation is supposed to do.

**Cost while it stood:** every full unit run on the slower host reported a failure. A suite that is
reliably red for a known non-reason is a suite whose red is no longer information — which is how a
genuine regression gets waved through as "that one always fails".

**RESOLVED in #161.** The tests now count items processed — via a `MessageListener` on an
`xsl:message` in the same `for-each` body whose cancellation is under test — and cancellation is
triggered by that count rather than by a timer. No wall-clock assertion remains in the file, so host
speed cannot affect the result structurally rather than merely empirically.

Verified by constructing the failure rather than waiting for it, because the first control run
**passed**: the defect is a property of the host *under load*, not of the host. Ten CPU spinners,
same box, same suite:

| arm | load | result |
|---|---|---|
| `main` idle | 2.69 | PASS 1934/1935 |
| `main` loaded | 21.20 | **FAIL 2** — both `StreamingCancellationTests` |
| #161 loaded | 19.77–22.74 | PASS 1934/1935 |

Two notes worth keeping. At higher load the loaded control failed **two** tests rather than the one
previously observed — a wall-clock proxy loses more assertions as the margin shrinks, so the count
of affected tests is itself load-dependent and not a fixed property. And the fix's own first draft
**passed while measuring nothing**: it read the item count from a return value that is never
assigned, because the method throws on cancellation, so the count was 0 and `0 < quarter` held. The
lower-bound assertion — at least `CancelAfterItems` processed — is the only thing that caught it,
which is why it is marked non-redundant in the file. This entry's shape, one level down, inside its
own fix.

Owned by the parsers2 session, to be fixed alongside the QT3 per-set baseline and the
`insn/call-template` re-baseline.

---

### 47. OPEN — static shadow-attribute evaluation silently DROPS what it cannot compute (2026-09-11)

`_xsl:`-prefixed shadow attributes are evaluated at compile time. The parser's implementation is a
**string-substitution subset**, not an XPath evaluator: given an expression it cannot handle, it
does not fail — it drops it, and compilation continues against an attribute value that is missing
or partial.

That is a fail-open at the point where a stylesheet states its own requirements. `package-version`
is the case that surfaced it (`package-version-909/910/010/011`): a shadow attribute computing a
version or range is silently discarded, so version validation then runs against nothing and
passes. The defect is not in the validation — it is that the validator was handed garbage and
could not tell.

Found while fixing #13's package-version cases (phoenixmldb-xslt#16). That PR **annotates
unevaluated shadow attributes** so nothing downstream validates the result, which converts the
silent drop into a visible one. The underlying need is real static XPath evaluation, which is not
done.

**Why this one is worth its own entry rather than a line in #13.** The four failing cases are the
symptom; the shape is the defect. A subset evaluator that returns a wrong answer instead of
refusing is indistinguishable from a working one until something downstream disagrees — the same
structure as the expected-error harness (#28), the empty-string atomization (#42) and the
element-constructor fallback that returned a string where a node was required. It will not be
confined to `package-version`: any shadow attribute whose expression exceeds the subset is
affected, and nothing currently says which those are.

Reported by the parsers2 session.

**Update 2026-09-15 — blast radius measured, and a second failure mode.** #47 predicted this
"will not be confined to `package-version`" and that "nothing currently says which those are".
It is now enumerated for one set: **every one of the 166 cases in `tests/fn/system-property-gen`
fails from this single cause**, which is why that catalog set scores 0/166 the moment it is added
to the harness. One cause, 166 manifestations — not 166 defects.

**But fixing #47 will not clear them, and this entry should not be read as though it would.**
Checked against the corpus rather than inferred: `system-property-100-data.xsl` declares its static
variables as `replace(?, '''', '''''')` (argument placeholder), several `function($x) { ... }`
inline function items including nested ones, `$ns-scope = 'switch-xsl-namespace'` (depending on
another static variable), and sequence literals; `system-property-100-dc-normal.xsl` then chains
`_select="{$v:static-call}"` through six further shadow-defined variables. Evaluating those at
compile time needs inline function items, partial application, simple map and inter-variable
chaining — i.e. the "real static XPath evaluation, which is not done" that this entry already names
as the underlying need. That is feature-sized. The 166 are a standing measurement of an absent
feature, not a cluster waiting on a fix. (Established by parsers2, who took the cluster and then
retracted it after reading what the cases actually depend on.)

**Update 2026-09-16 — four defects fixed; the set still does not pass, for a different reason.**
The root shape turned out to be an asymmetric pair (#40), not merely a weak evaluator. The engine
has **two** compile-time XPath evaluators:

| path | evaluator | operates on |
|---|---|---|
| `use-when` | `EvaluateStaticExpression` | the parsed AST — binaries, sequences, ranges, `if`, quantified, `instance of` |
| shadow attributes | `EvaluateShadowExpression` | raw strings — `\|\|`, `$var`, literals, `QName()` |

Same job — evaluate XPath at compile time with no context item — and only one got the real one.
Shadow attributes now go through the `use-when` pipeline
(`ParseXPathWithContext` → `ResolveExpressionNamespaces` → `EvaluateStaticExpression`), with the
string matcher kept only as a fallback. Four defects fell out, each with a regression test in
`ShadowAttributeStaticValueTests` (5 of its 7 tests fail on unfixed source):

1. **`CollectStaticDeclarations` stored a static variable's raw select TEXT as its value.** That
   text is what got pasted into shadow attributes. Now evaluated.
2. **The "is it a literal?" test was `StartsWith('\'') && EndsWith('\'')`** — also true of
   `'a' || 'b'`, which it therefore stripped to `a' || 'b`. Only something that parses the
   expression can tell those apart, so the real evaluator now runs first.
3. **`||` parses to `StringConcatExpression`, a separate AST node** from
   `BinaryExpression{Operator=Concat}`. `EvaluateStaticExpression`'s switch had only the latter, so
   every static concatenation fell to `default:` and was reported unevaluable. Two representations
   of one operator, one consumer handling one of them — the #53 shape again.
4. **`ResolveShadowValue`'s `{$name}` arm appended nothing and reported success** for an unknown
   variable. Now marks the result incomplete.

**What this does NOT do is make `fn/system-property-gen` pass, and the reason is worth recording.**
Those 166 rest on `doc('…')/data:test`, predicated paths, `!`, inline function items and dynamic
invocation of them — evaluated at compile time. The engine owns exactly one thing that could do
that, `EvaluateAsync` on the execution context, and it is async and needs a source document, while
shadow resolution is a **sync pre-pass over the XML tree before anything is compiled**. Closing
that gap is a feature, not a fix.

What did change for that set: all 166 failed with `XPST0003: mismatched input '<EOF>'` — the XPath
parser describing the empty string the shadow resolver had handed it, naming neither the attribute
nor the expression. An unevaluable shadow attribute on `select`/`test`/`match`/`group-by`/
`group-adjacent` now raises at resolution time naming both, and says plainly that it is a processor
limitation rather than an error in the stylesheet. 166 identical unreadable errors became 166
errors that state what is missing. **Improve the error first** — the rule that keeps earning out.

**The guard closes empty-and-unevaluated, not the whole class.** `!complete && IsNullOrWhiteSpace(value)`
does not catch a *partially* evaluated AVT: `_select="{$a}{$b}"` with `$a` evaluable and `$b` not
produces a NON-empty value, skips the throw, and compiles against a partially-substituted string.
That is the annotation path behaving as #16 designed it, so it is correct rather than broken — but
"fails open" still describes that subset, and it is the shape most likely to surface later as a
confusing downstream error, because the value looks plausible and is wrong. Stated precisely:
empty-and-unevaluated now raises at resolution time naming the attribute and sub-expression;
partially-evaluated values remain annotated and compile, as before. (Named by parsers2 while
reviewing the guard's scope.)

A scoping concern on `group-by`/`group-adjacent` was raised and withdrawn: `!complete` is the
load-bearing half of the guard, so a grouping key that legitimately evaluates to empty resolves
with `complete == true` and never reaches the throw. An empty `group-by=""` is not a valid XPath
expression in any case.

Unit gate after all four: 1,853 passed, 0 failed, 1 skipped.

The subset evaluator has a **second** failure mode beyond the silent drop. Minimal repro on the
published `xslt` 2.0.0 tool:

```xml
<xsl:param    name="prefix" static="yes" select="''"/>
<xsl:variable name="fname"  static="yes" select="$prefix || 'string-length'" as="xs:string"/>
<xsl:template name="main">
  <xsl:variable name="f" _select="{$fname}#1"/>
  <out><xsl:value-of select="$f('hello')"/></out>
</xsl:template>
```

```
XPath parse error: XPST0003: mismatched input '#' expecting <EOF>
  ↳ parsing in variable/@select ... : $prefix || 'string-length'#1
```

It substituted the static variable's **unevaluated select text** where its **value** belonged, so
the shadow attribute became `$prefix || 'string-length'#1` instead of `string-length#1`. Not a
drop — a paste of source text that then fails to parse. In `system-property-gen` the same paste
yields an empty expression, hence 166 identical `XPST0003: mismatched input '<EOF>'`.

The boundary is exact, and explains why this hid:

| static variable's `select` | read as | result |
|---|---|---|
| `'string-length'` (literal) | shadow attribute | passes — `<out>5</out>` |
| `concat($prefix,'string-length')` | AVT value | passes — `sees="string-length"` |
| `concat($prefix,'string-length')` | shadow attribute | **fails** — pastes source text |

The variable itself evaluates correctly everywhere *except* a shadow attribute; inside one, the
substitution is textual. A literal `select` is textually identical to its own value, so the common
case is indistinguishable from correct. That is the same "wrong answer rather than a refusal"
structure #47 already names — confirmed here with the case that separates the two.

---

### 48. Every `xs:integer` from text was a BigInteger — one ternary, four defects (2026-09-11)

`IntegerConstructorFunction.ParseIntegerText`:

```csharp
long.TryParse(t, ..., out var l) ? l : BigInteger.Parse(t, ...)
```

The conditional operator unifies its arms to one type, and `long` converts implicitly to
`BigInteger`, so the `long` branch is widened back. **Every `xs:integer` cast from text has been a
`BigInteger` since `2ea9564` (1.6.12)** — including `xs:integer('10')`.

That single line is upstream of four defects found separately over two weeks:

| symptom | where it was "fixed" |
|---|---|
| `sum((xs:integer("10"), xs:integer("30")))` returned **0** | #40 — added a BigInteger branch to `SumHelper` |
| map keys the comparer called equal hashed apart | xquery#7 — added BigInteger to the hash |
| `(10,20,30)[xs:integer('2')]` returned **all three** | positional predicates had three hand-written checks, none admitting BigInteger, so it fell to EBV |
| `function-0701` | the regression that finally led here |

Three of those were fixed **at the point of use**. Each added BigInteger to one more consumer,
and each left the producer alone. Nobody asked why a small integer was a BigInteger in the first
place.

**The lesson is not "watch the ternary".** That warning is already in #40 — written about a
*different* ternary, the `SumHelper` narrowing, nine days after this one was introduced and while
this one sat upstream unfound. A note naming a hazard does not find existing instances of it.

The lesson is: **when a consumer needs a special case for a value it should never have received,
ask where the value came from.** `SumHelper` needing a BigInteger branch for `xs:integer('10')`
was the evidence, and it was read as a gap in `SumHelper`. Fixing the symptom at each point of
use is what let one line produce four defects and survive the fix of three of them.

The test that would have caught it asserts the runtime TYPE of `xs:integer('10')`, not its value
— the same distinction that made the `SumHelper` narrowing bug visible only to a type-checking
assertion (#40). It exists now.

Bisected with CLIs built against their pinned packages: xslt 1.6.10–1.6.12 pass, 1.6.13 fails;
1.6.13 on XQuery 1.6.10 passes; a DLL swap isolates it to XQuery `2ea9564`. Measured, 0 newly
failing: XSLT 10,137 → 10,139, QT3 29,509 → 29,524 (+15 across `fn:abs`, `avg`,
`distinct-values`, `min`, `number`, `round-half-to-even` on integer arguments). Positive control:
9 of 14 new tests fail on the old code. (phoenixmldb-xquery#12, found by parsers2.)

---

### 49. Timing tests, fourth instance — and the fix I recommended is not sufficient (2026-09-11)

A fourth test measuring the machine rather than the engine, and this one matters more than the
others because **it was written using the fix I proposed for the first three.**

| # | test | what it asserted | how it failed |
|---|---|---|---|
| 1 | parser scaling | 50K children parse in under 2s | failed at load 19, code correct |
| 2 | parser scaling, 2nd form | 2x ratio under 3.0 | failed CI at 3.71 on 69ms/255ms |
| 3 | parser scaling, 3rd form | 4x ratio under 8.0 | failed CI at 18.4 — GC on a 2-core runner |
| 4 | `StreamingCancellationTests` | still running after 3s | finishes in ~2s on a fast box |
| 5 | xquery `#10` scaling | 4x ratio | **13.9x on net10.0, passed net8.0 in the SAME run** |

Instance 5 is the informative one. It used a **ratio**, which is what I recommended after instance
1 — a ratio cancels ambient load because both measurements carry it. That reasoning is correct and
still insufficient:

- **It only holds if both measurements carry the SAME load.** Parallel xunit classes on a 2-core
  runner do not give that. One measurement got a free core, the other did not, and the ratio
  amplified the difference rather than cancelling it.
- **At small absolute sizes, fixed costs and GC dominate.** 69ms against 255ms is not measuring
  the algorithm. Instance 3 failed at 18.4 on measurements where the small case was 29ms.

Same code measured 196/265/502/910 ms at 4k/16k/64k/256k locally — linear, obviously.

**What actually works, from five attempts:**

1. **A large span.** 4x separates linear (4x) from quadratic (16x) far better than 2x, and 16x
   better again. Instance 5's fix widened to 16x input with a 64x bound.
2. **Absolute sizes big enough that fixed cost is noise.** If the small case is tens of
   milliseconds, stop.
3. **Isolation from parallel tests.** A non-parallel collection. Without this the other two do
   not save you, which is the whole lesson of instance 5.
4. **Or do not assert timing in CI at all.** Instance 3 ended here: assert correctness at scale
   everywhere, compare timings only locally. A benchmark that reports beats a test that fails.

**The transferable point is about advice, not timing.** I gave a fix that addressed the mechanism
I had seen (absolute bounds are load-dependent) and not the mechanism I had not (co-scheduled
tests break the assumption a ratio relies on). It was applied faithfully and failed the same way.
A remedy that names one cause is worth exactly as much as that cause's share of the problem, and
the way to find out is to let someone apply it and watch — which is what happened here.

Held the 1.7.1 release: `xquery` main was red on this, not on a regression. (phoenixmldb-xquery#19,
found and fixed by parsers2.)

---

### 50. Three tables disagree about what the prefix `dbxml` means (2026-09-11)

**Resolved by owner decision, 2026-09-15: namespace consolidation**
(endpointsystems/phoenixml `docs/superpowers/specs/2026-09-15-namespace-consolidation-design.md`).
The decision went the other way from the recommendation below. All six extension functions move to one
namespace, `phx` → `https://schemas.phoenixml.dev/2026/functions` (id 13), which the XQuery library
predeclares. `dbxml` means only `/2026/meta`, and inside `phx:metadata` keys only. `/2026/db` (id 9) is
retired and never reused. There are no aliases: `dbxml:metadata` and `ft:*` stop compiling. The `Ft`/`Xslt`
id-10 collision below goes with `Ft`. Core: phoenixmldb-core#8. XQuery and this repo's URI mapping for
`xmlns:phx`: the phx consolidation PRs, released together.

Found by db-engine, reported via parsers2, confirmed here from source. Not a crash and
not release-blocking — it is a naming decision that needs settling once, by a human,
before more docs and more user queries are written against the disagreement.

Three independent tables assign the prefix `dbxml`, and no two agree:

| table | `dbxml` means | evidence |
|---|---|---|
| Core registry | `https://schemas.phoenixml.dev/2026/meta` (id 11) | `phoenixmldb-core/.../NamespaceRegistry.cs:26` |
| XQuery engine | `https://schemas.phoenixml.dev/2026/db` (id 9) | `FunctionLibrary.cs:647`, `MetadataFunctions.cs:15,85` |
| published docs | `http://phoenixml.endpointsystems.com/dbxml` — retired, resolves to NOTHING | 9 occurrences, 2 pages on phoenixml.dev |

Core gives `/2026/db` the prefix `phx` and hands `dbxml` to `/2026/meta`. The XQuery engine
and every published example use `dbxml` for `/2026/db`. So the same token names two different
namespaces depending on which table you read, and a third value — the pre-migration URI — in
the docs a user actually copies from.

**The docs facet is fixed** (`phoenixml-docs` branch `fix/dbxml-namespace-uri`): that URI
appears in no engine source except the comment in `FunctionLibrary.ResolveNamespace` recording
its migration, and there is no reverse-alias, so `dbxml:metadata()` could not resolve for
anyone following the site. The prefix in a prolog is user-chosen, so correcting the URI is
safe whichever way the naming lands.

**`FunctionNamespaces.Dbxml` is additionally a misleading identifier** — it names id 9, which
Core calls `PhoenixmlDb`/`phx`. Reading the two files side by side suggests a `Dbxml` id and a
`PhoenixmlDb` id exist separately. They are the same id.

#### The rationale that should drive the decision

`dbxml` is not arbitrary. `database-extensions.md` states it "follows the Berkeley DB XML
convention for database extension functions" and carries a comparison table against
Sleepycat's `http://www.sleepycat.com/2002/dbxml`; there is a `migration-guide.md` aimed at
that audience. The name is a deliberate compatibility gesture toward Berkeley DB XML users.

That makes **Core the odd one out**, not XQuery. `dbxml` belongs on the database *extension
function* namespace — which is what Berkeley DB XML users expect and what the engine and docs
already do. Core assigning it to `/2026/meta` is the entry that contradicts the intent.

**Recommendation for Lucas:** give `/2026/meta` a different conventional prefix in Core
(`meta`, or `phxmeta`) and let `dbxml` mean `/2026/db` everywhere. That aligns three tables by
changing the one with no users pointed at it, and preserves the Berkeley DB XML affordance.
The alternative — renaming `dbxml`→`phx` engine-side — discards that affordance and breaks
every published example and every user query already written against it.

Nothing renamed. db-engine has worked around it by binding only `phx` → `/2026/db`.

#### Found alongside: a real id collision, still latent

`FunctionLibrary.cs:648-650` documents in-code that `FunctionNamespaces.Ft = new(10)` collides
with Core's `NamespaceId.Xslt`, also 10. Harmless only because nothing round-trips an id
through both tables today. Resolving it needs a **Core** change (a FullText id), so it wants a
Core release — and Core is unchanged at 1.7.0, i.e. this does not ride along on an
XQuery/XSLT-only 1.8.0. Worth deciding at the same time as the prefix, since both are Core
registry edits.

### 51. `cache="yes"` has returned other calls' results in every version ever shipped (2026-09-11)

Found by parsers2 while implementing `new-each-time="no"`. Fix in flight as xslt#21 (CI
pending at time of writing). Recorded separately from the fix because the **shipped-version
consequence** is the part that needs a decision, and it is unusually clean-cut.

`xsl:function cache="yes"` memoizes on a key built by `AppendCacheKey`
(`DefaultXsltExecutionContext.cs:490`). The key is a **string concatenation**, and its
`default:` arm is `sb.Append(arg.ToString())`. That is not an identity:

| call | key | consequence |
|---|---|---|
| `f((1,2))` vs `f((3,4))` | both `System.Object[]` | **second call returns the first call's result** |
| `f('1')` vs `f(1)` | both `1` | string and integer share an entry |
| `f((1,2), 3)` vs `f(1, (2,3))` | commas collide across argument boundaries | unrelated calls share an entry |
| `map{1:'x'}` vs `map{'1':'x'}` | both `map{1:x}` | map keys render through the same arm |
| any node argument | `RuntimeHelpers.GetHashCode(node)` | a hash code is not an identity; two nodes can collide |

No error is raised. The function returns a well-formed answer computed for different arguments.
This is the worst shape a defect takes: silent, plausible, and in a feature a user turns on
specifically to make things faster — so it is most likely to bite exactly the workloads that
call the function many times with many different arguments.

#### Which releases carry it

`AppendCacheKey` and its `ToString()` arm both date to `030bfe1`, the **initial release
commit**. `git tag --contains 030bfe1` returns **47 of 47 tags — every version this engine has
ever published, 1.1.0 through 1.7.0 inclusive.**

There is no good version to roll back to. That matters for two things already in flight:

- **`docs/RELEASE-HYGIENE.md`.** The unlisting plan assumes a user pushed off a bad version
  lands on a good one. For `cache="yes"` that is not true of any published version, so the
  remedy here is "upgrade forward once #21 ships", never "pin back".
- **The 1.8.0 decision.** This is a stronger argument for cutting it than the conformance
  gains are. Conformance moves a number; this stops an engine returning wrong answers.

#### Scope, stated so it is not overstated

`cache="yes"` is **opt-in**. A stylesheet that never sets it is unaffected, and the attribute
is not a default. So this is not "every user got wrong answers" — it is "every user who used
this feature could have, in every version, with no signal that it happened." The honest
summary is a narrow blast radius and a total failure inside it.

#### Checked: we have been recommending it

`phoenixml-docs/docs/language-reference/xslt/instructions/functions.md` does three things:

- **:467** describes the correct semantics — "the result of each unique combination of
  arguments" — which is precisely what the implementation did not do.
- **:470** gives a worked `cache="yes"` example.
- **:511** is a **Tip actively advising users to turn it on**: "If your function depends only
  on its parameters ... consider `cache=\"yes\"`."

So the public site has been recommending a feature that returns other calls' results, and
describing the behaviour a reader would reasonably expect instead. That is worse than shipping
the defect quietly, and it is the part that should not wait for a release: the docs can carry a
caveat today, whereas the fix reaches users only in 1.8.0.

The W3C corpus does exercise it — four `decl/function` cases (`function-1031`, `-1034`,
`-1035`, and one more) set `cache="yes"`. They did not catch this because a memo key is only
wrong when two *different* argument shapes collide, and a conformance case that calls a
function one way never produces the collision. **A cache defect is invisible to any test that
does not call the same function twice with different arguments** — worth remembering when
reviewing #21's tests.
### 52. OPEN — accumulators read a later-declared accumulator's value one node late (2026-09-11)

Found by parsers2 while auditing built-in parameter cardinality. Recorded here because the
defect it uncovered is a **real engine bug that nothing was reporting**, and because of how it
surfaced.

XSLT conformance case `accumulator-077`: `accumulator-after('header-id')` returns the empty
sequence where an integer is expected. **This is the non-streamed variant (`STREAMABLE=false`),
so it is not the streaming path** — corrected here, having first been recorded as streaming on
my part. parsers2's instrumentation adds that the `header-map` end rule on `header-item` gets
`()` at least once, yet the final output is still correct, which points at an extra or early
evaluation of the rule rather than a wrong value. The value is then passed to `map:put` as its
`$key`. `map:put` declares `$key` as `xs:anyAtomicType` — exactly-one — so a correct engine
raises `XPTY0004` there. Ours did not enforce declared cardinality on built-ins at all, so
`map:put` accepted `()` and the case went on to fail somewhere downstream, or not visibly at all.

**The accumulator returning `()` is the bug. The lenient signature is why nobody saw it.**

#### Root cause — found by parsers2, confirmed here from source

Not an extra evaluation: an **off-by-one in declaration order**. At `header-item[1]`'s end
`accumulator-after('header-id')` returns `()`; at `header-item[2]` it returns `1`, the
*previous* item's value.

`WalkAccumulatorsAsync` (`DefaultXsltExecutionContext.Streaming.cs:581`) runs every
accumulator's end-phase rules at a node in **declaration order**, updating `currentValues[i]`
as it goes. `header-map` is declared before `header-id`, so when `header-map`'s end rule asks
for `header-id`'s after-value at this node, `header-id` has not run its end rule here yet — and
the read returns the entry the start phase left, i.e. the before-value, which is the previous
node's after-value.

The comment directly above that loop is the tell:

> `// Update after-values incrementally so cross-accumulator references work.`

Incremental update makes cross-accumulator references work **in one direction only** — from a
later-declared accumulator to an earlier-declared one. The comment asserts the general
property; the loop delivers half of it. A comment that states an invariant the code only
partly holds is the same failure shape as a check that fails open, and it is why this looked
intentional to every reader since.

`accumulator-077` passes its final assertion **by coincidence**: its single lookup
(`idref="1"`) happens to hit the entry written one item late. The output being right is not
evidence the values were.

#### Severity: this is not the conformance case, it is every stylesheet with this shape

**Any stylesheet whose accumulator reads a later-declared accumulator's after-value at the same
node silently gets the previous node's value.** No error, no diagnostic, a well-formed answer
built from stale data. Declaration order in the stylesheet — which an author has no reason to
think is significant — decides whether the result is right.

`WalkAccumulatorsAsync` dates to `030bfe1`, the **initial release commit**; `git tag --contains`
returns **47 of 47 tags, 1.1.0 through 1.7.0**. As with #51 there is no good version to pin
back to.

Unlike #51 this is **not opt-in**. `cache="yes"` is a feature a user switches on; accumulators
are ordinary XSLT 3.0, and nothing in the stylesheet marks the hazard. Two silent wrong-answer
defects now, both dating to the first commit, and this is the one with the wider blast radius.
It is also **not** the streaming path — `accumulator-077` is `STREAMABLE=false`.

**Checked, and unlike #51 the answer is clean: our docs do not demonstrate this shape.** The
seven `xsl:accumulator-rule` blocks in `phoenixml-docs` contain no `accumulator-after` or
`accumulator-before` call — every published example reads accumulator values from a *template*,
which happens after the walk has finished that node and is unaffected. So no docs caveat is
needed here, and the negative result is recorded so it is not re-investigated.

#### Fix approach (parsers2, in progress)

Evaluate **on demand**: when `accumulator-after('B')` is requested for the node currently in its
end phase and `B` has not run its end rule there yet, run it first. The existing
`_evaluatingAccEndPhase` guard continues to raise `XTDE3400` on a genuine cycle.

Deliberately not a static topological sort, because **accumulator names can be computed at run
time** — a sort over the declaration graph cannot see an edge that only exists once a name is
evaluated. Worth recording as the reason, since the static approach is the obvious one and this
is why it is wrong here.

#### A second silent defect in the same loop: the cycle check only caught self-reference

Implementing the fix exposed it. A **two-accumulator cycle** — `a` reads `after(b)`, `b` reads
`after(a)` — raised nothing on the old code: `a` read `b`'s provisional entry and finished, so
the cycle resolved to stale data instead of an error. The old `_evaluatingAccEndPhase` guard
only detected an accumulator referring to *itself*. Under on-demand evaluation it is now
correctly `XTDE3400`, which is what the W3C case `error-3400a` expects.

So the same loop carried two silent failures for the same reason: **reading a provisional entry
is indistinguishable from reading a settled one.** The off-by-one and the missed cycle are the
same defect seen from two angles — nothing ever asked whether the value it read had actually
been computed yet.

`#24` measures 10,160 → 10,162 with zero set losses, gaining `accumulator-079` and `error-3400a`.
Three of its five tests fail on the old code; the reader-second variants are guards, and are
labelled as such rather than counted as controls.

#### Why this is in the register rather than just in the fix

This is the same shape as #28, #44 and #47: *a check that fails open does not merely miss a
defect, it conceals one that other machinery would otherwise have surfaced.* The cardinality
check is not a new feature here — it is a detector that was switched off, and switching it on
is how the accumulator bug became visible at all.

Three instances now where tightening a lenient check revealed a genuine defect rather than
creating work. Worth remembering the next time a strictness change looks like churn: the
question is not "does this cost us cases" but "what has it been hiding".

#### It gates the cardinality work

parsers2 measured the audit with declarations corrected: QT3 **29,524 → 29,534 (+10), zero
per-set losses**. All 113 losses from the first blanket attempt were mis-declared signatures,
not spec disagreements. XSLT still loses 22, in four groups:

| cause | cases | status |
|---|---|---|
| `document()` — `$uri-sequence` declared `xs:string?`, spec says `item()*` | 19 | declaration fix |
| `format-number()` — XSLT override declares `$value` exactly-one `xs:double` | 1 | declaration fix |
| `string-join()` (`bug-2701`) — stylesheet never calls it, so it comes from engine internals | 1 | cause not yet found |
| `map:put $key` (`accumulator-077`) | 1 | **this entry — root cause found, see above** |

Sequencing agreed: the XSLT declaration fixes land first as their own PR (harmless alone), then
the XQuery check ships only when both sweeps show zero set losses. **`accumulator-077` must be
fixed before the check lands, or the check itself books a −1 it did not cause** — which would
then read in the history as the strictness change costing a case, exactly the wrong conclusion.


Applying that shape to #21 found one more collision class: map keys were rendered by
`ToString()` through the same `default:` arm, so `map{1:'x'}` and `map{'1':'x'}` shared an
entry. Covered by a test that fails on the old code (`11f9d48`).

The two-distinct-nodes case is the exception and is **documented as a guard, not a positive
control** — a `RuntimeHelpers.GetHashCode` collision between two live nodes cannot be forced,
so that test passes on the old code too. Saying so in the test comment is right: a test that
cannot fail on the defect it names is worth keeping and worth not miscounting as proof.

### 53. PATTERN — one slot, two meanings, distinguished only by control flow (2026-09-11)

Named while writing up #52, confirmed as a family by parsers2. Filed as a pattern entry in the
same spirit as #40 (the unswept-twin audit), because the value is in the sweep it suggests, not
in any single instance.

**The shape.** A single field, slot or type carries two *different* meanings, and nothing in the
value says which one it is — the reader is expected to know from where it sits in a loop, or
from which code path wrote it. Every such slot is a silent-wrong-answer defect waiting for a
reader who does not have that context. No error is possible, because both meanings are
representationally identical; the value is simply interpreted as the wrong one.

#### Instances found so far

| # | slot | meaning A | meaning B | how they're told apart |
|---|---|---|---|---|
| 52 | `currentValues[i]` in `WalkAccumulatorsAsync` | before-value (provisional) | after-value (settled) | position in the declaration-order loop |
| 52 | same slot, cycle detection | "not yet computed" | "computed, and this is it" | nothing — hence the missed two-accumulator cycle |
| 20 | `TextNodeItem` in the function-result assembly | genuine text content | duplicate of text already in `_output` | which path wrote it (`Output.cs:239`, `Functions.cs:2198`) |
| 37 | the prefixed-variable fallback | variable is **missing** | variable holds the **empty sequence** | nothing — both returned `null`, so `()` read as undefined inside map/array constructors and quantified expressions |
| 41 | `current-merge-key()`'s "no key" marker | there **is no** merge key | the merge key **is empty** | nothing — both are a null variable. **Latent: no failing case yet** |

#52's two halves are the clearest demonstration that these are one defect and not two: the
off-by-one and the missed cycle both reduce to *reading a provisional entry is
indistinguishable from reading a settled one.* Fixing the representation would have prevented
both; fixing either symptom alone would have left the other.

#### Why this is worth a deliberate sweep rather than case-by-case discovery

#40 established the precedent — an audit of paired implementations found more in one pass than
tripping over them individually had in a week. This family has the same property: the instances
are invisible to tests (both meanings produce well-formed output), so they surface only when
someone happens to construct the case that crosses the meanings. `accumulator-077` sat looking
like a cardinality nit; the `TextNodeItem` case looked like lost text.

**The audit question:** for each field that can hold a computed result, can a reader tell from
the value alone whether it has been computed yet, and what it represents? Where the answer is
"no, you have to know where you are", that is an instance.

Two candidates named but **not yet verified** — recorded so they are checked rather than assumed:

- `_sequenceAccumulator` and `_output` both carry function results (parsers2). Whether they are
  genuinely ambiguous or merely parallel needs reading before it is claimed.
- The empty sequence reportedly has two representations in places (`null` versus a zero-length
  collection). If so it is the same shape, and unlike the others it would be engine-wide.

Owner: parsers2 will take the sweep once the cardinality work lands. Not urgent; the instances
are old, and the point of naming the shape is that the next one gets recognised on sight.

#### The pattern is earning its keep (updated 2026-09-11)

Two of the five instances above — #37 and #41 — came out of the XSLT audit batch *after* this
entry was written, and neither was reported as an instance of it. They were found as ordinary
defects and recognised as this shape afterwards, which is exactly the outcome a pattern entry
is for: it does not find the bug, it tells you what you are looking at once you have.

**#37 is the cleanest statement of the shape yet.** A prefixed-variable fallback returned `null`
for *both* "this variable does not exist" and "this variable holds the empty sequence", so a
variable legitimately holding `()` read as undefined inside map and array constructors and
quantified expressions. One representation, two meanings, and the caller has no way to ask which.

**#41 is the first one caught while still latent** — `current-merge-key()` marks "no key" with a
null variable, and an empty merge key would be indistinguishable from it. There is no failing
case, which is precisely why it is worth recording: found by recognising the shape rather than
by tripping over an outcome. That is the pattern working forwards instead of backwards.

#### A sibling shape: the question is not ambiguous, the evidence is gone

parsers2 filed one more finding under this entry, and it is worth distinguishing rather than
absorbing. In `xsl:merge`, the `for-each-source` sub-sequences were concatenated and then
**sorted unconditionally**, so *"is this input in order?"* could never be asked — the input's
order was overwritten before anything looked at it. That is why the XTDE2220 check had nothing
to check.

Same *consequence* as this pattern — code needs an answer the data structure cannot give — but a
different *mechanism*. In every instance above the answer is **ambiguous**: one representation,
two meanings. Here the answer was **destroyed**: a property existed, and a step that did not
care about it overwrote it before the step that did.

**#43 is the clearest destruction instance, and it shows where this shape meets the fail-open
family.** `xsl:merge` resolved a relative `for-each-source` URI against the wrong base, then
**skipped the unretrievable source silently** — so `merge-041` produced output from *one* of its
two sources, with no error. A merge of two files that quietly merges one.

Read as destruction: the failure evidence existed for exactly as long as the failed retrieval,
and was discarded at that moment, so nothing downstream could ask *"did every source load?"*
Read as fail-open: a source that could not be retrieved was indistinguishable from a source that
loaded and contributed nothing. **They are the same sentence from two directions** — which is
this register's oldest refrain, *a thing that did not happen looks exactly like a thing that
happened and found nothing*, arriving at the destruction shape from the other side.

That convergence is the useful part. A silent skip is not merely a missing error; it is the
**act of destroying the evidence** that any later check would need. Now `FODC0002`.

**#50 is destruction again, and it was the largest single fix of the sweep (+21).** Invoking an
abstract component reported "not found", because **abstract functions and named templates were
dropped at merge time**. The component existed and was abstract; by the time anything asked
about it, the record that it had ever existed was gone, leaving the only answer a lookup can
give for something absent. `XTDE3052` — "you invoked an abstract component" — is unreachable if
the component was deleted before the invocation was checked. Ask before the erasing step.

#### The dangerous compound: destruction plus a fallback that manufactures a plausible answer

xslt #53 is the sharpest instance, and it adds something the earlier ones did not have. A
template body needing the whole subtree ran on a **buffered copy materialised from the live
reader**. The copy carried fresh node ids, so the per-node accumulator values the streaming pass
had already computed *correctly* were unreachable from it — the evidence existed, and the
identity connecting it to the node was gone by the time `accumulator-before()` asked.

That alone would be ordinary destruction. What made it invisible rather than loud is what
happened next: **the lookup missed and silently recomputed** the accumulator from its initial
value over that one node — which always yields initial-plus-one-rule-firing. So it returned
`1`: a number of exactly the right type, in exactly the right range, that a reader has no reason
to doubt.

**Destruction plus a plausible-answer fallback is worse than destruction alone.** Bare
destruction tends to surface as a null, an empty sequence, or a crash — something visible. A
fallback that recomputes converts a lost value into a *confident wrong one*, and nothing in the
output records that anything was lost. When auditing for this shape, the fallback path deserves
more suspicion than the lookup: **ask what the code does when it does not find what it expected,
and treat "recompute a fresh answer" as a defect rather than resilience.**

#### One record, two lifetimes

parsers2's first attempt at that fix copied *both* the pre- and post-descent accumulator values
and regressed the cases reading `accumulator-after()`. The reason is worth keeping: **at the
moment the copy is taken, only the pre-descent value is final** — the element's end-phase rules
have not run, so its post-descent value is not yet meaningful. The two halves of one record had
**different lifetimes**, and treating them as a single unit was the error.

That is a distinct trap from the ambiguity and destruction shapes, and a productive thing to ask
of any record that gets copied, cached or snapshotted: *are all of its fields valid at the same
instant?* Where they are not, copying the record is wrong however faithfully it is done — the
fix here records the copy's **origin** instead of its values, so each half is resolved when it is
actually final.

Worth keeping the two apart, because the remedies differ. Ambiguity is fixed by widening the
representation so it can say which meaning it holds. Destruction is fixed by **ordering** — ask
the question before the step that erases its answer, or preserve what that step consumes. A
sweep for "one slot, two meanings" would never have found the merge case.

The generalised audit question, covering both: **does the code still have the evidence for every
question it needs to answer, at the moment it needs to answer it?**

Related to neither and kept here because it is easy to mistake for the first:
`count([()](1))` returns `1` on pinned 1.7.0 even with a literal — XQuery's empty-array-member
representation. That belongs to the array/sequence family in the XQuery audit (and to the
empty-sequence-has-two-representations problem), not to this one: the ambiguity is in how an
empty *member* is stored, not in a slot doing double duty. XQuery-side, so it waits for that
track.

### 54. OPEN — the per-set gate cannot see a set that did not run (2026-09-11)

Found by following up parsers2's SIGSEGV report, and **proven rather than reasoned**: the
missing-set case was run against the gate's own awk.

A test host died mid-sweep (exit 139, `strm3`, just after `sx-IntersectExpr`), so
`sx-GeneralComp-le` and `-ne` never reported. Their rows are simply absent from the run's
results file. The per-set gate at `scripts/conformance.sh:329` is:

```awk
NR==FNR { base[$1]=$2; tol[$1]=($4==""?0:$4); next }
($1 in base) && $2 < base[$1] - tol[$1] { ...report regression... }
```

It iterates **CURRENT** and looks each row up in the baseline. A set present in the baseline and
absent from CURRENT is never visited, so **no comparison happens and nothing is reported.**
Demonstrated with a three-set fixture: removing a set worth 50 cases from CURRENT produces an
empty regression report.

This is the register's oldest refrain in a new place: *a set that did not run looks exactly like
a set that ran and found nothing.* See #28, #44, #47, #52.

#### What already protects us, and what does not

**Chunk level: covered.** `conformance.sh` captures each chunk's exit code, prints
`NO RESULT (exit $rc)`, and exits non-zero, so a crashed chunk fails the run. That is why
parsers2 saw this at all.

**Re-baselining: covered, deliberately.** `CONFORMANCE_UPDATE_BASELINE=1` merges rather than
overwrites (`:290` keeps baseline rows absent from CURRENT), so a partial run cannot silently
erase sets. That was a considered design and it holds here.

**Per-set gate: not covered.** The summary's per-set section is what a reader consults to answer
"did anything regress?", and it answers "no" for a set that vanished. The chunk-level failure
and the per-set clean bill appear in the same `summary.txt`, and only one of them is alarming.

The gap is narrow but real: it needs a set to disappear while its chunk still exits 0 — a
filter that matches nothing, a fixture that fails to instantiate, a rename — for the run to look
entirely clean. A crash is the loud version of a failure mode whose quiet versions are the
dangerous ones.

#### FIXED in xslt #26 (pending merge) — and my proposed patch was wrong

I proposed the obvious inverse of the existing `newsets` check: report every baselined set
absent from CURRENT. **parsers2 correctly refused it.** It checks *every* baselined set, so any
single-chunk run — `conformance.sh strm3`, or the xqts-only runs done routinely — would report
every other chunk's sets as NO RESULT. Hundreds of false alarms on the most common invocation,
which is #40's lesson exactly: a gate that cries wolf on ordinary use gets ignored, and an
ignored gate catches nothing. Recorded because the flawed version is the one that looks obvious.

What shipped instead scopes the check to sets whose chunk **actually ran**:

- QT3 sets (no `tests/` prefix) belong to `xqts`.
- `tests/<g>/` belongs to chunk `<g>`, verified against a full run — every chunk's sets live in
  its own directory.
- The `strm` sub-chunks' sets are read from the `InlineData` lists in
  `XsltStreamingTests{,2,3}.cs` — **the list the tests actually run**, not a second copy that
  could drift.
- If that list cannot be read, the check **fails closed** (`PER-SET CHECK BROKEN`) rather than
  going blind. The right default for a control whose whole purpose is catching silence.

Verified against recorded runs: the crashed run names exactly `sx-GeneralComp-le` and `-ne`;
clean `--all`, strm3-only and xqts-only runs report nothing; deleting a set from a single-chunk
run gets it reported; and feeding strm3's results to chunk `strm1` reports strm1's own sets,
which proves ownership comes from the class lists rather than from the results file.

#### A third gap in the same gate: a set with no baseline row is never defended (2026-09-11)

Found by parsers2 while wiring `decl/expose` (#72). The gate fails the run on two conditions —
`regressed` (a baselined set lost cases) and `noresult` (a baselined set did not report, the fix
above). **`newsets` is printed and never fails.**

So a test-set that runs without a baseline row contributes its cases to the total and is **never
defended**: it could lose every case it has and the gate would stay green. It is listed under
"per-set NEW sets" each run, indefinitely, and nothing ever requires it to be baselined. Visible,
but not gated — and a line that appears in every summary is a line nobody reads after the second
time.

This completes the set of three. The gate defends a set only while a baseline row exists for it:

| condition | gated? |
|---|---|
| baselined set loses cases | **yes** — `regressed` |
| baselined set does not report | **yes** — `noresult` (xslt #26) |
| set runs with no baseline row | **no** — reported only |

**Suggested fix**, symmetric with the other two: fail on `newsets` unless
`CONFORMANCE_UPDATE_BASELINE=1` is set. That makes adding a set a deliberate act with an obvious
remedy — exactly how `regressed` already behaves — rather than a line in a report. parsers2
sidestepped it on #72 by adding the baseline row in the same commit, which is the right habit but
not a mechanism.

#### The crash itself — recorded, not diagnosed

First test-host crash in 46 run directories. `strm3` then ran clean three times (883/895, 27/27
sets). Does not reproduce; no cause. If a SIGSEGV appears in `strm3` again, `sx-GeneralComp` is
where it stopped. Logged so a second occurrence is a pattern rather than another first.

### 55. FIXED 2026-09-11 — the evidence artifact described a different run

Surfaced by a side question from parsers2 — whether `conformance-results/summary.txt` being
tracked was intentional. It is, deliberately and for a good reason. The reason is currently not
being served.

`.gitignore:17-24` excludes the conformance output but re-includes `summary.txt`, explaining:

> README.md publishes conformance figures, and a published number whose evidence is not
> versioned cannot be audited later. That is not hypothetical — the previous headline claimed
> "2604/2661 tests" and no artifact in the repo could confirm or refute it.

So the mechanism exists precisely to stop an unauditable headline. **The committed artifact
today is from a timed-out XQTS run** (`6f28d38`, "conformance summary from the XQTS timeout
run"):

```
xqts    3600s  TIMEOUT after 3600s | 1271/1286 cases 98.8%, 15 failed
```

Nine test-sets, 1,286 cases, a run that did not finish. Meanwhile `README.md` and `STATUS.md`
publish **10,163/10,630** and **29,534/31,414** from the committed baseline.

**This is worse than the absence it was built to prevent.** A missing artifact makes a figure
unverifiable; a stale one makes it look *refuted*. Anyone auditing the README against the
repo's own evidence finds a 98.8%-across-1,286-cases timeout summary and concludes either that
the headline is unsupported or — worse — that 98.8% is the real number.

The control was built correctly and then quietly stopped tracking what it certifies. Same
family as #43 and #44: the machinery exists, the discipline around it lapsed.

#### Fix — make the artifact and the baseline a matched pair

They are currently independent, which is why they drifted. The rule that removes the drift:

> **`summary.txt` should be the summary of the run that produced the committed baseline.**
> A baseline raise updates both, in the same commit, or neither.

That makes the pair self-certifying — the baseline says what the figures are, the summary says
what produced them, and a reviewer can see at a glance whether they match. It also costs
nothing extra, because a baseline raise already involves exactly such a run.

parsers2 has the recorded runs from the `#25` baseline raise (`0a48c76`, two full `--all` runs
on xslt `be7b43d` / xquery `ab7c0ac`). Committing that run's summary restores the pair. Asked
rather than done here: this session deliberately does not run full sweeps, and fabricating a
summary from numbers rather than from a run would be the exact sin the artifact exists to
prevent.

#### The secondary hazard worth knowing

Because the default `OUT` is the tracked directory, **any local run overwrites the artifact** —
including a single-chunk run. `conformance.sh strm3` leaves a strm3-only summary in the working
tree, and committing that replaces the repo's whole-suite evidence with one chunk's. parsers2
hit the dirty-tree side of this and restored the file manually before committing.

Options, none taken yet, register owner's call:

1. **Keep it tracked, adopt the pairing rule above** — preferred; it keeps the audit property
   that motivated tracking and fixes the drift at its source.
2. Default `OUT` to an untracked directory and have baseline raises write the tracked copy
   explicitly. Removes the accidental-overwrite hazard, adds a step.
3. Stop tracking it. Cheapest, and throws away the audit property for the sake of a dirty tree
   — which is how the "2604/2661" situation happened in the first place.

#### Resolved — option 1, and parsers2 improved on the proposal

Fixed in xslt #27 (`c8d8d54`). Tracking stays; the pairing rule is written into
`scripts/conformance.sh` beside the rest of the baseline discipline, where someone raising a
baseline will actually meet it.

**parsers2 made the evidence better than I asked for.** I suggested committing the summary from
the run that produced the baseline. They committed a **fresh confirming `--all` on current
main** instead, and the reason is sharper than my proposal: raises take the *minimum across
runs*, so no single contributing run equals the baseline. Of the two behind this one, `bl2-2`
had `call-template` at 38 (total 10,164) and `bl2-1` lost a chunk to the SIGSEGV. **Neither
summary is the baseline.** A confirming run afterwards is — and it re-exercises the new #54 gate
on exactly the committed numbers.

That run came out XSLT 10,163 and QT3 29,534, matching the committed baseline exactly: no
gains, no regressions, no NO RESULT sets, exit 0. Verified here independently — the summary's
per-group counts sum to 10,163 and agree with the baseline group by group.

The rule as recorded: **raise the baseline and update `summary.txt` in the same PR, or neither;
and the evidence must be a clean confirming run on the raised baseline, never one of the runs
the raise was computed from.** It is process rather than machinery — raises are hand-edited
min-of-runs rather than `CONFORMANCE_UPDATE_BASELINE=1`, so the script cannot enforce it, but a
reviewer can: a raise whose PR carries no summary is incomplete.

### 56. FIXED 2026-09-11 — the conformance workflow has never gated a release

Raised by parsers2 relaying Lucas's release-cadence directive; diagnosed here.

**The workflow is red on every recent run — 8 of 8 — including BOTH `v1.7.0` release-tag runs
and the `v1.6.15` one.** Anyone reading a red Conformance check as "this release was gated" has
it backwards: no release on this line has ever passed it. It is not a gate; it is a signal
nobody can act on, which is worse than no signal because it occupies the place where a gate
would go.

#### ROOT CAUSE — found by parsers2, and it is not the timeout

**Correcting my own diagnosis below: raising the timeout would not have fixed CI.** I read the
`CONFORMANCE_TIMEOUT` override and stopped at the first sufficient-looking explanation. It is
real, and it is not why the workflow is red.

The nightly's xqts chunk finishes 172 sets in **103 seconds**, then **hangs**. Reproduced
locally against xquery `v1.7.0`: wedged after 36 sets. `dotnet-stack` on the xunit child, at
127% CPU, names the case — QT3 `op/same-key/same-key-023`. It builds a **421,875-key map**, then
runs `map:remove` plus `map:put` on it for every key inside an `every`. On 1.7.0 those copy the
whole map on every call (xquery#6), which is on the order of **10^11 entry copies**. And
`QuantifiedOperator` in 1.7.0 never polls cancellation, so the harness's 10 s per-case timeout
**cannot stop it**.

Two defects composing: one makes a case astronomically slow, the other makes it uninterruptible.
Either alone is survivable.

**Both are fixed on xquery main** — the HAMT map, and cancellation polling in hot loops. So:

> **#56 is caused by #57.** CI measures the *pinned* engine because the conformance symlink
> dangles there, and the pinned 1.7.0 cannot finish this case or be cancelled out of it. The
> nightly clears when the train pin moves to 1.8.0. Until then a longer timeout just burns more
> runner hours reaching the same hang.

That is worth sitting with: two separately-registered findings turned out to be one story, and
the one I diagnosed confidently was the downstream half.

#### RESOLVED by xslt #31

The hard per-case wall-clock is in: the QT3 runner starts each case on the thread pool and
abandons it 15 s past the cancellation deadline, so a case that ignores cancellation costs
**one case, not the chunk**. Measured both ways:

| engine | before | after |
|---|---|---|
| pinned 1.7.0 | chunk **wedged** | **161 s**, all 428 sets run, `same-key-023` named as abandoned |
| xquery main | 77 s | 77 s, nothing abandoned — unchanged |

The second row is the part that makes it a good fix: on an engine without the underlying
defects it changes nothing at all, so the remedy costs nothing where it is not needed. And the
abandoned case is now *named* in the output, which is what turns this from a hang someone has to
reproduce locally with a stack dump into something diagnosable from the CI artifact.

#### The harness change, as originally proposed

parsers2 is adding a hard per-case wall-clock in `XqtsTestRunner` — `WhenAny` against a delay,
abandoning a query that ignores cancellation. A non-cooperative case then costs **one case, not
the chunk**, and this would have been diagnosable from the CI artifact alone instead of needing
a local repro and a stack dump. Cooperative cancellation is only cooperative if the callee
agrees; the harness needs a remedy that does not depend on the engine's goodwill.

#### The timeout misconfiguration — real, but a contributing factor, not the cause

`scripts/conformance.sh` gives the QT3 chunk a longer limit than the XSLT groups, and the
exemption is conditional:

```sh
XQTS_TIMEOUT="${CONFORMANCE_XQTS_TIMEOUT:-3600}"
...
chunk_timeout="$TIMEOUT"
[ "$g" = "xqts" ] && [ -z "${CONFORMANCE_TIMEOUT:-}" ] && chunk_timeout="$XQTS_TIMEOUT"
```

The exemption applies **only when `CONFORMANCE_TIMEOUT` is unset**. `.github/workflows/
conformance.yml:103` sets `CONFORMANCE_TIMEOUT: '2400'` globally — so **xqts runs at 2400 s, not
3600 s.** The CI configuration cancels the exemption written for exactly this chunk. There is
already a dedicated `CONFORMANCE_XQTS_TIMEOUT` for raising xqts alone; CI does not use it.

The script's own comment records that this chunk was "killed every time and reported TIMEOUT,
which read [as a hang]" under the old 900 s default — the exemption was the fix for that, and
the CI env var silently re-broke it.

Still worth fixing on its own merits — a chunk-specific exemption that a global env var silently
cancels is a trap regardless of what else is wrong — but it is not the reason the workflow is
red, and fixing it alone would have changed nothing.

The earlier "~13,500 of 31,414 cases in 2400 s" reading is superseded: the chunk is not grinding
slowly through the corpus, it is **stuck on one case**. That also dissolves the supposed 30-70x
runner-vs-local gap, which was an artifact of dividing a hang by a case count. Worth recording
as its own small lesson: a throughput number computed from a run that never finished describes
nothing.

#### Why this matters beyond the red X

Lucas is now setting release policy (slow the cadence, lockstep the engines). A release process
naturally wants a conformance gate, and this repo *looks* like it has one. It does not. Deciding
policy on the assumption that conformance is enforced at tag time would be deciding on a false
premise — so either fix the workflow or state plainly that conformance is verified by the
committed baseline and the per-set gate in `ci.yml`, not by this workflow.

**Owner: parsers2** (harness). Diagnosed and registered here rather than patched, per the split.

### 57. OPEN — our published conformance figures describe a build that does not ship (2026-09-11)

Found by parsers2 while building dev mode. This one is mine to answer: I published the figures.

`PhoenixmlDb.Conformance.Tests` reaches XQuery by `ProjectReference` through the **tracked
symlink** `src/PhoenixmlDb.XQuery → ../../phoenixmldb-xquery/src/PhoenixmlDb.XQuery`
(git mode 120000). The conformance workflow checks out **only this repo**, so in CI that symlink
dangles, the referenced project cannot be found, and the build falls back to the
`PhoenixmlDb.Xslt` project's **pinned `PhoenixmlDb.XQuery` package**.

So the same suite measures two different engines:

| where | XQuery under test | XSLT result | QT3 result |
|---|---|---|---|
| locally (symlink resolves) | `phoenixmldb-xquery` **main** | **10,163** | **29,534** |
| in CI (symlink dangles) | pinned **package 1.7.0** | **10,160** | **29,506** |

**The gap is now measured on both suites** (parsers2, 2026-09-11, via xslt #31's runs): 3 XSLT
cases and **28 QT3 cases depend on unreleased XQuery.** That is the concrete size of the
question below — publishing the main figure over-states what an installed engine does by 3 and
28 cases respectively.

**The committed baseline, README, STATUS.md and the 1.8.0 draft all carry the 10,163 number** —
the *main* figure. Every one of them is therefore a claim about an engine combination that no
user can install today, because `PhoenixmlDb.Xslt` 1.7.0 depends on `PhoenixmlDb.XQuery` 1.7.0.

#### The fail-open underneath it

A `ProjectReference` whose target does not exist should stop the build — that is what MSB3202 is
for, and `phoenixml/src`'s dead absolute symlinks do exactly that (see the workspace CLAUDE.md).
Here it does not: **the build succeeds and silently substitutes the package.**

Confirmed by observation rather than by running a build: CI conformance runs reach ~13,500 test
cases before timing out. A build that failed on a missing project never reaches a test at all.
So the substitution happens, quietly, and the run looks entirely normal.

Same family as every other entry in this register: *a thing that did not happen is
indistinguishable from a thing that happened and found nothing.* Here it is a dependency that
did not resolve being indistinguishable from one that did.

#### Which figure should we publish?

**Recommendation: the shipped configuration is the headline.** A conformance percentage is a
claim about the product someone installs. "95.61%" should mean "install this and you get this",
not "check out two repos at main and you get this". The main-branch number is useful to
engineering and belongs in the register or a clearly-labelled at-HEAD line, not in the README
headline.

The good news is that this mostly resolves itself at release time: if Xslt 1.8.0 pins XQuery
1.8.0, and XQuery 1.8.0 contains main, then the two configurations **coincide** and the figure
describes both. The divergence only exists between trains — which is precisely now, and is
exactly the moment we were about to publish from.

So the concrete fix is smaller than the finding sounds:

1. Say in `README.md` which XQuery the figure was measured against. A figure without that is
   ambiguous regardless of which one we choose.
2. Measure the release figures in the **release configuration** — the pin, not the symlink.
3. Keep the main-branch number where engineering needs it, labelled as such.
4. Make the dangling reference **loud** rather than silent, so CI cannot quietly measure a
   different engine than a developer does. parsers2's dev mode already has a missing-sibling
   guard that fires; the conformance path wants the same.

**Open for Lucas** (parsers2 is taking the same question to him): whether published conformance
means *as shipped* or *at main*. Recorded rather than decided, and nothing has been republished.

### 58. A check that depends on the outside world, but only runs on our events, is stale between them (2026-09-11)

Found while porting `check-release-train.sh` into the MCP repos, and it began as a mistake of
mine: I wrote that `xquery-mcp` and `xslt-mcp` drifted to `1.6.14` through the whole 1.7.0 train
"and nothing said so". **That is wrong.** `check-pins.sh` catches exactly this and was failing
the moment I ran it:

```
FAIL  PhoenixmlDb.XQuery 1.6.14 is BEHIND published 1.7.0 by 2 release(s), policy allows 0
```

The true statement is narrower and more useful.

#### The shape

`check-pins.sh` compares a pin against **what is published on nuget.org** — a fact about the
outside world, which can change while our repository does not. But it runs only on `push` and
`pull_request`. So:

- At the last push, the pin was current and the check passed.
- `PhoenixmlDb.XQuery 1.7.0` was then published elsewhere.
- The check's answer became FAIL at that instant — and **nothing evaluated it**, because nothing
  pushed to those repos.

A week of drift, with a correct check in place that would have caught it on any run. This is
distinct from the fail-open family that fills this register: there, a check runs and wrongly
passes. Here, a check that would correctly fail **never runs**. Both end in silence; the
remedies differ.

Verified, not inferred: running `./scripts/check-pins.sh` against `main` in both MCP repos fails
today, while their most recent CI runs are green. The green runs are not wrong — they were right
when they ran.

#### Which of our checks have this property

Any check whose verdict depends on state we do not control:

| check | depends on | triggered by |
|---|---|---|
| `check-pins.sh` | latest published version on nuget.org | push / PR only |
| `check-release-train.sh` | nothing external — compares two values in the repo | tag push |
| conformance | the W3C corpora at a pinned SHA | schedule + push |

`check-release-train.sh` is immune by construction: both operands are in the working tree, so its
answer cannot change while the repo sits still. That is a property worth preserving in any
future gate — **prefer a check whose inputs are all local.**

#### Fix

Give `check-pins.sh` a scheduled trigger in every repo that runs it, so the answer is
re-evaluated when the world changes rather than when we happen to push. A daily cron is enough;
the drift it catches is measured in days. Without that, the guarantee it offers is only ever
"this was true at our last commit", which is not what anyone reads it as.

Repos affected: `xquery-mcp`, `xslt-mcp`, and any other consumer running `check-pins.sh` on
push alone. Noted for the lockstep work rather than done here — it belongs with the same
release-mechanics pass.

### 59. Two traps in the `xspec` fork — a misleading `gh` default and a permanently red Lint (2026-09-11)

Both found by parsers2 bringing the fork into the lockstep train. Neither is an engine defect;
both will cost the next person time, and one of them briefly produced a false conclusion.

#### `gh` answers about the wrong repository

The fork's checkout has upstream `xspec/xspec` as `gh`'s default repo, so a bare command
resolves against **upstream, not ours**:

```
gh pr view 1                      # upstream xspec/xspec PR #1 — MERGED years ago
gh pr view 1 --repo phoenixmldb/xspec   # ours
```

parsers2 briefly read their own open PR as already merged. That is the dangerous shape: it did
not error, it answered confidently about a different repository. **Always pass
`--repo phoenixmldb/xspec` in that checkout.**

Worth generalising: a fork is the one place where a tool's "obvious" default is someone else's
project. Any `gh` command in `xspec/` without `--repo` is suspect.

#### Lint is red on `phxspec` and has been, independently of any current work

`prettier` flags **21 files** — `phxspec-publish.yml`, `census/*.md`, `dotnet/README.md` — and
the previous push to `phxspec` was red the same way. So the fork's Lint check is not reporting
anything about the change in front of it.

**A permanently red check is an ignored check**, and an ignored check is indistinguishable from
no check at the moment it finally has something real to say. That is the same conclusion as #40
and #56, reached from a third direction: #56's conformance workflow has been red on every
release, and this is red on every push.

**Decision: do not block lockstep work on it.** Refusing to merge a functional change until
pre-existing formatting debt is cleared would be paying for someone else's backlog with the
release timeline, and the debt predates every current PR.

**But it should be fixed deliberately, not left.** Either bring the 21 files to prettier's
satisfaction in one hygiene commit, or narrow the Lint scope to what we actually maintain — the
fork carries a large upstream tree we did not write and do not intend to reformat, which is
probably why this was allowed to drift in the first place. The second is likely the honest
answer: a linter pointed at someone else's code will always be red, and configuring it to only
cover `dotnet/`, `census/` and our workflows would make it mean something again.

Owner: register side (release hygiene), not engineering. Not urgent; scheduled after the
lockstep trains.

### 60. Crucible is a real-world engine test bed we are not using as one (2026-09-11)

Answering Lucas's question of whether crucible needs development work. **Its code does not** —
the finding is that its *value to the engine* is being left on the table.

#### State of the repo, measured not assumed

Built and tested at `f697ffe` against its `PhoenixmlDb.Xslt 1.6.13` pin:

- `dotnet build Crucible.slnx -c Release` — **succeeded, 0 warnings, 0 errors**, under
  `TreatWarningsAsErrors` with `AnalysisLevel=latest-all`.
- `dotnet test` — **134/134 passing** (126 Core, 8 Extensions).
- **No** `TODO`/`FIXME`/`HACK` anywhere in `src/`.
- **No** engine workarounds, and no comment anywhere saying a feature was avoided.
- Seven stylesheets across two themes plus `_base`, all clean `version="3.0"`.

So: healthy, and idle. Last commit 2026-09-01, ten days before this entry, during which the
engines went 1.6.13 → 1.7.0 → well past it on main.

#### What it actually needs

1. **The pin bump**, which is train work (1.6.13 is the Tier 1 known-bad version; see
   crucible#9).
2. **Dev mode**, as parsers2 is adding to the other lockstep members, so it can build against
   engine *main* rather than only against a published package.

#### The opportunity, which is bigger than either

Crucible's own `RELEASES.md` says it plainly:

> Crucible transforms every page of phoenixml.dev through this engine, so it is one of the two
> real-world consumers that exercise it outside its own test suite.

Measured, that workload is **115 markdown sources → 264 HTML pages, 149 of them API pages.**
A document pipeline of that size is a different kind of test from the W3C corpus: the corpus
asks *is this construct conformant*, crucible asks *does a real stylesheet still produce a real
site*. The two fail in different ways, and we only run one of them.

Today that exercise happens **after** a release, against a pin three trains old. So the
real-world signal arrives too late to act on and describes an engine nobody is running.

**Proposal: run crucible against engine main as an XSLT regression gate.** With dev mode in
place this is a build of the docs site and the existing scale assertions — `deploy.yml:73-74`
already demands ≥250 pages and ≥140 API pages, which is exactly the right shape (assert it ran
at the expected SCALE, not merely that it did not fail). Wire that to engine main and an XSLT
change that breaks a real pipeline is caught the day it lands.

This fits the current XSLT-first directive particularly well: it is a ready-made regression
harness for the engine we are about to push hard on, and it needs wiring rather than building.

**Not needed:** moving the themes to XSLT 4.0. They are clean 3.0, nothing is straining against
it, and 3.0 is the more portable target for a tool other people may run.

### 61. OPEN — an indirect global cycle reports XPST0008 where XTDE0640 is due (2026-09-11)

Found by parsers2 writing tests for xslt #33, and **pre-existing — identical on main**, so it is
not a regression from that work.

A cycle among global variables reached *indirectly* is reported as a static error naming a
variable that is perfectly well defined:

```
$d → f:c() → a local variable reading $c,  with  $c := f:c()
→ "XPST0008: Variable $c not defined"        (XTDE0640 is due)
```

The direct form is handled correctly: `$c + 1` inside `$c` is caught statically as `XPST0008
... references itself`, which is right — a variable that literally names itself is a static
defect. The indirect form is a *runtime* circularity, and XSLT 3.0 gives it `XTDE0640`.

**Why the message matters more than the code here.** "Variable `$c` not defined" sends a reader
looking for a missing declaration that is not missing, in a stylesheet where the real fault is a
cycle they cannot see from that line. Anyone debugging this is looking in the wrong place
entirely — which is #21's lesson (improve the error first) in a new instance.

Suspected cause, **not yet traced**: the audit's `LazyValue` re-entry path, where a local
variable returns `()` on re-entry. An empty sequence where a value was expected then reads
downstream as "no such variable" rather than "you have a cycle". If that is right, the fix is at
the re-entry guard rather than anywhere near the error-reporting code, and the wrong code is a
symptom two layers down.

### 62. A stale generated `runtimeconfig.json` makes a clean branch look like a 25-case regression (2026-09-11)

Reported by parsers2, who lost time to it **twice in one day**. Registered because it is a
measurement trap, and this project has learned the hard way that a measurement which lies
quietly is worse than one that fails.

**Removing a `runtimeconfig.template.json` does not regenerate `bin/*.runtimeconfig.json` on an
incremental build.** The generated file survives, so its `AppContext` switches keep applying to
a branch whose source no longer sets them. Both times, a sweep showed what looked like a
**25-case regression** and the cause was `PhoenixmlDb.Xdm.StrictStringValue` still switched on
from the other branch.

The shape is familiar and worth naming: **build output is state, and state that outlives the
source that produced it will be attributed to the source that did not.** An A/B across two
branches is exactly the operation that exposes it, and exactly the operation whose whole purpose
is attributing a difference to a change.

**Rule: when A/B-ing branches that differ in a `runtimeconfig.template.json` — or in anything
that lands in build output rather than in source — delete the generated file first**, or build
clean. A stale `runtimeconfig.json` is indistinguishable from a real result, and it produced a
plausible number twice rather than an error once.

Related in kind rather than mechanism: #46 (tests that measure the machine), #43 (the harness
measured Debug and understated the engine), #57 (CI measures a different engine than a
developer does). All four are the same question — *what did this number actually measure?*

### 63. A streaming aggregate over the plain path answered from nothing (2026-09-11)

Found by parsers2, fixed in xslt #35. **The third silent-wrong-answer class this week, and on
the most ordinary path of the three.**

Under a declared streamable mode, a `match="/"` template whose body aggregates over the stream
returned answers computed from **no input at all**:

| expression | returned | regardless of |
|---|---|---|
| `count(//PRICE)` | `0` | what the document held |
| `sum(//PRICE)` | *empty* | what the document held |
| `count(//*)` | `0` | what the document held |

No error. A well-formed, plausible answer — `0` is exactly what a reader expects from a document
with no matches, so nothing about the output says "this never looked."

**Cause.** The document template's body was classified *literal-only* — it registers no
`for-each` subscriptions — and a literal-only body runs **before** the streaming pass. It
therefore evaluated against watchers that had seen nothing yet. `sum` over an empty watcher
returned `null` while the watcher's own documentation said it should return `0`, which is its
own small instance of code disagreeing with its comment.

#### Why the W3C corpus could not catch it

Every streaming test-set aggregates inside `xsl:source-document`, and that path already drains
the stream before the body runs. **The corpus exercises the shape it happens to use, not the
plain path** — and the plain path is the one the CLI takes: `xslt` auto-streams a file whenever
the stylesheet declares a streamable mode. `--no-stream` gave the same wrong answers.

This is the same structural blindness as #51's cache key, where four W3C cases used
`cache="yes"` and none could collide because none called a function two different ways. A suite
can have full coverage of a feature and zero coverage of the way users reach it. **Conformance
counts constructs; it does not count paths.**

#### Severity

Worse than #51 and comparable to #52. Not opt-in — declaring a streamable mode is ordinary
XSLT 3.0 — and the affected path is the default CLI behaviour rather than a library API. A user
running `xslt` over a document gets `0`, and `0` is a number they will believe.

Age not yet established. Fixed with `sum` over empty returning `0`, and W3C `function-5015a`
gains with zero real losses.

**For the 1.8.0 notes.** Alongside #51 and #52 this makes three silent wrong-answer classes in
the release, and this one should lead: it needs no opt-in, no unusual API, and no unusual
stylesheet — only a streamable mode and the CLI.

### 64. OPEN (XQuery-side) — the UCA collation ignores `alternate=shifted` (2026-09-11)

Found by parsers2 while implementing collation-aware `xsl:merge` (#42). Recorded here rather
than acted on: it is XQuery-side, and the XSLT-first order parks it.

```
compare('Akersberga', 'Akers Styckebruk',
        'http://www.w3.org/2013/collation/UCA?lang=sv;caseFirst=upper;alternate=shifted')
  returns  1
  expected -1
```

`alternate=shifted` means variable-weight characters — spaces and punctuation — are ignored at
the primary level. With it honoured, the space in `Akers Styckebruk` drops out and the two
strings compare as `Akersberga` against `AkersStyckebruk`, giving `-1`. The parameter is parsed
and then dropped.

**The blast radius is wider than merge.** This is `CollationHelper.MapUcaToStringComparison` /
`CompareUca`, which backs **`fn:compare`, `fn:sort`, `xsl:sort`, `xsl:for-each-group`, key
comparison — every collation-aware call in both engines.** Any caller passing `alternate=shifted`
silently gets codepoint-ish ordering instead, with no diagnostic. A user sorting Swedish names
gets a wrong order, not an error.

Suggested mapping, from parsers2: .NET's `CompareOptions.IgnoreSymbols` is the natural fit for
the shifted behaviour. Not verified here.

Worth noting what this is *not*: not a missing feature we never claimed. The collation URI
parameter is accepted, which is a promise that it is honoured. **Accepting a parameter and
ignoring it is worse than rejecting it** — a rejected option tells the caller to change
something; an ignored one tells them nothing while changing their results. Compare #47, where
shadow-attribute evaluation silently dropped what it could not compute.

**Blocked on the XSLT-first order, not on difficulty.** When the XQuery track reopens this
should be near the front: it is a small mapping fix with a wide correctness surface.

### 65. OPEN (XQuery-side, blocked) — two error-reporting defects found by the XSLT sweep (2026-09-11)

Both found by parsers2 working the XSLT failure list, both XQuery-side, both parked by the
XSLT-first order. Recorded so they are not rediscovered.

#### `fn:collection()` conflates "declared but empty" with "not found"

A collection that is declared and contains nothing raises `FODC0002` — the not-found error —
where the empty sequence is due (`fn/collection-001`, `-003`). Same for `uri-collection()`.

**This is #53's ambiguity at a different layer**, and the same conflation as #37's
prefixed-variable fallback: *absent* and *empty* share one representation, so a caller cannot
distinguish "there is no such collection" from "the collection is empty". #37 resolved it in
one direction (`null` for both), this resolves it in the other (error for both). The pattern
does not care which way the collapse runs.

Consequence for a user: a perfectly valid empty collection is an error rather than an empty
result, so a stylesheet that iterates a collection which happens to be empty aborts instead of
producing nothing.

#### An undeclared prefix in a function call reports the parser's error

`XPST0003` comes back from the parser where `XPST0081` is due (`error-XPST0081a`). `XPST0003` is
"syntax error"; `XPST0081` is "namespace prefix not declared". The query is *syntactically
fine* — the parser simply cannot resolve the prefix and reports the only code it has.

Worth noting as another instance of #21 (improve the error first): a user told their query has a
syntax error will re-read their syntax. The actual fix is a missing namespace declaration, which
is nowhere near what the message points at.

### 66. FIXED 2026-09-14 — a streamed `accumulator-after()` counts only the matched subtree (2026-09-11)

Found by parsers2 while fixing the buffered-copy identity loss, and **deliberately not fixed**.
Pre-existing, unrelated to that change, and registered rather than quietly encoded in a test.

Same stylesheet, same input, an accumulator that is never reset:

```xml
<doc><chap>…2 figures…</chap><chap>…2 figures…</chap></doc>
```

| mode | output | correct? |
|---|---|---|
| streamed | `2 2 2 2` | **no** |
| non-streamed | `2 2 4 2` | yes — the second `chap` has seen four figures |

`accumulator-after()` on the streamed path reports the total for the **matched subtree**, not for
the stream so far, so an accumulator that deliberately runs across the whole document resets in
effect at every match.

**Fixed 2026-09-14 (xslt #87, +2: `accumulator-003s`, `accumulator-005s`).** The cause was one
step below where this entry pointed. `ExecuteWithBufferedSubtreeAsync` takes its copy by consuming
the subtree from the reader with `ReadSubtree`, so **the forward pass never sees those events at
all** and never fires their accumulator rules. The running value stayed where the matched element's
own start rule left it; the subtree walk that answered `accumulator-after()` then started from the
accumulator's *initial* value, which is why every match looked like a reset.

Seeding that walk from the streamed node's recorded before-value was tried first and did not work —
the recorded value was itself 0 at the second `chap`, which is what proved the pass, not the walk,
was where the value was lost. The fix replays the skipped descendants into the pass's own state.

**One thing this entry got right and it mattered:** the non-streamed arm is the oracle. It was
checked first and independently, and the first repro written for it was unsound — it declared
`streamable="yes"` on both arms, so "non-streamed" reproduced the bug too and would have sent the
work after a phantom. Parameterising `streamable` gave `2 2 4 2` and the real target.

**Not covered:** the `snapshot()`/`copy-of()` buffering paths in
`DefaultXsltExecutionContext.Functions.cs` set `_streamingSubtreeBufferConsumed` without going
through `ExecuteWithBufferedSubtreeAsync`, and keep the old behaviour.

**This is an asymmetric pair (#40).** Two implementations of one semantics, and one of them is
right — the non-streamed path already produces the correct answer, so this is not an open
question about what the spec means. The remedy is to make the streamed path agree, not to decide
which is correct.

**Why it was left alone is the good part.** parsers2 had a fix in hand for the adjacent defect
and could have made these cases pass by encoding *either* answer in a test. Writing the test to
match current behaviour would have frozen the bug into the suite as expected output — the exact
move that makes a defect permanent and invisible. The test file says so explicitly instead.

Start at `ExecuteWithBufferedSubtreeAsync`; it is the same buffered-copy path as the fix in
xslt #53.

### 67. PATTERN — a proxy predicate is indistinguishable from the real one until it isn't (2026-09-11)

Named by parsers2, from the AVT/accumulator routing fix. A third shape alongside #53's
*ambiguity* and *destruction*, and distinct from both: here the evidence is present and intact,
and the code **asks the wrong question of it**.

#### The instance

At the document level, an accumulator read against the context node needs the value after the
whole tree is consumed, so such a body must be routed to the whole-input buffer. That was
detected for a `select`, but **not for an attribute value template** — because the AVT check
tested for input **navigation**, and an accumulator call navigates nothing.

```xml
<xsl:value-of select="accumulator-after('count')"/>   <!-- routed correctly -->
<result count="{accumulator-after('count')}"/>        <!-- folded to the initial value -->
```

The identical call, in the same stylesheet, answered differently depending on where it was
written. And it produced the same silent-plausible-number as #53: `0` for a count, with nothing
to say a routing decision had been made against it.

#### The shape

The property that mattered was **"does this expression need the whole input?"**. The predicate
implemented was **"does this expression navigate the input?"**. Navigation was a *proxy* — and
it correlated perfectly, until a function family arrived that needs the whole input while
touching no path at all.

> **A proxy predicate that is right for every case anyone tested is indistinguishable from the
> real predicate until it isn't.**

That is what makes this different from #53. There is no lost evidence and no ambiguous slot to
widen; the inputs were all there and the answer was computed correctly *for the question asked*.
The defect is one level up, in the choice of question — which is why it survives code review
comfortably: the implementation is a faithful answer to a reasonable-sounding predicate.

#### Why proxies are so durable

A proxy is adopted because it is **cheap and observable** where the real property is expensive or
abstract. "Does it navigate?" is a syntactic question answerable from the AST. "Does it need the
whole input?" is semantic and needs to know what each function means. The proxy is chosen
honestly, it passes every test written against the cases that motivated it, and its failure mode
is silent — it does not misbehave, it simply classifies a new case into the wrong bucket.

#### The audit question

For any classifier, router or streamability decision: **what property is this actually testing,
and what property does the caller believe it tests?** Where those differ, enumerate the cases
where the two come apart rather than trusting that the correlation holds. Function families that
consume input without navigating it are the obvious first place to look here; there may be
others.

Sibling to #53 rather than an instance of it. Same consequence — a confident wrong answer — by a
third route: not lost evidence, not an ambiguous representation, but a well-implemented answer to
the wrong question.

#### The corpus can embody a proxy too (added 2026-09-11, from #70)

parsers2 noticed the extension, and it is the more uncomfortable half. A **test suite** can stand
in the same relation to correctness that a proxy predicate stands in to the real property.

In #70, every `XTDE0450` case in the corpus exercises the **element-content** shape — the half
that is locally detectable. The other half of the same rule, a temporary tree from an untyped
variable, appears in no case. So:

> **An implementation that handles only the easy half of the rule scores 100% on that rule.**

Passing the tests is therefore a proxy for implementing the rule, and it fails in exactly the way
#67 describes: it correlates perfectly until a construct arrives on the side the suite never
sampled. #69 is the same observation from another direction — there, five cases passed because a
feature was absent; here, an incomplete implementation would pass because the missing half is
untested.

#### CORRECTION 2026-09-13 — the coverage argument was overstated, and the true version is more useful

I summarised a day's findings as *"the corpus was blind to all of them"*. **parsers2 corrected it
with the numbers, and I had one of them wrong outright: #82 moved a case** (base 349 / change
350, `insn WON [copy-5101]`), which was reported at the time and did not survive into my summary.

The honest split for 2026-09-13:

| outcome | findings |
|---|---|
| moved a conformance case | **#82** (`+copy-5101`), **#86** (`+as-0141`) |
| invisible to the corpus | #71, #79, #84 (unfixed) |
| not applicable | #69 — the suite does not reference the CLI project |
| plus | the streaming separator AVT fix, **+5** |

So **three of five**, not all of them. parsers2's framing, which I am keeping because it is the
right instinct: *weaker arguments that are true survive contact better.*

**And the correction contains something more useful than the number.** Two of the day's
hand-found defects were things **the corpus could see and was already failing on** — nobody had
traced those failures back to a cause.

> **A suite failing on a defect without anyone knowing why is a different problem from a suite
> not covering it — and a more tractable one.** Those failures are already sitting in the logs
> with their causes attached to nothing.

That splits the thesis below in two. The corpus genuinely cannot see some things (#71, #79, #85),
and that part stands. But part of what looked like blindness was **unattributed failure**, which
needs no new coverage at all — only someone asking what single cause explains a cluster.

**The cheap sweep that follows, and it has a demonstrated yield:** take currently-failing cases
and ask what one cause explains a group of them, rather than starting from a hand-probe. #86 was
found by probing; it would equally have been found by asking why `as-0141` failed.

**The consequence for how we read our own number.** A conformance percentage is not a measure of
correctness; it is a measure of agreement with a sample. Where the sample is systematically
biased toward the detectable half of a rule — and #69 and #70 are two independent demonstrations
that it is — the score overstates the engine in a way no amount of running it more carefully will
reveal. This does not make the number worthless; it makes it a floor on what is wrong rather than
a ceiling, and it is an argument for the kinds of evidence the suite cannot provide: real-world
pipelines (#60), and defects found by reading rather than by running.

### 68. OPEN — the W3C corpus contradicts itself on streamable accumulator AVTs (2026-09-11)

Found by parsers2, **deliberately not resolved**. Recorded because the blocker is a spec reading,
not a code change, and guessing would be worse than waiting.

Two sets disagree about the same construct — a `match="/"` template in a streamable mode reading
`accumulator-after()`:

| cases | expect | via |
|---|---|---|
| `accumulator-009s`, `accumulator-019s` | **`XTSE3430`** — a static "not guaranteed streamable" error | AVTs |
| `attr/mode` `mode-1107a`, `mode-1107c` | **the value** (`3`) | `xsl:value-of` |

**We pass `mode-1107a/c`.** Both cannot be right about whether that construct is streamable.

#### Why it was left alone

Making `009s`/`019s` pass means raising a static error on a construct we **deliberately support
elsewhere** and are currently scored correct for. That is not a fix; it is trading two passes for
two passes while making the engine less useful, on the strength of a guess about which set
reflects the spec.

The honest position: the engine is internally consistent and the corpus is not, and we do not yet
know which side is right. **An unresolved contradiction recorded is worth more than a resolved
one guessed** — the guess would be indistinguishable from knowledge six months from now.

Unblocking it needs XSLT 3.0 §streamability read against both cases. Until then these two are not
counted as engine failures, and should not be chased for the conformance number.

### 69. A test can pass because the feature is absent — and implementing it then scores as a regression (2026-09-11)

Named by parsers2 while parking a +5/−2 change. **This is the most consequential thing in the
register today**, because it says our conformance number may contain passes that were never
tests.

#### How it showed up

The conformance runner never supplied a **base output URI**, which is an invocation parameter the
catalog states explicitly — `<output file="results/x.xml"/>` in the test element, with
`file="#absent"` meaning "supply none" (`013`/`015` test exactly that absence).

Because it was never supplied, `current-output-uri()` returned the empty sequence
**everywhere**. That happened to be:

- **right** for the two cases that want empty (`016`, `017`)
- **wrong** for the five that want a value (`002`, `003`, `004`, `010`, `014`)

So two of the seven passed, and **five of the seven were never testing anything.** Supplying the
parameter — making the engine *more* correct — turned 5 failures into passes and, by removing the
accident, turned 2 passes into failures. A net +5/−2 that reads on the scoreboard as a partial
regression.

#### The shape, generalised

> **An assertion of "empty", "absent", "error" or "nothing happened" can be satisfied by a
> feature that was never implemented. The test then passes for a reason unrelated to the
> behaviour it names, and implementing the feature correctly is scored as a regression.**

This is the fail-open family (#28, #44, #54) seen from the *test's* side rather than the
harness's. #28 was a runner that scored an expected-error case as passing on any exception. This
is subtler: the runner is fine, the engine is fine, and the **capability the test depends on was
never switched on**, so the engine's degenerate answer coincides with the expected one.

#### The auditing consequence, which is the point

**Any conformance case whose expected result is empty, absent, or an error is suspect until the
feature it exercises is known to be implemented.** A green result there proves the engine
produced the expected output; it does not prove the engine *did* anything.

We have prior form: #28 found the harness inflating XSLT conformance by 1.9 points, and the QT3
figure had to be restated from 99.72% to a reproducible 93.94% (`phoenixmldb-xquery/docs/
CONFORMANCE.md`). Both were cases of a number describing something other than what it claimed.
This is a third mechanism for the same outcome, and unlike those two it leaves no trace in the
harness to find — the only way to catch it is to notice that a feature the suite exercises is
not wired up.

**A fourth invocation parameter, 2026-09-12: `package_version_resolution`.** The catalog states
the policy a test assumes — `<package_version_resolution value="lowest_version"/>` — the engine
**already supports it as a load option**, and the runner never passed it. So those cases ran under
the default and reported the version they had explicitly asked us not to choose:
`use-package-203b` wanted `1.0.0` from `{1.0.0, 2.0.0}` and got `2.0.0`. Fixed, +4.

That makes three catalog inputs now found unsupplied by this audit — the base output URI (this
entry), the principal-vs-secondary stylesheet role (#78), and this one — each of which made real
tests measure something other than what they named.

**Third instance, and it is the one with a number: #71.** Three parser comparisons against the
literal `"yes"` mean `streamable="true"` reads as not streamable, so **27 streaming cases have
been passing while running unstreamed** — they ask for streaming, get none, and agree with the
expected output because unstreamed and streamed are supposed to produce the same answer. That
agreement is exactly what hides the class.

Three instances in one day, from three different mechanisms — an unsupplied invocation parameter
(this entry), an untested half of a rule (#70), and an attribute value the parser does not
recognise (#71). They are not variations on one bug; they are variations on one *blind spot*.

#### The audit was run properly, and it is now largely ANSWERED (2026-09-12)

parsers2 enumerated every element appearing inside `<test>` and `<environment>` across the
corpus, filtered to catalog vocabulary, and grepped the runner for each. **Nineteen catalog
inputs the runner never reads** — and then, because a raw count misleads exactly as the
45-vs-43 count did, measured how many cases declare each and how many of those are *currently
failing*:

| catalog input | cases | failing |
|---|---|---|
| `schema` | 792 | **9** |
| `resource` | 33 | 3 |
| `supported_calendars_in_date_formatting_functions` | 4 | **3** |
| `collation` | 39 | 1 |
| `document` | 26 | 1 |
| `extension-function` | 1 | 1 |
| `languages_for_numbering` | 5 | 1 |
| `ordinal_scheme_name` | 3 | 1 |
| `default_language_for_numbering` | 1 | 1 |
| `module` | 1 | 0 |
| `additional_normalization_form` | 12 | 0 |
| `default_html_version` | 4 | 0 |
| `default_output_encoding` | 2 | 0 |
| `unparsed_text_encoding` | 4 | 0 |
| `available_documents` | 1 | 0 |
| `detect_accumulator_cycles` | 1 | 0 |
| `default_calendar_in_date_formatting_functions` | 2 | 0 |

**Nineteen unread inputs, roughly twenty failing cases behind all of them combined, and seven
inputs where every declaring case passes anyway.** `schema`'s 792 is almost entirely cases that
declare a schema and do not need it — only 9 fail.

Two readings, both worth keeping:

**1. The question was worth asking, and is now answered for this corpus.** It found three real
ones — the base output URI (this entry), the principal-vs-secondary stylesheet role (#78), and
`package_version_resolution` (#69 above) — all fixed on 2026-09-12 for **+13 between them**. The
tail is thin. **Nobody should read "19 unread inputs" as 19 opportunities.** An audit that
concludes is worth more than one that becomes a standing backlog item, and this one concluded.

**2. Frequency did not predict value.** `package_version_resolution` appears in **12** cases; the
base output URI in **570**. The common input was not the valuable one. What predicted value was
whether the input **changes the answer** for the tests that declare it — which is visible only by
measuring, never by counting.

That second reading generalises past this audit: *the size of a population is not evidence about
the size of the defect within it.* Both times this register has been misled by a count — the
45-vs-43 error and "19 opportunities" — the error was treating an occurrence count as an impact
estimate.

Best remaining candidates by the measure that matters:
`supported_calendars_in_date_formatting_functions` (3 of 4 declaring cases failing) and
`resource` (3 of 33). Both small, and both lower value than #79.

#### Original audit note, superseded by the table above

Worth a deliberate pass, after the current track: **enumerate the invocation parameters the
catalog can set, and check which ones the runner actually reads** — and, from #71, **enumerate
the attribute values the spec allows and check which ones the parser accepts.** #41 already records eight
catalog environment attributes the XSLT runner never reads; this is the same audit reaching a
different attribute, and it found five hollow passes in one small test-set. There is no reason to
think `fn/current-output-uri` is the only place.

#### The change itself, parked with its dependency

Not merged. The +5 is real and the −2 are real, and both losses are one defect:
`current-output-uri#0` called as a **function item** (`016`) and inside an **inline function
body** (`017`) must return `()`, and **nothing distinguishes a dynamic invocation from a direct
one** by the time the XSLT-side implementation runs. The signal belongs on
`QueryExecutionContext`, beside `InsideXslEvaluate` — **XQuery-side, so parked by the XSLT-first
order.**

parsers2 declined to merge two known-wrong answers to buy five right ones, holding the
no-per-set-losses line kept all session. The right call: the scoreboard would have improved and
the engine would have shipped two answers we knew were wrong. The diff is small and can be redone
in minutes once the XQuery track opens — this entry exists so that is a lookup rather than a
rediscovery.

### 70. OPEN — SENR0001 vs XTDE0450 needs a destination marker, not an error-code swap (2026-09-11)

Declined by parsers2 after probing, and registered with the evidence so the next attempt starts
from the finding rather than the symptom.

`decl/output-0710/0711/0712` expect `SENR0001` where we raise `XTDE0450`. The distinction is
real and the spec is unambiguous:

- **`XTDE0450`** — a map used as the **content** of an element or document node.
- **`SENR0001`** — a map that reaches the **serializer** as an item of the final result.

The three cases do the latter: `<xsl:sequence select="$maps"/>` as the whole result of the
initial template.

#### Why it is not a code swap — the probe

Five shapes, all reaching the one code path where the error is raised:

| shape | `attrs` | `doc` | distinguishable? |
|---|---|---|---|
| top-level sequence | 0 | 0 | — |
| inside an element | 1 | 0 | **yes** |
| inside `xsl:document` | 0 | 1 | **yes** |
| variable with tree content | 0 | 0 | **no** — identical to top level |
| variable with map only | 0 | 0 | **no** — identical to top level |

A temporary tree built by an untyped `xsl:variable` is `XTDE0450` per §5.7.1, and **at the moment
of the error it is indistinguishable from the top-level result.** So the only rule available from
local state — *`attrs==0 && doc==0` means serializer* — would win the three tests and start
answering `SENR0001` for a case that is genuinely `XTDE0450`.

**And the suite would not catch the damage**: every `XTDE0450` case in the corpus is an
element-content shape, which is the distinguishable half.

#### What it actually needs

A **destination marker** threaded to the serialization point — *this sequence is becoming a
document node* versus *this sequence is the result*. That is engine plumbing, not an error-code
change, and it is the right fix rather than the affordable one.

#### The trade being refused, now twice in one day

This is the second cluster declined for the same reason (the first is #69's `+5/−2`), and the
shape is worth naming rather than counting:

> **Buying N conformance cases by making a case silently wrong that the corpus cannot see.**

Both times the scoreboard would have improved and the engine would have got worse, and in neither
case would any test have registered the loss. A register that only recorded merged PRs would show
two clean wins here; what actually happened is two correct refusals. **The absence of those
commits is the achievement.**

### 71. OPEN — `streamable="true"` is silently not streamable; 164 corpus stylesheets have never streamed (2026-09-11, re-measured 2026-09-13)

Found by parsers2. **The third and largest instance of #69, and the only one with a direct
user-facing cost.**

XSLT boolean attributes accept `yes`/`no`/`true`/`false`/`1`/`0`. Three places in the parser
compare against the string `"yes"` alone:

| attribute | file |
|---|---|
| `xsl:source-document streamable=` | `StylesheetParser.Output.cs` |
| `xsl:mode streamable=` | `StylesheetParser.Declarations.cs` |
| `xsl:param required=` | `StylesheetParser.Variables.cs` |

So **`streamable="true"` reads as not streamable.** The construct runs unstreamed, and for
`xsl:source-document` its body is never checked for streamability at all.

#### The user-facing cost, which is the part that matters most

Streaming exists so a document larger than memory can be processed at all. A user who writes
`streamable="true"` — the spelling half the world reaches for first — **gets no streaming and no
diagnostic.** On a small document the answer is right and nothing is visibly wrong. On the large
document that motivated the attribute, the engine buffers instead of streaming.

Nothing in that story produces an error message naming the cause. This belongs in the 1.8.0 notes
once fixed; it is not a conformance nicety.

#### The 27 hollow passes

Fixing the three comparisons measures **+3 / −27**:

| set | cases | n |
|---|---|---|
| `attr` | `streamable-001/-002/-037/-039/-041`, `-046..-053`, `-063..-066`, `-135`, `doe-0802` | 19 |
| `insn` | `stream-200/-201/-202/-203/-211` | 5 |
| `decl` | `accumulator-003s`, `accumulator-005s` | 2 |
| `strm3` | `sx-gc-eq-801` | 1 |

Every one asks for streaming with `streamable="true"`, **has been running unstreamed**, and
passes because the unstreamed answer agrees. They are not regressions; they are the bill for a
feature that was never switched on, arriving at once.

This is #69's line at its sharpest. A green result proves the engine produced the expected
output; it does not prove the engine did anything. **Here it proves the engine did the opposite
of what the test asked and got away with it** — precisely because streamed and unstreamed are
supposed to agree, which is what makes the whole class invisible.

It also puts a number on #70's "a floor on what is wrong, not a ceiling": **27 cases in the
streaming sets are currently measuring nothing.**

#### RE-MEASURED 2026-09-13 — the scale is larger and the cost is smaller

Re-measured after two days of intervening fixes. **The defect is unchanged; both numbers moved.**

**Scale.** `641` corpus stylesheets use `streamable="yes"` and stream properly. **`164` use
`"true"` or `"1"` and have never once been streamed on this engine** — roughly a fifth of the
streaming corpus, silently demoted, with the streamability rules never running over those bodies
either.

**Cost, now 19 rather than 27:**

```
base 332  ->  change 349
attr   LOST [doe-0802, streamable-037/039/041/047..053/064/065/066/135]  WON [mode-0014]
decl   LOST [accumulator-003s, accumulator-005s]
insn   LOST [stream-211]
strm3  LOST [sx-gc-eq-801]
misc   WON  [error-3430a]
```

**The losses are output mismatches, not errors.** The stylesheets now genuinely stream, and **the
streaming implementation produces wrong results.** They were passing because streaming was
switched off behind their backs.

> This is #69's family an order of magnitude larger: a test can pass because a feature is absent —
> and here **a whole attribute spelling was the off switch.**

**Not shipped.** parsers2 dropped it from their branch and kept it as a patch at
`/tmp/claude-1000/0001-streamable-true-silently-meant-not-streamable.patch`. **That location is a
shared temp directory and should be a branch before anyone relies on it.**

#### The sweep tells you which cases moved. It never tells you who gets hurt.

Recorded 2026-09-13, after a release-timing decision nearly turned on a misreading — mine.

I summarised this change to Lucas as one the conformance numbers *"scored as +17"*. **They said
the opposite: 332 → 349, nineteen losses against two wins.** The numbers were entirely honest
about the cost.

**What they could not show is that the cost was a different kind of thing.** Nineteen losses
normally means *tests we were already failing, now visible*. Here it means **real users, running
stylesheets that worked, getting wrong output.** In the sweep's output those two are
**indistinguishable**:

> **A correct-looking regression report and a user-harming change produce identical numbers.**

That is worse than a misleading measurement, because nothing about it invites a second look. The
19 were reported accurately, read accurately as "cases we already fail", and the user-facing
consequence was invisible in every artifact the tooling produces.

**The general rule:**

> For a change that alters **what executes** rather than **what a result is**, the case counts
> cannot answer the question that matters. Someone has to ask *"who is running this today, and
> what happens to them?"* **by hand — and nothing in the tooling prompts it.**

This belongs with #76's ad-hoc section: the sweep is sound, and the risk is in what is concluded
from it. But it is a distinct failure from the ones recorded there — those are cases where the
measurement was wrong. **Here the measurement was right and answered a different question than
the one being asked.**

#### Exposure falling: 19 → 8 (2026-09-13, later)

    #83  deferred bodies + literal-element attribute AVTs   +3
    #84  multi-step select routing                          +6
    #85  shallow copy misread as consuming (over-rejection) +1
    #86  position() across a multi-step descent             +1  (in CI)

All zero-loss. `attr` chunk 44 → 33.

**The remaining eight are not more of the same**, and parsers2 says so before the slope changes
rather than after:

```
streamable-064/065      tunnel parameters; -065 mixes a secondary document and a non-streaming mode
streamable-135          has-children() in a predicate
streamable-137/138/139  fn:path() output
accumulator-003s/005s   duplicated output
stream-211, sx-gc-eq-801  content dropped
doe-0802                double-escaping
```

Everything cleared so far was **routing or classification** — a predicate not consulted, an entry
condition written for one call shape, a counter scoped per level. What remains is heavier;
`streamable-065` currently emits `<out/>`, total content loss, combining `doc()`, a non-streaming
mode and tunnel params in one case.

> **Expect the rate to drop from here.** Said in advance so the slope is not read as a stall
> later — which is the failure mode of reporting a run of easy wins without characterising what
> is left.

#### Superseded — exposure at 10 (2026-09-13, earlier)

Three streaming fixes from this set have landed since the cost was measured. **The exposure is
now 10, not 19**, and still falling:

```
remaining:  streamable-030/053/064/065/066/135/137/138/139
            accumulator-003s/005s   (duplication)
            stream-211, sx-gc-eq-801 (dropped content)
            doe-0802                 (double-escaping)
```

That **materially changes the release calculus**: shipping with a note is a much easier call at
ten than at nineteen, and the question may stop being interesting on its own before it needs
answering. Recorded so the "19" in the decision above is not quoted as current — the number was
correct when measured and has a date on it for this reason (#89).

#### Why it is Lucas's decision

The fix is **unambiguously correct and makes the numbers worse.** The 19 are **not new defects** —
they are existing streaming defects that were invisible. Shipping it converts a silently-wrong
baseline into an honestly-worse one.

Same trade as #88's catalog alignment, and the two belong together: **both are about the
denominator describing what the engine actually does.**

**Recommendation: take it.** A baseline that counts unstreamed runs as streaming passes will keep
hiding streaming defects indefinitely, and **every future streaming fix gets measured against a
corrupted floor.** If he takes it, the 19 become a work list rather than a regression — and
probably the most valuable streaming work available, since each is a real defect with a
reproducing case already written.

But it must be a **deliberate decision with the 19 named**, not something slipped in under a
correctness heading.

**Unresolved caveat if it lands.** The fix uses the shared `NormalizeYesNo`, which raises
`XTSE0020` on a non-boolean value. **No test exercises that path**, so whether an invalid
`streamable` value should be an error or a silent false is **unverified against the spec** and
wants checking before it ships.

#### Sequencing — this is a project, not a three-line fix

**Whoever fixes the attribute parsing inherits 27 streaming failures the same day.** The parser
change is trivial; what it uncovers is a streaming-correctness effort. It should be planned and
scheduled as one, not stumbled into by someone tidying a boolean comparison.

Two notes for whoever takes it:

- **`error-3430a` comes free.** `streamable="true"` with `//a` and `//b` — two consuming operands
  — should raise `XTSE3430`, and the classifier already implements the multiple-consuming-operands
  rule correctly. It was simply never called. Turning the attribute on calls it.
- **`xsl:param required="true"`** is the same commit shape and probably harmless alone, but was
  **not measured separately**. Do not assume it rides along cleanly.

parsers2 reverted the boolean fix and shipped only the clean part of that branch, rather than
merge a −27 into a track whose whole discipline has been zero per-set losses. Correct call, and
the same refusal as #69 and #70 — with the difference that here the losses are honest arrears
rather than new damage, which is exactly why they need scheduling instead of suppressing.

### 98. A stale sibling checkout read as a 34-case regression (2026-09-14)

Found by parsers2 while restating the published figure for #57, and worth recording because the
false signal was indistinguishable from the real thing it imitates.

A full `--all` reported **21 test-sets below baseline, 34 cases**, 28 of them in QT3. That is the
exact shape of a genuine regression and it survived the obvious checks: it reproduced across three
runs, and it was present on `main` as well as on the branch, which ruled out the change under test.

**It was the environment.** `phoenixmldb-xslt` reaches XQuery through the
`src/PhoenixmlDb.XQuery` symlink into the sibling `phoenixmldb-xquery` checkout, and that checkout
was sitting **detached at the v1.7.0 release commit, 27 behind main** — left over from an A/B
three days earlier. Restoring it to main cleared **18 of the 21 sets** and returned QT3 to 29,534
on the nose. Same engine the whole time; only the dependency source had moved.

The three that remained are `attr/disable-output-escaping` (−1), `attr/streamable` (−2) and
`strm/sx-GeneralComp-eq` (−1) — the known, recorded cost of #82, which Lucas decided to keep.

#### What this says about the gate

The per-set gate is the right control and it did its job — it refused to stay quiet. But **it
cannot distinguish "the code got worse" from "a sibling checkout moved"**, because both present as
the same number going down. Nothing in the run states which XQuery it was measured against, so the
reading depends on someone thinking to look.

Two corrections came out of it:

- The README claimed a local run measures `phoenixmldb-xquery` **main**. It measures whatever that
  tree is checked out at. Now stated as such, with this episode as the worked example.
- The first diagnosis of the same evidence was **also wrong, in the other direction**: from an
  unfetched local object database, `origin/main` looked 27 commits behind and still on 1.7.0, and
  the conclusion drawn was "the 1.8.0 version bump never happened." A `git fetch` showed `v1.8.0`
  tagged and `<Version>1.8.0</Version>` on main all along. **Stale refs produced a confident wrong
  answer twice in one session, in opposite directions.** Fetch before concluding anything from
  `origin/*`.

#### The lesson that generalises

A measurement harness that reads one repo but *builds* against another needs the second one's
identity in its output. Until it is, a clean-looking baseline gate can be measuring something
other than the commit under test — and it will say so in the only vocabulary it has, which is
"regression".

### 72. PARTLY RESOLVED 2026-09-14 — five W3C test-sets present in TestData and never run (2026-09-11)

Found and measured by parsers2. **The fourth blind spot in one day, and the outermost yet**: not
"which attribute values does the parser accept" or "which invocation parameters does the runner
read", but **which test files does the harness open at all.**

The runner's `InlineData` lists **259 of the 264** test-set files present on disk. The five it
does not:

| set | cases | pass | fail |
|---|---|---|---|
| `tests/decl/expose` | 42 | 9 | **33** |
| `tests/fn/system-property-gen` | 166 | — | **168** |
| `tests/fn/collation` | 5 | 0 | **5** — cannot run, see below |
| `tests/decl/import-schema` | — | — | not measured |
| `tests/sandp/_base-expressions.xml` | — | — | not measured |

**`decl/expose` is now wired (xslt #57, merged 2026-09-14) at a gated baseline of 9/42.** The
published figure moves from 10,163/10,630 (95.6%) to **10,307/10,672 (96.6%)** — the denominator
grows by 42 because a set that was never opened is now opened, so wiring it LOWERS the percentage
relative to what the same engine would have scored on the old denominator. That is the intended
direction: a number that rose because a failing area stayed invisible is worse than a smaller one
that counts it.

The other four remain unrun, and remain policy calls rather than engineering ones.

#### `decl/expose` — the one that matters

An entire feature area scored at **zero information**. We have **no `XTSE3010` and no `XTSE3020`
anywhere in the engine**, and nothing ever said so because the set that would is not wired up:
`expose-901..903` (XTSE3020, exposing an undeclared component) and `expose-908..911` (XTSE3010,
raising visibility from private) sit unrun. It also explains `error-3010a`/`3020a` directly.

Two cases additionally show `XTSE0630` "duplicate global variable", which looks like a real
defect in how exposed variables are merged.

#### `fn/collation` — verified as an upstream corpus defect, not our clone

Checked here rather than left as a policy question. `tests/fn/collation/` at corpus commit
`fddf1cf` contains **only `_collation-test-set.xml`**. The catalog references
`collation-001.xsl` through `-005.xsl`; **none of the five exist.** A `--depth 1` clone takes the
complete tree at a commit, so these are absent upstream, not dropped by our checkout.

So these five cannot pass at any level of engine quality. **Excluding them is not a dodge — it
is the only honest option** — provided the exclusion is documented, counted, and says *why*.
Worth reporting upstream.

#### `fn/system-property-gen` — a distortion question, and the trap inside it

166 near-identical generated cases would be **~4% of the XSLT corpus**, one generated family
outweighing several real feature areas and moving every future percentage.

The trap is that "exclude it, it would distort the number" and "exclude it, we fail it" produce
the same commit. **The discipline that keeps them apart: exclude only for distortion, never for
difficulty, and make the exclusion visible in the published figure.** The cleanest form is not an
exclusion at all — **report handwritten and generated sets as two figures**, so neither hides the
other and nothing is dropped for failing.

#### Sequencing — my judgement, since parsers2 asked

1. **Wire `decl/expose`, but land it with the baseline raise.** It is real files, a real feature,
   and 42 cases of truth we currently do not have. But it moves the denominator, and so does the
   pending #57 mode decision. Doing both in one restatement means **one number change with one
   explanation**; doing them separately means the published figure moves twice in a week for
   reasons that will blur together. Prepare it now, merge it with the raise.
2. **Exclude `fn/collation`** on the evidence above, with the reason in the exclusion.
3. **Do not decide `system-property-gen` for the number.** Two figures, or include it and say so.

The general form of this audit now has three questions, all of which found something today:
**which attribute values does the parser accept (#71), which invocation parameters does the
runner read (#69), and which test files does the harness open (this entry)?**

### 73. PATTERN — a permanently-failing operation is an accidental guard, and everything behind it is untested (2026-09-11)

Named by parsers2 from `fn:transform` (xslt #59), where three defects were stacked and **each one
hid the next**.

| # | defect | exposed by |
|---|---|---|
| 1 | `delivery-format='raw'` returned a serialized **string** — a template constructing nodes writes to the output buffer, not the sequence collector, so the raw path had nothing typed and fell back to text | — |
| 2 | the parse failed on secondary results — a serialized result document starts with an XML declaration, which cannot sit inside the wrapper the parse uses | fixing 1 |
| 3 | the secondary result came back holding the **primary result's content** — parsed into a fresh node store the surrounding evaluation never consults, so the node resolved its children against the *caller's* store, where the same ids belong to other nodes | fixing 2 |

**Defect 3 has been latent since the code was written, and was unreachable while defect 2 held.**
A parse that always fails never produces a node whose store affiliation matters. The broken parse
was, accidentally, a guard.

#### The rule

> **When you fix a failing parse, lookup or guard, assume the code behind it has never run.**
> Everything downstream of a permanently-failing operation is untested *by construction* —
> whatever the coverage numbers say, no test has ever reached it, because nothing could.

Coverage instrumentation will report those lines as uncovered, which sounds like it should have
warned someone. It does not, in practice: uncovered lines behind an error path are the most
ordinary thing in a codebase, indistinguishable from defensive branches nobody expects to hit.
The signal only becomes legible once you know the operation in front fails *always* rather than
*sometimes*.

#### Why the corpus could not have caught it

Had only defect 2 been fixed, the result would have been a **wrong-content secondary result** and
the conformance run would have shown **+0 with no losses** — a completely clean A/B. Nothing in
the suite compares a secondary result's content against the document it should have come from.

What caught it was **reading the output rather than the score**: the secondary said `892` when the
primary said `892`, and the secondary document plainly contains `479`. This is the direct
vindication of #70's "a floor on what is wrong, not a ceiling" — the number was not merely
uninformative here, it was *perfectly clean* while the engine returned the wrong document.

#### The tell: plausible for the wrong reason

`892` is not a nonsense value, a null, or a zero. It is a **real number produced by the real
transform** — just the other one. Same family as #53's confident-wrong-answer, reached by a
different mechanism: not a recomputing fallback manufacturing a value, but **store confusion
returning a genuine value from the wrong place.**

The generalisation worth carrying: *a value that is correct for some other input is harder to
doubt than a value that is obviously broken*, and the two are indistinguishable without checking
what the answer should have been. Consistency between two outputs is not evidence — here it was
the defect.

#### The sweep this suggests

**Node-store affiliation deserves its own audit.** The rule — *a node must live in the store the
evaluation consults* — is invisible at the type level, because both sides are `XdmNode`. Nothing
in a signature, a cast or a compiler check distinguishes a node from the right store from one
that belongs to another, and getting it wrong is silent rather than loud. There may be other
cross-store parses.

That places it alongside #53 and #67 as a property the type system cannot express and no test
happens to assert — which is the recurring shape of the worst defects in this register.

#### Its mirror image

#69, #71 and #72 catalogue **tests that never tested** — hollow passes where the suite exercised
nothing. This is the same failure from the other side: **code that never ran.** Together they say
the same thing about the relationship between a test suite and an implementation. A green suite
can mean the tests are hollow, or that the code behind a broken gate was never reached, and
neither leaves a mark in the score.

### 74. A streamed shallow-copy nested the source's siblings — and the corpus was ready to certify the half-fix (2026-09-12)

parsers2, xslt #60. The first *structural* streaming defect of this track rather than an error
code, and user-facing in the plainest possible way.

Under a streamable mode, a template that copies its element and applies templates to its children
turned siblings into a nest:

```
source:    <book><bktlong>long</bktlong><bktshort>short</bktshort></book>
streamed:  <book><bktlong><bktshort></bktshort></bktlong>     … and <book> never closed
expected:  <book><bktlong/><bktshort/></book>
```

**Not subtly wrong — malformed.** Anyone streaming a document with a shallow-skip mode and a copy
template has been getting output that is not well-formed XML.

**Cause.** The dispatch helper materialises an element's *whole subtree* and leaves the reader on
its `EndElement`. The `apply-templates` in the body did not know that and kept driving the
reader, so the first thing it read was the element's **next sibling**, processed as a child.

#### The part worth keeping: how the second half was caught

The first fix made the body walk the materialised children, and **the corpus said +2 with no
losses.** A clean A/B on a fix that was still wrong.

What caught it was a control parsers2 had written by hand — `<a><b><c/></b><b/></a>`, asserting
the fix **does not flatten a real hierarchy**. It failed at depth 3 with `<c>` left open:
`xsl:copy` and the built-in shallow-copy defer their closing tag to the streaming loop's
`EndElement`, and for an already-materialised element that event has been consumed, so nothing
ever popped the deferred close.

> The corpus was ready to certify a fix that moved the defect **one level deeper** — +2, zero
> losses, and still broken for any input nested one step further than the test data happens to
> reach.

#### Two lessons, both generalisable

**1. Write down what the fix must NOT do.** In parsers2's words, the control exists because they
wrote down what the fix must not do, not what it must do. A test asserting the intended outcome
passes as soon as the intended case works. A test asserting the *invariant that must survive* —
here, "a genuine hierarchy stays nested" — keeps holding the fix to account after it stops being
new. Negative controls of this kind are the cheapest defence against a fix that relocates a
defect instead of removing it.

**2. Depth bias — a new form of the sample problem.** `mode-1418`'s fixture is two levels deep;
nothing in the corpus exercises three under that shape. So the sample was biased toward the
**depth** the test data happens to use, exactly as #70's sample is biased toward the detectable
half of a rule. This is the same blind spot in a new dimension.

**Audit question, alongside the three in #72:**

> For a fix that changes a recursive or nested path, **does a test exist at a depth greater than
> the one that motivated it?** A depth-2 fix validated only by depth-2 data is indistinguishable
> from a correct one.

#### It recurs per CALL SITE — a generalisation of #40 (2026-09-12)

The same defect reappeared at a **third dispatch site** (xslt, striding descent): the driver
materialises the whole element and leaves the reader on its end tag, the built-in shallow-copy
was not told, and a matched `CATEGORIES` with three `CATEGORY` children emitted
`<CATEGORIES DESC="…"></CATEGORIES>` and ran no template for any child. **Silent rather than
malformed this time** — the output is well-formed and simply missing everything inside, which is
the harder of the two to notice.

Of the **four** places that materialise-and-dispatch: two were wrong, one (`ControlFlow`) had
solved it independently by suspending streaming for the body, and the second is now fixed.

That is #40's asymmetric-pair shape scaled up: not two implementations where one has the fix, but
**an invariant that must hold at every call site with no shared helper enforcing it.** One site
solving it independently is the tell — it proves the problem is real *and* that the solution was
never generalised. Same family as the node-store affiliation sweep in #73: a property the type
system cannot express, no single place to enforce it, and silent when violated.

**Worth a deliberate pass rather than a fourth encounter:** enumerate the materialise-and-dispatch
sites and give them a shared helper, or at minimum a shared assertion.

#### Footnote on measurement

The partial run reported +2; the full sweep came back **+5**, with `mode-1424` and `mode-1426`
also coming good. Worth remembering in both directions — a partial run can understate as easily
as overstate, and the number to report is the one from the full sweep.

### 75. `warning-on-no-match` was parsed, validated, and then dropped (2026-09-12)

parsers2, xslt #61. **The fifth variety of #69's family, and the best-disguised.**

The parser read `xsl:mode warning-on-no-match`, checked it for `XTSE0020`, and **threw the value
away**. Nothing carried it, nothing consulted it, nothing warned.

From outside it looked implemented. A stylesheet using the attribute compiles clean; an invalid
value is correctly rejected. That is the disguise, and it is a good one:

> **Validation without effect proves the code *read* the attribute — which is exactly the
> evidence a reviewer would look for.**

A validated-and-dropped attribute passes every **negative** test, because bad values really are
rejected, and has no **positive** test, because nothing observable happens on the good path.
There is no output to assert against, so the absence of a test looks like an oversight rather
than a gap.

#### Where it sits in the family

| # | mechanism | the silence |
|---|---|---|
| 69 | an invocation parameter the runner never supplied | the engine's degenerate answer matched the expected one |
| 71 | an attribute value the parser did not **recognise** | `streamable="true"` read as not streamable |
| 72 | test files the harness never opened | an entire feature area unscored |
| 73 | code behind a permanently-failing operation | never reached, so never tested |
| **75** | an attribute the parser **recognised and discarded** | validated, so it looks implemented |

#71 and #75 are worth holding side by side: one is a value the parser did not recognise, the
other a value it recognised and dropped. **Different mechanism, identical silence.**

**Audit question, the fifth in this family:** *for each attribute the parser validates, what
reads the value afterwards?* A validation with no consumer is the tell.

#### The corpus could not have caught it either — from the assertion side

`assert-warning` returned **false unconditionally** in the harness, with an honest comment saying
warnings were not collected. So four W3C cases were **unfailable and unpassable**: they could not
pass however correct the engine became.

That is #69's shape arriving from a third direction. #69 was a missing *invocation* parameter;
#72 was a missing *file*; this is a missing *assertion capability*. A test whose assertion always
returns false is not a failing test — it is a test that has been removed from the suite while
still being counted in it.

parsers2 implemented warning collection in the same PR and **kept it strict**: a test asking for
a warning that gets none still fails. That matters — the easy version of this fix is to make
`assert-warning` return true, which would have converted four unpassable tests into four hollow
passes and moved the problem rather than solving it.

#### Why the feature is worth more than its four cases

The built-in template rule is **silent by design**. A node no template matched produces text
where the author expected markup, or nothing at all, with no indication that a rule was missing —
one of the genuinely hard things to debug in XSLT, because the output is well-formed and simply
incomplete.

`warning-on-no-match` is the spec's answer to exactly that, and we were ignoring it. **This is a
debugging capability, not a conformance point** — the four cases are the smallest part of its
value.

### 76. The A/B procedure has an unchecked precondition, and violating it looks like a clean result (2026-09-12)

Caught by parsers2 on themselves, mid-run, and killed before it produced a number. Registered
because **the failure mode of our measurement is silence, not noise** — which is the same
sentence as every other entry in this register, turned on the method rather than the engine.

The habit all session has been `git stash push -- src` to produce the baseline side of a
same-checkout A/B. That works **only while the change is uncommitted.** This time the change was
already committed, so the stash moved nothing, **both sides measured identical code**, and the
run was on course to report a flat, zero-loss A/B — **indistinguishable from a correct result for
a change that genuinely costs nothing.**

> The procedure has a precondition that nothing checks, and violating it produces a
> **passing-looking** result rather than an error.

Every A/B this session has been reported as evidence. An A/B that silently compared a tree with
itself would have carried exactly the same authority as the real ones.

#### Suggested guard

Before measuring, **assert the two sides actually differ**. The cheapest form:

```sh
git diff --quiet HEAD origin/main -- src && { echo "A/B ABORT: baseline and change are identical"; exit 2; }
```

Fail closed, as everything else here does. A comparison that cannot distinguish its two arms is
not a weak measurement, it is not a measurement — and it costs one command to rule out.

`git checkout origin/main -- src` is the correct baseline mechanism regardless of commit state,
and is what parsers2 redid the run with.

#### A second unchecked assumption in the same class: which branch am I on?

Added 2026-09-12. A `SESU0013` commit landed on an **already-merged branch** rather than a fresh
one, because the branch was never created. It surfaced only on push — *refspec does not match
any* — and was harmless because that branch was already merged.

**The failure mode if it had not been:** the commit sits quietly on the wrong branch and ships
inside an unrelated PR, with nothing at commit time to say so.

parsers2's proposal, which I agree with: the sweep wrapper should assert **both** preconditions
side by side, since they are the same class of unchecked assumption:

1. the baseline arm differs from the change arm (above), and
2. **"am I on a branch that is neither `main` nor already merged?"**

Neither costs anything, and both fail silently today.

**A third, added 2026-09-12 (#78): do not switch branches while a sweep is running.** The sweep
checks `base` out into the working tree and restores `HEAD`'s version when it finishes. If `HEAD`
moved in between, it restores the wrong content and says nothing. Hit and survived on timing
alone.

**A fourth, added 2026-09-13: the branch must not be BEHIND base.** parsers2's first sweep of the
`fn:serialize` branch was invalid because the branch predated xslt #72 — so the **base arm had a
fix the change arm lacked**, and the two arms differed by *two* changes rather than one.

It reported a **better** result than the truth: 0 losses / 3 wins against a real 0 / 1. That is
the dangerous direction. A stale branch **silently attributes main's own progress to your
change**, and the resulting number is flattering, which is the condition under which a
measurement is least likely to be questioned.

`git rev-list --count HEAD..origin/main` catches it; the sweep script currently cannot. parsers2
rebased and re-measured, and xslt #73 carries the rebased numbers and says so.

**Why this one belongs in the script rather than in a register entry**, in parsers2's sharper
framing: the check is cheap and they *still* did not make it, because **a stale branch gives you
no reason to look.** The other three preconditions are things you would suspect from the result —
a flat A/B, a refspec error, restored content that looks wrong. This one you can only catch by
asking a question the result never prompts. A precondition that produces no symptom cannot be
enforced by attentiveness, however disciplined; it has to be enforced by the tool.

#### A long sweep opens a window the guard cannot see into (2026-09-13)

Guard 3 checks whether the branch is behind base **at the start**. parsers2's branch fell **6
commits behind main while the sweep was running**, so the guard could not have caught it.

Harmless in that instance — all six were BUGS.md edits, and they verified it with
`git diff --name-only` rather than assuming, and said so on the PR. But the general case is real:

> **The honest check is at PR time, not sweep time.** A ten-minute sweep on a moving main has a
> ten-minute blind spot by construction.

**Deliberately not fixed with a second guard.** The sweep is long and main moves; a guard that
re-checks at the end would fail routinely on changes that do not affect the numbers, and a guard
that fires routinely is one that gets ignored (#40). The remedy is a habit belonging in this
section: **confirm what landed during the sweep before believing its numbers** — `git diff
--name-only base..main` answers it in one command.

Same shape as everything else here: **the tooling is sound at the moment it runs, and the risk
lives in what you conclude afterwards.**

#### The guards protect the sweep — not the ad-hoc check you run to interpret it

Added 2026-09-13, after parsers2 made guard 1's exact mistake **by hand**: running
`git stash push -- src` for a quick base comparison while the change was already **committed**, so
the stash moved nothing and both arms of the manual check ran identical code. Two lines reported
to themselves as base-vs-change before noticing they were byte-identical.

Guard 1 exists in the script for precisely this. **They stepped outside the script to do it
manually** — which is the normal thing to do when you want one quick answer rather than a full
sweep.

> The guards protect the **formal** measurement. The dangerous one is the **informal** check you
> run to interpret a result you already half-believe.

That is the worse position of the two: a sweep is run before you have a conclusion, an ad-hoc
comparison *under* one, and a result that agrees with what you expect is the least likely to be
questioned. Tooling cannot reach that check by definition — it is ad-hoc — so the only defence is
the habit: **`git checkout origin/main -- src`, never `stash`**, which is what produced the real
numbers above.

**That habit needs a second half, added 2026-09-13 after parsers2 hit it twice in one day.**
`git checkout origin/main -- src` is the right tool for *taking* the comparison, and
`git checkout HEAD -- src` to undo it **destroys the uncommitted working change** — and the
working change is the entire point of the exercise. Caught within a minute both times, but it is
a loss with no recovery, unlike every other trap in this list.

> **Commit before comparing.** Checkout is the right tool for the comparison and the wrong tool
> for the restore, and the restore is where the change lives.

Three distinct ways to fool yourself while *interpreting* a measurement, all found in one day —
the compound probe (#85), the ad-hoc stash (above), and this — and **none of them catchable by
the sweep guards**, because all three happen outside the sweep.

#### Sequence, not vigilance (2026-09-13) — the rule did not work

parsers2 destroyed their own uncommitted edit with `git checkout <ref> -- src` a **third** time,
**minutes after writing "commit before comparing" to me as the remedy.**

> **Writing the rule down did nothing.**

What worked was **reordering the operations**: commit, *then* measure — so there is no
uncommitted state for the comparison to eat. The hazard is removed rather than guarded against.

That generalises past this one trap, and is the most useful thing in this entry:

> **A rule only binds when it changes the order of operations. A rule that asks you to remember
> something at the dangerous moment is not a control — it is a wish.**

Every guard in this entry that *works* is a precondition the tool checks. Every one that has been
violated repeatedly is a habit someone was asked to maintain. The register has now recorded this
same trap three times with an increasingly emphatic rule attached, and the rule's emphasis made
no difference at all; the sequence change did.

#### The same error one remove deeper: inside the analysis of the logs (2026-09-13)

parsers2 nearly reported "135 passing" **derived by subtraction** — the expect-set minus the
failed-set. That is unsound, because **the logs record failures only.** A skipped case and a
passing case are both simply absent, so subtraction silently counts every skip as a pass.
Confirmed on `accumulator-058`: zero occurrences in any log, pass and skip indistinguishable.

The sound number needed confirming from the **per-chunk totals** that none of the 155 live in
`sandp` and that every other chunk reports real case counts.

This is the *same* error as the section below, one level further out: not a test run that
reported nothing, but an **analysis of the logs** that treated absence as success. The
interpretation layer has now produced three of these in one afternoon — which is why this
section keeps growing while the guarded sweep has produced none.

#### Why this section will keep growing, and why that is correct

I framed that contrast as *"we moved the risk rather than removing it"*. **parsers2 corrected it,
and their version is better:**

> The sweep produced zero interpretation errors partly because **it produces exactly one number
> in one format — there is almost nothing to interpret.** The ad-hoc layer is where all the
> *questions* get asked: which cases moved, why, is that a flicker, does this cluster share a
> cause. **Those questions are the actual work; the sweep just scores it.**

So the risk was never displaced by the tooling. **It was always in the questions, and the sweep
never touched that part.** This section will keep growing however good the tooling gets — and
that is the **correct outcome, not a gap to close.** A tool can make a measurement trustworthy;
it cannot make an inference trustworthy.

#### All of them are the same species: treating an absence as an answer

The three failures of 2026-09-13 look unrelated and are not:

| | the absence | read as |
|---|---|---|
| wrong filter, then 26 skipped | **an absent test run** | "zero failures" |
| the 85-versus-0 error split | **an absent error of the right kind** | "it errored, so this is not silent acceptance" |
| "135 passing" by subtraction | **an absent log line** | "not in the failure log, therefore passed" |

**Every one read as "nothing wrong here."**

That is the register's oldest sentence — *a thing that did not happen looks exactly like a thing
that happened and found nothing* (#28, #44, #54, #69) — arriving in the **interpretation layer**
rather than in the engine or the harness. The remedy is the same one that works everywhere else
in this file: **make the absence visible.** Check the count, not the failures. Check the error's
identity, not its presence. Confirm from totals, never by subtraction.

#### FOURTH occurrence — and it undermines "sequence, not vigilance" (2026-09-13)

The change was destroyed again with `git checkout HEAD -- src`, this time **after writing the
tests and jumping straight to the base comparison.**

We recorded *sequence, not vigilance* as the fix, and it is still the right idea — but it has a
hole:

> **The sequence only binds if you actually enter it.** parsers2 was not measuring; they were
> checking whether new tests failed on base — **which does not feel like a measurement at all.**

So the trap has a **second entrance we did not close**. It is not only the sweep that needs the
commit-first order, it is **any base comparison — including the trivial-feeling one you run while
still writing the change.** That entrance is *more* dangerous precisely because it is not
recognised as the start of a procedure, so no procedure gets invoked.

This is the limit of process design as a remedy: a sequence protects the path it is attached to,
and the same hazard has other paths reaching it.

**What actually saved the work, both times, was not a habit:**

> **When a change is more than a couple of lines, apply it via a script you can re-run.** The
> recovery cost then rounds to zero regardless of the habit failing.

That is the practical mitigation, and it is of a different kind from everything else in this
section — it does not prevent the mistake, it **makes the mistake cheap.** Given four occurrences
in two days against a rule that has been written down twice, cheapening the failure is a better
investment than preventing it.

#### Zero failures and zero tests look identical in a grep (2026-09-13)

Two of parsers2's first three attempts to measure #87 **reported clean results from tests that
never ran** — first a wrong filter (`Suite=XSLT&Group=strm2`, where `strm2` is a *class* filter,
not a Group), then a run where all 26 tests were SKIPPED. **Both printed as zero failures.**

> Always check the test **count**, not just the failure count. A suite that ran nothing is
> indistinguishable from a suite that passed everything, by exactly the grep you would use to
> check.

This is #54's "a set that did not run looks exactly like a set that ran and found nothing", at
the level of the ad-hoc command rather than the gate. `./scripts/conformance.sh strm2` is the
supported path and would have avoided both.

All four preconditions share a shape worth naming: **the sweep mutates the working tree and
assumes nothing else does.** It is a stateful operation wearing the interface of a measurement. Guards 1 and 2
check preconditions at the start; this one is a precondition that must hold *throughout*, which
is a harder thing to assert and a good argument for the sweep taking its own worktree rather than
asking the operator to hold still.

#### Why this belongs in the register rather than in a habit

Three of this session's most valuable findings (#69, #71, #73) were counterfactuals of the form
*"the clean A/B would have certified something wrong"*. This is the same statement about the
tooling that produces those A/Bs. **The measurement apparatus is subject to the pattern it keeps
detecting**, and there is no reason to expect it to be the one exception.

### 77. OPEN (policy) — 919 `sandp` cases are dependency-skipped, and the streamability analyser has no corpus evidence at all (2026-09-12)

Found and measured by parsers2, **deliberately not acted on**: the sequencing and denominator
implications belong with Lucas, alongside #57 and the two policy calls in #72.

The `sandp` group's **919 test cases** carry a dependency:

```xml
<sweep_and_posture satisfied="true" value="supports-sweep-and-posture-assessments"/>
```

Our runner reports *"all cases filtered by dependencies"* — **every one skipped.** That is
**correct behaviour today**: it is an optional feature and we do not claim it.

#### But we have the thing it tests

The engine **has a streamability classifier with postures and sweeps.** Exposing that capability
and implementing `assert-posture-and-sweep` would make 919 cases measurable — **~9% of the XSLT
corpus by case count, the single largest coverage change available to us**, at an unknown pass
rate.

The part that matters more than the percentage:

> **The streamability analyser currently has no corpus evidence whatsoever.** A major engine
> component, responsible for decisions that produce silently wrong output when they are wrong
> (#63, #71, #74, xslt #62), is validated by nothing in the W3C suite.

Three of this session's malformed-output and silent-wrong-answer defects were in or adjacent to
streaming. The one body of tests that would exercise the analyser directly is the one we skip.

#### RETRACTED 2026-09-13 — the premise was false, and #85 is NOT blocked on this

**The claim below is wrong and the escalation built on it has been withdrawn.** parsers2
measured it:

| XTSE3430 cases (non-streamable must be REJECTED) | count |
|---|---|
| total in the corpus | **155** |
| in `sandp` | **0** |
| in chunks that actually execute | **155** — 135 passing, 20 failing |

**Not one** `XTSE3430` case is in `sandp`. They live in `accumulator`, `mode`, `sf-current`,
`si-map`, `si-assert`, `square-array`, `su-shallow-descent`, `sx-fcall` and `error-34xx` — all in
chunks that run. **The streamability analyser does not have "zero corpus coverage"; it has 155
executed cases.**

So the risk picture for tightening it is close to the opposite of what I argued:

- **Under-rejection** (the defect in #85) has **20 failing cases as direct targets**, with 135
  passing ones that must stay passing — a real before/after signal.
- **Over-rejection** (the direction worth fearing) would surface **immediately** in the ~2373
  executed streaming cases (`strm1` 712/729, `strm2` 710/749, `strm3` 884/895): a valid streamable
  stylesheet newly rejected fails there.

**`sandp` remains worth enabling on its own merits** — breadth, and the analyser's posture/sweep
assertions specifically. It is **not a prerequisite for #85**, and Lucas should not be asked to
decide it as one.

**How I got it wrong:** I inferred "no corpus evidence" from "the 919 cases that would exercise
it are skipped", without checking whether any *other* cases exercise it. That is the same move as
the 45-vs-43 error — an impact claim derived from one population without measuring the others.
Third time this session, and the first where it reached a recommendation to Lucas.

#### Superseded — the original escalation, kept for the record

#85 found that under a streamable mode, **non-streamable expressions answer instead of being
rejected** — `last()` returns 1, `preceding::*` returns 0, and a stylesheet the spec says must be
diagnosed runs quietly producing plausible output.

The fix direction is **"reject more stylesheets"**, which can only lose conformance cases if the
analyser over-rejects. Making that change to a component with **no corpus evidence whatsoever**
is the worst available combination.

**The 919 `sandp` cases are the evidence you would want before that change, not after.** That is
a considerably stronger argument for enabling them than ~9% coverage, and it converts this entry
from a denominator question into a sequencing dependency.

#### Why it is a policy call, not engineering

- It moves the denominator by ~919 cases, on top of the #57 mode decision and the `decl/expose`
  set in #72. Three denominator changes at once needs sequencing, not three separate surprises.
- The pass rate is **unknown**. It could be an unflattering number, and the decision to look must
  be made *before* the number is known, or it is not a decision.
- Claiming `supports-sweep-and-posture-assessments` is a **public statement about the product**,
  not an internal test-harness change.

#### The remaining unconditionally-false assertions

`assert-warning` was fixed in xslt #61. What still returns false unconditionally:

| assertion | cases | note |
|---|---|---|
| `assert-posture-and-sweep` | 919 | dependency-skipped anyway, so currently invisible either way |
| `assert-serialization-error` | 43 | **not a hollow cluster — see the correction below** |

#### CORRECTION (2026-09-12) — I got the serialization-error cluster wrong

I wrote above that these were "a live hollow cluster… they run, they assert, and the assertion
cannot succeed however correct the engine is." **That is false, and it was my error, not a
reported one.** parsers2 measured before starting work on it:

- There are **43**, not 45.
- **42 of them already pass.**

Nearly all are `<any-of>` carrying an `<error code="…"/>` alternative alongside the
`assert-serialization-error`, and the engine raises that alternative. `output-0197` is typical —
`SEPM0016` **or** `XTSE0020`, and we raise `XTSE0020` statically at parse time. The harness also
already matches `assert-serialization-error` codes against thrown exceptions
(`MatchesExpectedError` handles both kinds), so this assertion is **not** in the category
`assert-warning` was: that one genuinely returned false unconditionally, this one does not.

The real work was **one case** — `output-0194`, `method="html" version="0.0"`, expecting
`SESU0013` with no alternative. A missing engine check, not a harness gap. Fixed in xslt #63.

**The lesson, which belongs on the audit list rather than in the engine:**

> **An assertion kind returning false in the harness does not mean the tests using it fail.**
> Most carry alternatives, and `any-of` needs only one to be satisfied. **Counting occurrences of
> an assertion kind overstates the work by however many are satisfied elsewhere** — here by a
> factor of 43.

I reached "45 cases cannot pass" by counting occurrences of the assertion and inferring impact,
without measuring. That is precisely the move this register spends its time cataloguing: a number
that looks like evidence, produced by reasoning rather than by running. The `sandp` figure beside
it **was** measured — 919 genuinely skipped, verified against the runner's own "all cases
filtered by dependencies" line — which is the only reason it survives this correction.

### 78. The runner was loading the wrong file — eight cases failed while testing nothing (2026-09-12)

parsers2, xslt #65. **The sixth variety in #69's family, and the one that inverts it.**

Eight `decl/package` cases failed with `XTDE0555` — *"no matching template … on-no-match='fail'"*
— for stylesheets that declare `on-no-match="text-only-copy"`. The tests declare two files:

```xml
<package    file="package-015.xsl"        role="principal"/>
<stylesheet file="package-015-import.xsl" role="secondary"/>
```

The runner's selection preferred **any `<stylesheet>` element outright**, so it loaded the
**secondary import** as the principal. That import declares `<xsl:mode on-no-match="fail"/>`.

**The engine was right the whole time** — the same transform through the CLI produces the
expected output. Eight cases spent their existence testing the wrong stylesheet and reporting the
engine's correctness as an engine defect.

#### The symmetry, and the uncomfortable half of it

#69, #71, #72 and #75 are tests that **passed** while testing nothing. This is tests that
**failed** while testing nothing — the same hollowness with the opposite sign.

> **A red result is no more self-verifying than a green one** — and red results likely get *less*
> scrutiny, precisely because a failure feels like information.

That is worth sitting with. A green result invites the question "did it really check?"; this
register is four entries deep in that question. A red result arrives already looking like
evidence: something is wrong, here is the code, someone should fix it. **The one shape nobody
audits is a failure they believe.**

Practical consequence for the remaining failure list: **a cluster of failures sharing one error
code is as likely to be one harness defect as one engine defect**, and the cheap first test is to
run the same transform through the CLI. Eight cases here, and the CLI disagreed with the harness
immediately.

#### Two notes on the tooling from the same stretch

**The `ab-sweep` script found its own flaw on first use.** Pointed at this very harness fix, it
aborted with *"src is identical to base"* — correct by its own rule and useless, because a
harness change moves the numbers exactly as an engine change does. It now swaps `tests/` as well
as `src/` (merged in xslt #64).

The diagnosis is the sharper part: **guard 1 was right that the arms were identical, and wrong
about which arms mattered.** That is a proxy predicate (#67) — `src` differing standing in for
*the measurement is meaningful* — written into new tooling on the same day that pattern was being
catalogued. Proxies are not a thing other people write.

**A third unchecked precondition (see #76): do not switch branches while a sweep is running.**
The sweep checks `base` out into the working tree and restores `HEAD`'s version afterwards. If
`HEAD` has moved meanwhile, it restores the **wrong content**, silently. parsers2 switched
branches mid-run and was saved only by timing. Same class as the other two, same silent failure
mode.

### 79. FIXED 2026-09-13 (xslt #76) — a streamed element had no ancestors at all

**Every particular of this entry as originally written was wrong.** parsers2 wrote it, corrected
it, and asked that the correction land as method rather than apology. Kept in full, because how
it aged is worth more than the defect.

| as recorded | actually |
|---|---|
| "returns only its parent" | returns **nothing** — no parent, no document node, `count(..) = 0` |
| "innermost-first where XPath requires document order" | order was never the issue; there was no chain to order |
| cause: the striding driver never calls `SynthesizeAncestorChain` | the striding driver **already links its ancestors** (#68 fixed that), and the subscription path has its own chain — the break was in a **third** path |
| "`si-apply-templates-001` still fails" | it passes on main today |

#### The real cause

The streaming processor sets the parent on its node **context**, and `MaterializeElement` never
passes it to the element it builds from that context. Only the root got one, assigned explicitly
on the next line.

The comment at that site says deeper elements *"keep their real element parent from
ancestorStack"* — **which is why it looked handled.** The assignment it names is real; it simply
lands on the wrong object. A comment describing a mechanism that exists, attached to a path where
it does not apply, is worse than no comment: it answers the question a reader would otherwise ask.

**The good twin was not in another component.** It is the **text-node path in the same loop, two
hundred lines below**, which has always taken its parent id off that same stack — with a comment
explaining what a parentless node does to pattern matching. Elements never did.

#### Why the entry decayed — the method lesson

> **I recorded a start point instead of a diagnosis, and a start point has a shelf life.**

"The striding driver doesn't call the good twin" was true when written and false a day later,
because **#68 — the same author's own PR — fixed exactly that**, and the entry it invalidated was
never revisited. **An entry naming a suspected SITE decays silently when the site changes; one
naming an observed SYMPTOM does not.**

The symptom was wrong too, and that half is more fixable: *"only its parent"* was recorded from
**reasoning about the code rather than running it**. Two minutes with a three-element document
would have said `count(..) = 0`. This is the register's own recurring finding — a number produced
by reasoning wears the clothes of a number produced by measurement — turned on the register
itself.

Hence the convention now stated at the top of this file.

#### Measurement, honestly

**No conformance movement.** base 350 / change 349, LOST [] WON [`call-template-1002`, a known
flicker]; streaming sets identical in both arms at 80 failures each. **Zero losses, zero real
wins** — the suite never asks a streamed element for its ancestors. Same shape as #71 and #82: a
real defect the corpus cannot see.

Carried instead by `StreamingAncestorAxisTests`: 8 tests, **7 fail on main**. Seven compare
streamed against unstreamed for the same document, so the two cannot drift apart unnoticed, and
one pins document order **literally**, so an innermost-first regression cannot hide behind an
equality check.

Guard 3 from #76 earned itself here: it caught that the branch had fallen one commit behind main
(#75, comment-only) **before** the measurement ran. First thing it flagged, and it was right to.

### 80. PATTERN — two walks that disagree, and the correct one hides the broken one (2026-09-13)

Named by parsers2 from a Martin Honnen report (xslt #71). A refinement of #40: not two
implementations where one has a fix the other lacks, but **two implementations where the correct
one handles most traffic, so the broken one is reached only by an unusual path.**

#### The instance

The **static** namespace-id walk (`DefaultXsltExecutionContext.Namespaces.cs`) descended into
every expression shape **except** `InlineFunctionExpression` and `DynamicFunctionCallExpression`.
An unresolved prefixed name test matches only no-namespace nodes, so:

```
function($m) { $m/xdm:item }   →   the empty sequence
```

No error, no warning. Measured: `outside=1 inline=0 fold=0 inline-unprefixed=1 for-each=1` — the
same name test works everywhere except inside an inline function.

**The runtime namespace walk, in the same file, already handled both shapes.** That is why this
shipped: every ordinary path went through the correct walk, and only expressions routed through
the static one hit the gap.

#### The shape

> A defect survives when **a second implementation of the same rule is correct**, and only one
> code path reaches the broken one.

The correct twin is not merely a missed opportunity to share code — it is **actively
concealing**. It keeps the observable behaviour right for the common cases, which is exactly the
evidence anyone would use to conclude the rule is implemented. The narrower the path reaching
the broken walk, the longer it survives, and the more confident everyone is that the area is
sound.

**Find out which one RUNS before reading either.** From #79: the pair can be **twenty lines
apart in the same file**, not across components — and going to the place the register pointed
cost two wrong guesses at the dispatch path before instrumenting. **Instrumenting first would
have been faster than reading**; the stack frame named the caller immediately. When a rule has
two implementations, the cheap first move is to observe which one executes, not to read both and
reason about which should.

**Audit action:** grep for other paired static/runtime walks over the same structure. Anywhere
one rule is implemented twice — once at analysis time and once at execution time — the two must
agree on the *shape list*, and nothing enforces that.

#### The corpus, again

A/B across all chunks: **base 356 / change 356, no per-set differences.** Zero losses *and* zero
wins — **the suite has no case of this shape.** Found by an external user, not by us, and it
would not have been found by running more tests. Add it to #70's tally of the corpus's blind
spots.

### 81. PATTERN — a cheap marker type leaks (2026-09-13)

Named by parsers2 from a Martin Honnen report. **This is the second time `TextNodeItem` has
produced a defect of exactly this kind**, which is what makes it a pattern rather than a bug.

The engine represents a text node **two ways**: `XdmText`, a real node in a store, and
`TextNodeItem`, a bare record holding the string — returned by an `xsl:function` whose body is
`xsl:value-of`. The second exists for one narrow purpose: distinguishing a text node from a plain
string in a sequence.

`fn:deep-equal` knew only the first. Both arms were wrong, in opposite directions:

| comparison | arm | answer | correct |
|---|---|---|---|
| two text nodes, same content | node arm saw *"one is a node, the other is not"* | `false` | `true` |
| text node vs plain string | atomizing arm | `true` | `false` |

Both silent.

#### The shape

> **An internal representation introduced for one narrow purpose is not known to every consumer
> that must treat it as the thing it stands for.**

The marker is cheap to introduce precisely because it avoids the cost of a real node — no
identity, no parent, no store. That saving is what makes it attractive, and every consumer doing
`x is XdmNode` is a place where the saving becomes a defect. Nothing in the type system connects
them: `TextNodeItem` is not an `XdmNode` **by design**, which is the point of it and also the
bug.

#### It has form

This is the same marker that previously **matched neither `text()` nor `node()` in patterns**
(`TextNodeItemMatchingTests`), and it appears in #53's instance table for a third failure —
the function-result assembly treating it as a duplicate of `_output` content based on which path
wrote it. **Three distinct defects, one representation.**

**Predicted sites, not yet swept:** anything doing `is XdmNode`, and anything branching on node
versus atomic. parsers2 has not run that sweep. It belongs beside the node-store affiliation
sweep in #73 — both are properties the type system cannot express, and both are silent when
violated.

### 82. FIXED 2026-09-13 (xslt #73) — `fn:serialize` emitted `xmlns:p=""` and its output did not reparse

Found by parsers2 while working Martin Honnen's reports. **Not reported by Martin, and not yet
fixed** — found by looking around the area rather than at the complaint.

`PhoenixmlDb.XQuery`'s `SerializeFunction` resolves namespace URIs **only when the provider is an
`XdmDocumentStore`**. The XSLT engine's store is an `XdmInMemoryStore`, so the URI comes back
`""` and any prefixed namespace serializes as `xmlns:p=""`.

**The output is not well-formed and does not reparse.** A user calling `fn:serialize` on a
document with any prefixed namespace gets a string that no XML parser will accept back.

#### Scope, measured rather than assumed

Confined to the `fn:serialize` **function**. `xsl:result-document` file output is **correct** —
parsers2 ran Martin's exact demo command: exit 0, well-formed on disk. So the defect does not
touch the ordinary output path, which is why nobody hit it until someone serialized to a string.

#### The fix is XSLT-side

The natural home is `PhoenixmlDb.XQuery`, but **that tree is parked at `v1.7.0` under the
XSLT-first order**, so the remedy is an XSLT-side override. Being scoped now.

Worth noting as a small instance of #40: the same operation is correct through one path
(`xsl:result-document`) and wrong through another (`fn:serialize`), and the correct one is the
common one — which is #80's concealment effect in miniature.

### 84. OPEN — a function-produced text node fails every axis step, and the error names an internal type (2026-09-13)

Found by parsers2 in the #81 sweep. **Not started.** Two separable defects, and the cheap half
should not wait for the expensive one.

A text node produced by `xsl:function` raises **`XPTY0020` on any axis step**, while `root()` and
`path()` return empty. parsers2 compared **19 operations** across the two text-node
representations; only those differ, so the leak is narrow but real.

#### Defect A — the message names an internal type to the user

> `got item of type TextNodeItem`

`TextNodeItem` is an engine-internal representation. A user has no way to know what it is, cannot
find it in any specification, and cannot act on it. The message tells them the implementation's
private business and nothing about their stylesheet.

**This is #21's "improve the error first" exactly, and it is cheap and independent.** Whatever
happens to the underlying asymmetry, no user-facing diagnostic should name this type. Worth
doing on its own rather than riding along with the harder fix.

#### Defect B — the asymmetry underneath

`xsl:variable` **materialises** its text result into a store-backed node; `xsl:function` does
**not**. So the same text content is a real node through one construct and a bare marker through
the other, and every consumer that needs a node fails on the second.

That is #40's shape again — one of a pair has something its twin lacks — and it is the root cause
that #81's sweep predicted would exist at other sites.

#### Sequencing — my first recommendation was wrong

I advised taking Defect A immediately as a small independent fix. **Defect A is an XQuery
change** — the message is raised in `AxisNavigationOperator.cs:43/68`, `PhoenixmlDb.XQuery`,
parked at `v1.7.0` under the XSLT-first order. There is no XSLT-side site for it. I recommended
work that cannot be done, against a hold I had been relaying to Lucas all week.

Two things make the hold the right answer rather than a problem to escalate:

- **The message only appears via Defect B.** Fixing B removes it in practice, so the
  user-hostile diagnostic is not independently reachable.
- **Only the second half of the message leaks.** The first half — *"An axis step was used when
  the context item is not a node"* — is accurate and actionable on its own.

So: **wait for the XQuery track**, and if anyone thinks the diagnostic argument outranks the
hold, that is Lucas's call to make, not one to settle between sessions.

#### Defect B is a design question, not "copy the twin"

parsers2 diagnosed it properly and the answer is more interesting than the asymmetry suggested.
The failure is specific to **`as="item()*"` — the default**:

| declared type | axis step | `root()` |
|---|---|---|
| `as="item()*"` | **`XPTY0020`** | `0` |
| `as="text()*"` | works | `1` |
| `as="node()*"` | works | `1` |

**The materialisation already exists on the function path and works.** It is deliberately
restricted to Text/Node, and the comment gives the reason: that block converts bare strings too,
and under `item()` **a string must stay a string.**

The narrow fix — materialise only `TextNodeItem`, never bare strings — respects that reason
exactly, and **still did not fire.** The block is additionally gated on the text output buffer
being empty, and under `item()` the body's text is written to **both** the accumulator and the
output buffer. That dual channel exists so the assembly can restore source order between text and
elements — **and it recognises the duplicate BY its `TextNodeItem` type.**

> **The marker type that causes this defect is load-bearing for the mechanism that would have to
> be changed to remove it.**

Materialising early emits the same text twice; the comment records that having been hit before
(`xsl:text` A + B coming back as `AB, A, B`). So the fix must either materialise *after* the
assembly, or give the assembly another way to spot the duplicate — a real design question in the
sequence-assembly path, needing its own A/B. parsers2 backed the experiment out rather than force
it, which was right.

Worth noting for #81: that entry predicted more sites where the marker leaks. This is the
opposite finding and just as useful — a site where the marker is **depended upon**. A sweep to
remove it will meet this, and should expect to.

#### 2026-09-13 — the fix works, costs three cases, and DOES NOT SHIP

Branch `fix/function-text-node-materialize` is pushed **with no PR**, for whoever picks it up.

```
base failures 348   change failures 353
insn.log  LOST [call-template-1002, call-template-1003]  WON []   <- known flickers
misc.log  LOST [seqtor-029, seqtor-034, seqtor-035]      WON []   <- real
```

`seqtor-029`, change against `origin/main`:

```
BASE    startendABCDEFGHIJKLMNOPQRSTUVWXYZ...      correct
CHANGE  startend A B C D E F G H I J K L M N ...   spaces inserted
```

**A third dependent — and it is the one the marker was invented for.** The sequence-serialization
path applies the top-level item separator between adjacent **nodes** and not between adjacent
**markers**. Materialise the markers into real text nodes and adjacent text — which XSLT 3.0
§5.7.2 requires to **merge** — comes out space-separated. `seqtor-029` is 61 levels of
non-tail-recursive function building exactly that, and it asserts the concatenation.

So the real work is **two changes**: materialise the marker, *and* teach sequence separation that
adjacent text nodes merge. **The second is the risky half, and it is not the half anyone sets out
to make.** That is the fourth change refused rather than merged on this track (see #69, #70, #71).

#### The negative results, recorded because they are the expensive part

**None of the obvious minimal cases reproduce it.** All three agree between the arms:

| probe | result |
|---|---|
| two adjacent real text nodes from variables | `[AB]` in both |
| function returning two atomics | `[A B]` in both |
| function returning two TVT text nodes | `[AB]` in both |

Only the recursive shape diverges, and the trigger was **not** pinned down — whatever it is needs
the nesting. Reproduce with `seqtor-029` itself.

*"I tried the obvious minimal cases and they pass"* is an hour that should be spent once. **A
register entry is worth more when it records what did NOT reproduce**, because the next person's
first instinct is exactly the instinct that already failed.

### 83. PATTERN — a capability gated on a concrete type, degrading instead of erroring (2026-09-13)

Named by parsers2 from the `fn:serialize` fix (#82). **The third defect from a single type test.**

```csharp
if (provider is XdmDocumentStore store) { …resolve namespace URIs… }
// else: return ""
```

Every other store silently gets a wrong answer. The XSLT engine's store is an
`XdmInMemoryStore`, so prefixed namespaces serialized as `xmlns:p=""` and the output would not
reparse (#82).

#### How it differs from #81, and why both are worth naming

| | #81 — a cheap marker type leaks | #83 — a capability gated on a concrete type |
|---|---|---|
| what is wrong | a **value** type consumers fail to recognise | a **capability check** that degrades instead of erroring |
| the defect | `x is XdmNode` is false for something that is a node | `x is XdmDocumentStore` is false for something that can do the job |
| the fallback | treats it as an atomic value | returns a plausible empty answer |

Both are invisible for the same reason: **the fallback returns something plausible.** #81 answers
with a string, #83 answers with `""`. Neither raises.

The distinct lesson here is about the *else* branch. A type test guarding a capability has two
honest options — implement the capability for the other type, or **fail** — and it took the third
option, which is to answer anyway. An `else` that produces a value rather than an error converts
"I cannot do this" into "the answer is nothing", and those are indistinguishable downstream.

**Audit action:** find capability checks written as concrete-type tests, and look at what the
else-branch returns. A branch returning empty, `""`, `null` or a default is the tell. The correct
shape is usually an interface the capable types implement, or an explicit failure.

Third instance from this one test — the earlier two are on
`fn_serialize_adaptive_method_emits_map_with_node_values`, also found via Martin Honnen.

### 85. OPEN — under a streamable mode, non-streamable expressions answer instead of being rejected (2026-09-13)

Found by parsers2 probing while waiting on CI for something else. **Recorded per the convention
at the top of this file: observed symptom and literal output, no suspected site.**

#### Reproduction

Same document, same stylesheet. The only difference is `streamable="yes"` present or absent:

| expression | streamed | unstreamed |
|---|---|---|
| `last()` | **1** | 3 |
| `preceding::*` | **0** | 1 |
| `following::*` | **0** | 2 |
| `preceding-sibling::*` | **0** | 1 |
| `following-sibling::*` | **0** | 1 |
| `root(.)/*` | **NONE** | `a` |
| `path(.)` | **`Q{...}root()`** | `/Q{}a[1]/Q{}b[1]/Q{}c2[1]` |

#### Why this is serious

Every one of these is a **downward-only violation that XSLT 3.0 requires to be rejected**.
`last()`, `preceding::` and `following::` are **not permitted** in a streamable construct. The
engine accepts the stylesheet and answers `0`, `1`, or `NONE`.

> A stylesheet that is wrong **in a way the spec says must be diagnosed** runs quietly and
> produces plausible output.

`count(preceding-sibling::*)` returning `0` for the second of three siblings is not obviously
broken — **it is a correct answer to a question about the first sibling.** Nothing in the output
distinguishes "there are none" from "I cannot see them".

This is #83's shape one level up: not a capability check answering anyway, but **an entire class
of expression** answering anyway. parsers2 rates it above the defect they were actually fixing,
and so do I.

#### CORRECTED — the corpus CAN see this, and 20 cases fail on it today

The original claim here — *"the streaming sets test streamable stylesheets, not unstreamable
ones"* — was parsers2's and they retracted it. **The `-9xx` cases exist precisely to test
rejection, and 20 of them fail today.**

`sf-current-902` is the clean example, and it shows why nobody traced them: the test-set expects
`XTSE3430` at compile time; the engine **accepts** the non-streamable `current()` pattern,
proceeds, and then dies with **`XTDE0040`** looking for a template named `main` that the
stylesheet does not have. **The engine's error is unrelated to the defect** — which is why these
were first classified as an entry-point problem rather than as streamability.

#### "Did it error?" is the wrong question

parsers2 had run a check splitting the 85 wrong-error-code failures into *raised some error* (85)
and *raised none* (0), and concluded that #85's silent-acceptance class was invisible to the
suite. It is not. **The engine raises an error — just not for the reason the test is about.**

> **"Did it error?" is the wrong question. "Did it error for the right reason?" is the question —
> and the first one looks like a rigorous check while answering nothing.**

That belongs beside #45 (group wrong-code failures by the ACTUAL message, not the pair) and #28
(the harness scored an expected-error case as passing on *any* exception). Three entries now, all
saying that an error's *presence* carries almost no information and its *identity* carries all of
it.

#### Sequencing — and it is coupled to a decision already with Lucas

Not started, deliberately, and the reason is worth stating because it changes what #77 is.

The likely fix is in the **streamability analyser**, and the direction is **"reject more
stylesheets"**. That direction can only ever *lose* conformance cases if the analyser
over-rejects — a change whose downside is invisible until it fires on a valid stylesheet.

**And #77 records that the streamability analyser currently has no corpus evidence at all.** The
919 `sandp` cases that would exercise it are dependency-skipped.

> Tightening an analyser that has **zero corpus coverage**, in the direction most likely to
> cause regressions, is the worst combination of the two. The `sandp` cases are the evidence you
> would want *before* making this change, not after.

So #77 is no longer only a policy question about the denominator. **It is a prerequisite for
fixing this safely** — which is a much stronger argument for enabling those cases than the
coverage percentage ever was, and it should be put to Lucas that way.

#### Unblocked is not the same as small — this is a programme, not a fix (2026-09-13)

parsers2 sampled the 20 failing `XTSE3430` cases. **They do not share a cause.** Four samples,
three distinct analyser rules:

| case | rule |
|---|---|
| `sf-current-902` | non-streamable use of `current()` in a match pattern |
| `si-map-901` | saving streamed nodes in a map value inside `xsl:stream` |
| `square-array-201` | mixed crawling and grounded sequence — LHS of `/` not scanning |
| `accumulator-059` | accumulator streamability (no comment; 6 cases in that family) |

By family: **accumulator 6, sf-current 4, si-map 3, error-34xx 2, and five singletons** —
roughly **10–15 independent rules**, each needing its own reasoning about posture and sweep.

**Recorded explicitly so "unblocked" is not read as "20 cases, go get them."** It is a good
backlog and a bad single PR.

The measurement story for whoever takes it is unusually good, and each rule can be landed
separately: **20 named targets**, **135 `XTSE3430` cases that must keep passing**, and **~2373
executed streaming cases** that catch over-rejection immediately.

#### First rule landed (xslt #79) — and the over-rejection risk fired on attempt one

`current()` in a streamable mode's match pattern. **+4, zero losses.** `strm1` 712/729 → 716/729.

**The first version was wrong in exactly the direction feared, and only the sweep caught it.**
Rejecting *any* `current()` in a streamable pattern won all four targets and **lost
`sf-current-100`** — a stylesheet that must be **accepted**. The most obvious reading of the spec
clause produced an over-rejecting rule on the first attempt.

That is the measurement design earning its keep immediately: the 135 must-keep-passing cases are
not a formality, they caught a regression the author had no reason to suspect.

#### The method for the remaining rules: diff the pair

`sf-current-100` (must be accepted) and `sf-current-902` (must be rejected) **share three
identical patterns and differ by one line.** So the rule cannot be "mentions `current()`":

| pattern | why |
|---|---|
| `namespace-uri(current())` | name off the start tag | **motionless** |
| `current()/@UNIT` | attribute on the start tag | **motionless** |
| `current()/../@CAT` | ancestor already seen | **motionless** |
| `current()='x'` | atomizes an element | **not** |
| `current()/text()` | content not delivered yet | **not** |

`current()` is treated as the node reference it is: motionless inside a name-inspection function
or at the head of a path, non-motionless when its **value** is taken, and a path from it judged
by axis.

> **When a `-9xx` case must be rejected and a sibling must be accepted, diff them. The corpus
> almost always ships the pair, and the diff IS the rule.**

That is the recommended approach for the remaining rules, and it is better than reading the spec
clause and guessing at its boundary — which is what produced the over-rejecting first version.

#### RULE, evidenced twice: write the ACCEPT cases FIRST

Not an observation any more. **Both rules implemented so far over-rejected on the first attempt,
each from a correct reading of the spec clause:**

| rule | caught by |
|---|---|
| #79, `current()` in a match pattern | `sf-current-100` — a corpus case that must be accepted |
| #80, both operands consuming | **five existing accept-side unit tests, before a sweep ever ran** |

> The reject cases only restate the targets you already know. **The accept cases find the
> boundary — and the boundary is where the entire risk of this programme lives.**

**Anyone continuing #85 should start from the accept cases, not from the failing case.** #80's
version caught its over-rejection before any measurement, which is the cheapest possible place to
find it.

#### Test shape: for a tightening change, the ACCEPT cases are the valuable half

Eleven unit tests: **five accept, four reject, two controls.**

> The **reject** cases merely restate the targets. The **accept** cases are what stops the *next*
> person's tightening from over-rejecting.

That inverts the usual instinct of testing the thing you just fixed. For any change that makes an
analyser stricter, the tests worth writing are the ones asserting what must still be **allowed** —
they are the only durable defence against the failure mode this rule already demonstrated once.

#### Why nobody had traced these

The `sf-current` cases were invisible **as streamability failures**, because the engine's error
was `XTDE0040` about a missing entry point. Four cases sat in a cluster first read as an
entry-point problem.

#45's shape exactly — group by the **actual** message and you file them under the wrong heading
entirely. Worth expecting the same for the remaining families: **accumulator 6, si-map 3,
error-34xx 2, five singletons.** Some of those are probably not filed where their cause lives
either.

#### The pair can be one file — and the rules are not all the same size

Applied to the accumulator family, "diff the pair" **sized the work rather than solving it**,
which is worth as much.

**The accumulator pair is cleaner than `sf-current`: one stylesheet, two test-cases.**
`accumulator-009` and `accumulator-009s` share `accumulator-009.xsl`; the `s` case adds
streaming. So it is not two files to diff but **one file evaluated under two regimes**, which
removes all doubt about what the difference is.

The rule behind it is far deeper. From the test's own description:

> *post-descent accumulator function used repeatedly in a single instruction, with no preceding
> consuming instruction … the rules take into account the **order of instructions** within a
> sequence constructor.*

So legality depends on **instruction order** and on what has consumed the stream **earlier in the
same sequence constructor**. That needs a **consumption model across a sequence constructor**,
not a predicate walk.

> **`current()` was a leaf-level rule. This is a flow-sensitive one.** The remaining families are
> not all the same size, and this one is at the far end.

Not started deliberately — it is not a slice that finishes cleanly, and starting it would leave
it half-done.

#### Recommended order for the remaining rules, cheapest first

| # | family | why here |
|---|---|---|
| 1 | **si-map** (3) | "saving streamed nodes in a map value" — sounds like a value-escape check, plausibly leaf-level like `current()` |
| 2 | **error-34xx** (2) | likely direct assertions about the error itself; worth a look purely because they may be near-free |
| 3 | **singletons** (`square-array-201` and four others) | one rule each, no leverage, but self-contained |
| 4 | **accumulator** (6) | biggest cluster, deepest rule, needs the consumption model. **Best value per rule if it generalises; worst place to start** |

**Carry the caveat with it: the family grouping is a hypothesis, not a map.** The four
`sf-current` cases were filed under an entry-point error until someone looked, so at least some of
those singletons may not be singletons — and the accumulator six may not all need the same rule.

#### All five singletons diagnosed (2026-09-13) — four are real, one is not ours

parsers2 diagnosed rather than implementing another rule, on the grounds that the diagnosis is
worth more than one more merge at this point. **Two of their own reads were wrong and were
corrected mid-investigation**, which is the part worth keeping.

| case | the error it reports | what it actually is |
|---|---|---|
| `sx-fcall-027` | `FODC0002` document not found | streamability miss |
| `si-assert-902` | `XX99: Assertion failed` | streamability miss |
| `su-shallow-descent-902` | cannot cast `<whole document>` to double | streamability miss |
| `square-array-201` | output mismatch vs `<any-of>` | mixed-posture, produced output |
| `mode-1903` | output `"brown-fox"` | **not a streamability rule — see #90** |

**`sx-fcall-027` was first called a corpus issue and is not.** The engine fails with *"document
not found: sx-FunctionCall/books.xml"*, and the file genuinely is absent — but **the test expects
`XTSE3430` at compile time, so a correct engine never opens it.** The missing file is
*downstream* of the missed rejection.

> **Third time today an error message pointed away from its own cause** — after `sf-current`'s
> `XTDE0040` and `si-map`'s missing file. In this area the reported error is close to worthless
> as a classifier.

#### Diff-the-pair again — and the pair was in the KEYWORDS, not the source

`sx-fcall-026` **is** correctly rejected; `-027` is not. The corpus keywords name the difference
exactly:

```
026   striding  leading-lone-slash
027   crawling  leading-double-slash  SimpleMapExpr
```

So the rule is **a user-function call with a crawling argument inside a simple map.**

That extends the method: **the discriminating pair is not always two stylesheets to diff.** It can
be one file under two regimes (`accumulator-009`/`-009s`), or — as here — **the test metadata**,
where the corpus authors have already written down the distinction the rule turns on. Check the
keywords before reading the source.

#### Method note from the same session

parsers2 first read this table as their own ancestor fix being incomplete, because the probe
showed `parent=NONE anc=[]`. It was not: asking for parent and ancestors **alone** on the same
document gives `parent=b anc=[a b]`, matching unstreamed exactly. The other expressions in the
probe were dragging the whole template elsewhere.

> **A compound probe attributes any failure to whatever you happened to be working on.**

Seven things tested at once nearly produced a report of a partial fix. Instrumenting settled it
in under a minute — the stack frames showed the parent ids being set correctly all along, which
placed the problem downstream of the change rather than in it. Second time in two days that
instrumenting first beat reading.

### 86. PATTERN — the default spelling of a type and its explicit spelling took different code paths (2026-09-13)

Found by parsers2 probing where #84 died. Fixed in xslt #77. **The worst of today's findings by
consequence: an `xsl:function` declared `as="item()*"` silently loses element nodes.**

#### Reproduction

```
<xsl:function name="f:m">                A<e/>B    ->  3 items   T E T
<xsl:function name="f:m" as="item()*">   A<e/>B    ->  2 items   T T
<xsl:function name="f:m" as="item()+">   A<e/>B    ->  2 items   T T
<xsl:function name="f:m" as="node()*">   A<e/>B    ->  3 items   T E T
<xsl:function name="f:m" as="item()*">   <e/><g/>  ->  1 item    T
```

**No error. Not stringified. The element is gone.**

#### Cause

A function body writes elements as markup into the output buffer and text into the accumulator.
One branch of the result assembly parses that buffer back into nodes; its guard listed the node
item types and `func.As == null`, but **not `item()`**. With `as="item()*"` the branch is skipped,
the branch below returns the accumulator's text items, and **the buffer holding the element is
discarded.** The fix is one token — `item()` joins that list.

#### The shape — and it is not #81, #83 or #84

Not a marker type leaking, not a capability check degrading. This is:

> **The widest type in the language behaved as the narrowest, and the DEFAULT spelling of it
> behaved correctly while the EXPLICIT spelling lost data.**

`item()` is by definition the type that excludes nothing, so it is the **one declaration that can
never justify dropping anything** — and it is also what a function gets when you write no `as` at
all. So the same function, with the same body, returns different results depending on whether you
write the type out.

**Anyone who declares types explicitly — the careful habit — gets the broken path.** And anyone
comparing the two versions sees them disagree with no reason visible anywhere in the stylesheet.

**The general form:**

> When a type's **default** spelling and its **explicit** spelling take different code paths, they
> will eventually disagree — and the explicit one is the one nobody tests, because the default is
> what every existing test was written with.

#### The sweep was run: clean — no other instances (2026-09-13)

parsers2 ran it across the XSLT engine. **Negative result, recorded so nobody repeats it:** the
function path was the only site.

| site | state |
|---|---|
| `Variables.cs:329`, `:1203` (variable paths) | already include `Item` |
| `IsNodeType` | correctly excludes `Item` — `item()` genuinely is not a node type |
| `PreservesItems` | includes `Item` |
| parser's static-value check | falls through conservatively |

**A negative result from a mechanical sweep is worth as much as a positive one, and is almost
never written down.** Without this, the next person reads the tell below, runs the same grep, and
spends the same hour confirming nothing.

#### The tell is mechanically searchable

Grep for allow-lists of the form `ItemType is X or Y or Z` that **accept `As == null` but omit
`ItemType.Item`**. That pairing is the exact signature: a guard that admits "no declaration" while
excluding "the declaration that means the same thing". Worth a sweep — this is one of the few
patterns in this register with a syntactic tell rather than a semantic one.

#### Measurement

base 348 / change 349; `attr` WON `[as-0141]`, `insn` LOST the two known flickers (re-ran `insn`
twice on the branch, neither failed). **Zero real losses, one real win.**

**Do not over-read that win.** `as-0141` moved because a W3C case **already declared
`as="item()*"` on a function returning an element** — a case the suite already had and was
already failing. **The suite was not blind to #86; it was failing on it without anyone knowing
why.** See the correction in #70: that is a different problem from missing coverage, and a more
tractable one.

Nine tests, six failing on main. One asserts **the invariant directly** — explicit `item()*` must
agree with declaring nothing — rather than pinning two expectations that could later drift apart
without the test noticing. That is the right shape for a defect of this kind: the bug *was* the
disagreement, so the test should assert agreement, not two values.

### 87. FIXED 2026-09-13 (xslt #78) — `xsl:import-schema` failed on any schema referencing the XML namespace

**13 conformance wins, zero real losses, from a one-method change.** The best single result of
the session, and the **first application of the failure-log sweep method**.

```
base failures 347   change failures 336
strm2  LOST []  WON [si-fork-001..009, si-fork-901, si-fork-902, si-map-007, si-map-009]
insn   LOST [the two known flickers]  WON []
```

#### The defect

```
XQST0059: xsl:import-schema failed ... loans.xsd:
  The 'http://www.w3.org/XML/1998/namespace:lang' attribute is not declared.
```

XSD 1.0 §4.2.6.2 lets a schema import the XML namespace **with no `schemaLocation`**, because
every processor is expected to already know it. **.NET's `XmlSchemaSet` does not**, so
`<xs:attribute ref="xml:lang"/>` was undeclared and the entire `import-schema` failed — **the
stylesheet would not load at all.**

The failure is *inside the imported `.xsd`*, so **nothing the stylesheet author writes avoids
it.** Fixed by seeding the default provider with the XML namespace's four attributes and
`specialAttrs`. Entirely XSLT-side: `XsdSchemaProvider.AddFromString` is public, so no XQuery
change.

#### Six remaining schema failures are a platform limit, not defects

Of 19 schema-loading failures, that cluster was the tractable one. The other six use `xs:assert`
— **XSD 1.1** — and .NET implements **XSD 1.0 only**. That is a platform constraint of the same
kind as the .NET-8-runtime requirement, **not an open bug**, and it should not sit in the
open-bug count pretending to be work someone could do.

#### The method comparison — and it flatters neither approach alone

This came from clustering the 346 currently-failing cases by family and looking for one cause
behind a group. `si-fork` was the largest cluster at 22; a single error explained 13.

> **The two methods find disjoint sets.**
>
> Today's **hand-probed** defects (#71, #79, #84) were mostly invisible to the corpus — real,
> reproducible, worth fixing, and worth nothing in the numbers.
>
> This one was worth **13** and **would never have turned up by probing**, because nobody writes
> a stylesheet importing `loans.xsd` by hand.

So neither is the better method; they answer different questions. The practical ordering:

- **The failure-log sweep is the default when there is a gap** — it starts from evidence already
  paid for, and it has a **measurable denominator**, so you can tell when it is exhausted.
- **Probing is what you do when someone hands you a real-world stylesheet** — it finds what users
  hit, which is precisely the set the corpus was never built to contain.

Recorded because the temptation after a 13-case win is to conclude that probing was wasted
effort. It was not; it found defects a user would meet and the sweep never would.

### 88. Two test-sets the harness runs are NOT in the catalog, and both are broken (2026-09-13)

Found by parsers2. **Not acted on** — it changes the denominator, which is Lucas's call, and it
reframes the held `decl/expose` PR (xslt #57) as one third of a package rather than a standalone.

> **Note on numbering:** xslt PR **#57** wires `decl/expose`. BUGS.md **#57** is the separate
> as-shipped-vs-at-main question. They share a number by coincidence and are different decisions.

#### Run by the harness but NOT in `catalog.xml` — 2 sets, both broken

```
tests/strm/sf-map-new/_sf-map-new-test-set.xml
tests/strm/sx-PathExpr/_sx_PathExpr-test-set.xml
```

Both are **duplicate test-sets whose stylesheet references do not exist beside them**.
`sf-map-new/` contains `sf-map-new-*.xsl` while its test-set asks for `si-map-*.xsl`;
`sx-PathExpr/` contains `sx-path-001.xsl` while its test-set asks for `sx-treat-901.xsl`. Every
case fails with *"Could not find file"*.

**They account for 7 failures** — `si-map-007`, `si-map-009`, `si-map-901`, `si-map-902`,
`si-map-903`, `sx-path-001`, `transform-001`.

#### SHARPENED 2026-09-15 — the seven are two different things, and only six move

"All phantom" was my wording and it is too coarse. parsers2 checked the baseline file rather than
inferring, and the rows are:

```
tests/strm/sf-map-new/_sf-map-new-test-set.xml      0   5
tests/strm/sx-PathExpr/_sx_PathExpr-test-set.xml    0   1
```

| | what it is | moves on alignment? |
|---|---|---|
| **6 cases** in `sf-map-new` + `sx-PathExpr` | **phantom denominator** — sets the catalog does not list, that we should not be running | **yes** |
| **2 cases**, `si-map-007` and `-009` | **genuine failures of a real catalog set** (`tests/strm/si-map`), unpassable until the suite files agree | **no** |

So the precise statement is **"6 phantom denominator cases, plus 2 unpassable cases in a real
set"** — not "the denominator is wrong by 7". Only the six leave the denominator if the alignment
happens; the two stay and keep failing.

**Effect on the published figure, if the alignment is taken:** 10,328 / 10,672 becomes
**10,328 / 10,666** — 97.15% → 97.21%. **A percentage move with no case changing behaviour**,
which is exactly the kind of movement that needs saying out loud in a release note or it reads as
progress.

#### NARROWED 2026-09-13 — the other direction is a re-derivation of #72

parsers2 ran the new grep-first check (#89) retroactively over the day's findings and **it caught
a second re-derivation within minutes.** The "3 catalog sets not run" half of this entry is a
**strict subset of #72**, which already records `decl/expose`, `fn/system-property-gen` and
`decl/import-schema` — **plus two that were not rediscovered today**: `fn/collation` and
`sandp/_base-expressions.xml`.

**See #72 for the unrun sets.** The genuinely new finding is the *other* direction — the two
broken sets above, and `sf-map-new` appears nowhere else in this file.

#### The two counts answer different questions, and the catalog is authoritative

| population | count |
|---|---|
| test-set files **on disk** | **264** — what #72 counted against |
| test-sets listed in **`catalog.xml`** | **260** |
| test-sets hardcoded in the **harness** | **259** |

#72's "five missing" is **disk versus harness**. This entry's is **catalog versus harness**. Both
are correct and they answer different questions.

> **The catalog is the authoritative population.** A file on disk that the W3C catalog does not
> list is **not a test we are failing to run — it is a file we should not be running**, which is
> exactly what `sf-map-new` and `sx-PathExpr` turn out to be.

**The alignment decision to Lucas should use the catalog count**, not the disk count.

#### The honest package is both directions at once

- **Dropping only the 2 non-catalog sets** looks like a 7-case gain and is **pure denominator
  shrinkage**.
- **Wiring only the 3 catalog sets** adds unknown failures and **looks like a regression**.
- **Together** they make the suite match the catalog — **the only defensible definition of what
  the denominator should be.**

That is how it should go to Lucas: one alignment decision, not three.

**`import-schema` is the one to look at first.** It has never run, and xslt #78 (the built-in XML
namespace schema, #87 here) merged an hour before this was found. **Those cases have never been
measured against an engine that can load a schema importing the XML namespace.** Genuinely
unknown which way they go.

#### Reading the intent comment is not reading the failure

parsers2 went looking for the `si-map` streamability rules from #85's list. **There are no
`si-map` streamability defects** — the three "failures" are a missing file.

They had **already written three plausible rules into a handoff note**, derived from reading the
stylesheets' own comments: nodes in a map value, key and value both consuming, non-map-entry
children.

> **All three were plausible. None was the actual cause. At least one would have been implemented
> before anyone noticed.**

The stylesheet's comment says what the test *intends to exercise*. It says nothing about why the
run failed — and when the failure is environmental, the comment is a **confident, well-written
description of something that never happened.** Same species as #85's misfiled `sf-current` cases,
from the opposite side: there the error message pointed away from the cause, here the source
comment pointed at a cause that was not operating.

`si-map` comes off #85's list entirely.

#### The same case name existed in two test-sets, one real and one broken

`si-map-007` and `si-map-009` appear as **wins** in xslt #78's A/B (from `strm2`, the real set)
and as **failures** here (from `strm1`, the phantom set).

The per-log diffing kept that honest. **A name-keyed comparison across chunks would have reported
a case simultaneously fixed and broken by unrelated changes** — and the most likely response to
that is to distrust the measurement rather than to discover two sets share a name. Worth
recording as a design property of the sweep that mattered without anyone planning for it.

### 89. The register is the only memory that survives compaction — and it was not consulted (2026-09-13)

Not a defect. A fact about how this pair of sessions works, recorded by parsers2 at their own
expense because the general cost is much larger than this instance's.

**parsers2 found #71 on 2026-09-11, had their context compacted, re-derived it from scratch on
2026-09-13, and reported it as a new discovery.** It landed on the existing entry only because
the other session recognised it.

> **The register was the only thing that survived the compaction, and it was not consulted before
> reporting.** The path went from a failing case to a root cause without ever asking *"do we
> already know this?"* — and the answer was yes, with a number and an estimate attached.

#### Why the general cost is larger than this instance's

The concrete cost here was small: a duplicate entry, caught. The general cost is not.

> **A re-derived finding arrives with all the confidence of a new one and none of the accumulated
> history.**

- **The estimate is re-made from scratch.** 27 became 19, and 19 would have been reported as if
  nothing had ever measured 27 — losing the fact that the cost is *falling* as other work lands,
  which is itself a decision-relevant signal.
- **Any decision already in flight is reopened.** #71 was with Lucas. A "new" finding with the
  same content restarts that clock and makes the question look less settled than it is.
- **Nothing marks it as a repeat**, so the second telling is indistinguishable from the first and
  gets the same weight in a summary.

#### The convention that catches it

> **Before reporting a finding as new, grep `BUGS.md` for the construct.**

Cheap, and the only check that catches this. parsers2's own note on why it would not have
occurred to them is the important half: **a finding you derived yourself does not feel like
something to look up.** The trap is specific to genuine work — nobody re-derives something they
half-remember; they re-derive something they have entirely forgotten, which feels exactly like
discovery.

#### And a second convention: date every number

The estimate improved because it was re-measured against a **moved main** — 27 then, 19 now, as
intervening fixes landed. That is an argument for recording *when* a number was taken:

> **A two-day-old cost estimate on a fast-moving tree is a different kind of claim from a fresh
> one.** A number without a date invites being quoted as current.

Both conventions are now in the file header.

#### The convention worked immediately — on the very next thing checked

parsers2 applied it retroactively to the day's other findings and **caught a second re-derivation
within minutes**: half of #88 was a subset of #72, which they had also written, two days earlier.

**Two re-derivations in one session, from the same cause.** And the cause is not unusual:

> Compaction between the finding and the re-finding **is not an exceptional circumstance — it is
> the normal one for any session long enough to do real work.**

Which settles what kind of check this is. It is **not bookkeeping hygiene; it is load-bearing for
any pair of sessions that compact**, and the failure it prevents scales with how productive the
session was.

#### A register only becomes memory when someone looks something up in it

parsers2's refinement, and it is sharper than the framing below:

> #72 was **written, filed, and correct** — and still did not prevent the re-derivation, because
> it was not read. **A register only becomes memory at the moment someone looks something up in
> it; until then it is an archive of things that were once known.**

Two days was long enough for that distinction to bite, twice.

That reframes every convention in this file's header. They are all rules about *writing*, and
writing was never the failing part — both re-derived findings had been written up accurately the
first time. **The grep-first rule is the only one about reading, and it is the only one that
converts the archive into memory.**

#### The structural point

This register began as a defect list. Across two sessions and at least one compaction on each
side, it has become **the durable memory of the work** — the only artifact that holds why a
decision was made, what a number cost, and which approaches were already tried and abandoned.

That only functions if it is **read**, not merely written. Every convention in the header exists
because writing discipline alone was insufficient; this one exists because reading discipline was
missing entirely, and nobody noticed until the same defect was discovered twice.

### 90. OPEN — a second `xsl:mode` declaration silently discards the first's attributes (2026-09-13)

Found by parsers2 diagnosing `mode-1903`. **Registered separately from #85 because it is #71's
family, not a streamability rule** — streamability switched off silently, by a mechanism with
nothing to do with streaming.

Two `xsl:mode name="X"` declarations: the first carries `streamable="yes"`, the second only
`visibility="public"`.

```csharp
StylesheetParser.Declarations.cs:631
    stylesheet.Modes[modeKey] = mode;          // the second wholly REPLACES the first
```

Conflicts are checked **only for attributes the new declaration explicitly sets**, so an absent
`@streamable` raises no conflict — and the replacement **silently discards `streamable="yes"`.**

The stylesheet's own comment states the intent exactly: *"Check that an `xsl:mode` with no
`@streamable` attribute isn't treated as `streamable='no'`"*.

#### Blast radius is all mode attributes, not just `streamable`

The same replacement will discard **`on-no-match`, `use-accumulators` and `visibility`** — any
attribute present on an earlier declaration and absent from a later one. **Whoever takes this
should estimate across all of them rather than fixing `streamable` alone**, because the mechanism
is indifferent to which attribute it loses.

#### MEASURED 2026-09-13 — the exposure is one case, not "all mode attributes"

parsers2 measured before implementing. Of the **five** corpus stylesheets with a repeated
`xsl:mode`:

| stylesheet | shape | state |
|---|---|---|
| `mode-1502` | both state `on-no-match` | conflict — already handled, passing |
| `mode-1904` | both state `visibility` | conflict — already handled, passing |
| `error-0545a` | both state `on-no-match` | conflict — already handled, passing |
| `error-0545b` | both state `on-multiple-match` | conflict — already handled, passing |
| **`mode-1903`** | `streamable` then `visibility` | **the bug — no attribute in common** |

**Only the fifth has the shape.** The corpus almost exclusively tests the *conflicting* case,
which the engine already handles; the non-overlapping case appears **exactly once**.

> **The mechanism's breadth and the corpus's exposure to it are different numbers** — and I had
> been quoting the first as if it were the second when I asked for an estimate "across all mode
> attributes."

That is right as a description of the mechanism and wrong as an estimate of exposure. The fix is
correct and safe and worth **one case at most**. Real stylesheets that split mode attributes
across declarations would hit it; the W3C suite essentially does not.

Worth carrying as a sizing caveat generally: **breadth tells you what could be affected, exposure
tells you what is** — and only the second is measured.

#### Fixed in xslt #81 — and the partial progress is visible, not inferred

Zero losses, zero wins, no per-set differences. **`mode-1903` still fails, as predicted** — but
the change is observable: it went from emitting `brown-fox` **unstreamed** to **streaming and
emitting nothing.** The mode is now correctly streamable; the missing piece is rejecting its body
for consuming twice (two `xsl:copy-of`).

That is a better position than a flat result: the first half is *demonstrated* rather than argued
for, and it is noted on the PR so a zero does not read as the fix not working.

#### Two independent ways to lose `streamable="yes"`, found in one session

| # | mechanism |
|---|---|
| **71** | the parser compares the attribute against the literal `"yes"`, so `"true"` and `"1"` silently mean not-streamable |
| **90** | a later mode declaration replaces an earlier one and drops the attribute entirely |

Neither raises. Neither is about streaming. **A feature can have more than one off switch, and
finding the first is not evidence you have found them all** — which is worth holding in mind
before concluding #71 explains the streaming picture.

#### Caveat before anyone starts it

**The merge fix alone probably will not flip `mode-1903`.** Once mode X is correctly streamable,
the body must then be *rejected* — two `xsl:copy-of` instructions, each consuming — and **it is
unverified whether the engine detects two consuming instructions in one sequence constructor.**
Likely two rules again, as with everything in this area (#84, #85).

Recorded so the first measurement showing no movement is read as expected rather than as the fix
not working.

### 91. OPEN — built-in-rule descent to a matching template emits the element empty and leaks its text (2026-09-13)

Found by parsers2 while writing tests for xslt #84. **Not fixed, and deliberately not
test-covered** — see below. Present on main independently of anything merged today.

```
streamed     <out><t/>one</out>
unstreamed   <out><t>one</t></out>
```

The element is emitted **empty** and its text **escapes outside it**. No explicit template with a
multi-step select is required, so this is a **wider shape than #84 covers**.

#### Clearing an entrance reveals everything behind it at once

xslt #86 is small but worth the note: **it was unreachable until #84 landed.** A multi-step
select never reached that driver, so its per-parent `position()` counter could not fire.

> **In a code path that was previously unreachable, every defect behind the entrance is
> invisible. Clearing the entrance reveals them all at once.**

Two consecutive PRs touching the same driver could easily read as the first having been wrong. It
was not, and parsers2 said so in the PR body for exactly that reason.

Two consequences worth carrying into any cluster of this kind:

- **The count going down more slowly than expected is not a sign the fixes are failing.** Each
  fix can expose its successor, so a cluster of N can take more than N changes without anything
  having gone backwards.
- **A second PR against the same code is not evidence the first was incomplete** — and it looks
  exactly like that in a commit log, which is why it needs saying at the time rather than being
  reconstructed later.

This is #73's accidental-guard shape in a new form. There, a permanently-failing parse hid the
code behind it; here, an unreachable entry condition hid the code behind *it*. Same conclusion:
**everything downstream of something that never runs is untested by construction.**

#### Deliberately not covered by a test

parsers2 wrote the case, saw it fail, and **removed it rather than ship it.**

> **A test that encodes a bug is worse than no test.** It either goes in red — where it is noise
> that trains people to ignore a red suite — or it gets "fixed" by asserting the current output,
> **and that is how a defect becomes a specification.**

Registering the defect keeps it findable without pinning the wrong answer into the suite. Same
judgement as #66, where the streamed/unstreamed `accumulator-after()` disagreement was recorded
rather than encoded — and the same reasoning: the corpus is a claim about what is correct, and
writing a known-wrong expectation into it makes the claim false in a way nobody re-reads.

#### A failing accept-side test is only evidence about your change if it passes without it

parsers2's first assumption on seeing the failure was that their own change had caused it. **It
had not — it fails identically on main.** Confirming that took one command, which they nearly
skipped because the conclusion felt obvious.

Same species as #76's guard-3 material and #85's compound probe: **a result that implicates your
own work is the one you are least inclined to check and most likely to misattribute.** The
accept-side tests introduced by the #85 rule (write the accept cases first) make this *more*
likely, not less — they are written before the fix is finished, so a failure among them naturally
reads as "I have broken something" rather than "this was already broken".

Cheap check, and it belongs beside the rest: **run the failing test on base before concluding
anything about your change.**

### 92. OPEN — `has-children()` always answers false under streaming, and the cheap fix is wrong (2026-09-13)

`streamable-135`. Diagnosed by parsers2 and **deliberately not started** — it needs a streaming-core
change, and the available shortcut is the trade refused four times already this session.

#### The defect

`has-children()` is implemented as `elem.Children.Count > 0`. **A streamed element is shallow**,
so it always answers **false**, and the test's root matched `*[not(has-children())]`:

```
actual     <account nr="76543210" empty="true"/>
expected   the element with its children copied through
```

#### Why the obvious fix is wrong

The reader knows `IsEmptyElement` at the start tag, so `!reader.IsEmptyElement` **would pass this
test immediately.** It is not the same question:

> **`<a></a>` is not an empty element and has no children.**

So the shortcut buys the case by making the function **silently incorrect on a shape the corpus
happens not to exercise** — exactly the trade declined on the base output URI (#69), on
`SENR0001` (#70), and on `streamable="true"` (#71). **Fifth refusal of that shape this session.**

#### What a correct fix needs

The pattern is evaluated **at the start tag, before any child event is read.** Answering
truthfully requires reading the next event and keeping it — **a genuine one-event lookahead buffer
that every downstream consumer of the reader respects.**

`_streamingDeferReadOnNextIteration` is **not** that: it is a *do-not-advance* flag, not
peek-and-pushback. So this is a streaming-core change with a wide blast radius, not a leaf fix.

**Sized rather than started**, same call as the accumulator rule (#85): a half-built lookahead
left at the end of a session is the worst possible artifact to hand over.

#### A prospective warning, which is the valuable part

> **The corpus cannot distinguish a correct implementation from `!IsEmptyElement` here.**

`streamable-135` passes under both. So whoever builds the lookahead **must add the `<a></a>` case
as a unit test themselves** — because the test they will naturally use to verify their work
**will not verify the thing they got right.**

This is #67's corpus-as-proxy used **forwards** rather than backwards. Everywhere else in this
register that pattern is a diagnosis of how a defect survived; here it is a warning issued
*before* the work starts, to someone who does not exist yet. That is a better use of it, and
worth doing wherever a cheap wrong answer and a correct one are indistinguishable to the suite.

#### Where that leaves #71's remaining eight

| group | cases |
|---|---|
| needs streaming-core work | **`streamable-135`** — this entry |
| `fn:path()` output | `streamable-137/138/139` |
| tunnel parameters | `streamable-064/065` — `-065` emits `<out/>`, total content loss |
| other | `accumulator-003s/005s` duplication, `stream-211` + `sx-gc-eq-801` dropped content |
| **reclassified (#94)** | **`doe-0802` and `streamable-064` share a root cause** — no `StreamingPlanner.Plan` for the shape, so the construct falls to the text-only sink. Filed apart under *double-escaping* and *tunnel parameters* until a `Debug.Assert` grouped them by cause. |

### 93. OPEN — the buffered subtree has no ancestors, and the missing parent is load-bearing (2026-09-13)

`streamable-137/138/139`. A fix was built, **measured at +2**, and **reverted** — it trades
accumulator correctness for two cases. **Sixth refusal of that shape this session.**

#### The defect — and it is the FOURTH driver missing this

`ExecuteWithBufferedSubtreeAsync` materialises the subtree **detached**, so the buffered root has
no parent and its ancestor axis stops at itself:

```
ancestor-or-self::node()    streamed 1 node    unstreamed 3 (document, root, match)
```

| driver | ancestors |
|---|---|
| processor's forward pass | sets a parent from its ancestor stack |
| striding descent | `LinkStridingAncestors` |
| **`ExecuteWithBufferedSubtreeAsync`** | **nothing** |

**#74's per-call-site shape, fourth instance** — an invariant that must hold everywhere, with no
shared helper enforcing it, and two of the three sites having solved it independently.

#### Why the one-line fix cannot work

`bufferedElement.Parent = element.Parent` works: `attr` 33 → 31, `streamable-138/139` pass. It
also **breaks `AccumulatorAfter_CountsTheSubtree`** with `XTDE3362: accumulator is not applicable
to the tree containing the context node`.

The reason is **structural, not incidental**:

> **The accumulator machinery decides which tree a node belongs to by walking to its root.** With
> no parent, the buffered root **is** its own root, so the lookup walks the buffered subtree and
> succeeds. **Any parent at all** moves the root onto the streamed tree, where the accumulator was
> never computed in that context.

Cloning the ancestors into fresh nodes does not help — it moves the root somewhere else that is
equally wrong.

So **the missing parent is load-bearing.** The absence that causes the defect is the same absence
the accumulator lookup depends on. That is #84's shape exactly — there, the marker type causing
the defect was load-bearing for the mechanism that would have to change to remove it. **Twice now
a "just add the missing thing" fix has turned out to be removing a load-bearing absence.**

#### What a correct fix needs

Make the accumulator lookup **buffered-root aware**, or give the node ancestors **without changing
what `root()` answers.** That is accumulator-machinery work and a **prerequisite**, not an
alternative.

**Start point, unverified:** `_bufferedSubtreeOrigin` already maps buffered root → streamed
element and is used for accumulator-**before**. Extending that idea to the applicability/after
path is the obvious first move, and the comment at that site explains why *after* differs from
*before*. parsers2 stopped at the diagnosis and did not test this.

#### The diagnostic tell — three wrong hypotheses first

The ancestor axis was blamed, then `last()`, then the `for-each` loop. What settled it was
instrumenting and seeing `items=1 strExec=False` — **a different execution path, not a bad
count.**

The tell was that the same loop iterated **three times with a literal body and once with
`position()`**:

> **When output changes with body content that should be irrelevant to iteration, suspect
> ROUTING rather than the loop.**

The body was selecting the route. That belongs with the instrument-first material (#80, #85):
three readings of the code produced three wrong hypotheses, and one instrumented run produced the
right one — the fourth time this session that instrumenting beat reading.

### 94. The engine contains a purpose-built diagnostic for this class of defect, and no run exercises it (2026-09-13)

Found by parsers2. **The codebase already held a detector for exactly the failure mode we spent
the evening chasing** — dormant, because the build configuration that enables it was turned off
for an unrelated and correct reason.

```
#143 Task 1.3 invariant: a guaranteed-streamable construct (...) reached the
built-in text-only-copy sink for a element node. It must stream or buffer via
StreamingPlanner.Plan, never collapse structure to text.
```

It is a `Debug.Assert`. **The conformance suite runs Release, so it never fires**, and every
violation instead presents as **silently wrong output**.

#### The decision that disabled it was correct — for a different purpose

`conformance.sh` already supports `CONFORMANCE_CONFIG=Debug`. The header records why Release
became the default: Debug times out on ~38 cases (#43).

> **That is the right call for SCORING and the wrong one for DIAGNOSIS**, and nobody revisited
> whether the diagnostic value survived the decision.

A new shape worth naming, and distinct from the hollow-pass family: **not a check that fails
open, and not a test that tests nothing — a working detector we built, switched off as a side
effect of an unrelated correct choice.** The decision was sound, documented, and made for
scoring; its cost to diagnosis was never priced because nobody was asking a diagnostic question
at the time.

#### It groups by CAUSE, not by symptom — and that is the whole payoff

Run as a diagnostic rather than a score:

```
attr    2 violations
  doe-0802         template match="doc",  body starts with XsltLiteralResultElement
  streamable-064   template match="/",    body starts with XsltApplyTemplates
strm1   0 violations
```

**Those two share a root cause.** They were filed separately — `doe-0802` under *double-escaping*
and `streamable-064` under *tunnel parameters* — **from reading their symptoms.** They are the
same defect: `StreamingPlanner.Plan` produces no plan for the shape, the construct falls to the
text-only sink, and structure collapses to text. **Neither symptom hints at the other.**

> **Clustering by error message groups by symptom. Clustering by this assertion groups by cause —
> because the assertion is placed at the cause.**

That extends #87's failure-log sweep to a strictly better signal where one exists. The sweep
method asks *"what single cause explains this group of failures?"* and answers it by inference;
an assertion at the cause answers it **directly**, and it found a pairing that three days of
symptom-reading had filed apart.

#### Negative result, recorded so nobody over-claims it

**`strm1` produced zero violations.** This is **not** a universal streaming-defect detector — it
finds one specific class. Cheap to run, and it will not explain everything.

#### DEFLATED 2026-09-13 — both follow-ups were run, and both came back empty

parsers2 ran the checks proposed here rather than assuming they would pay, and **the entry as
first written over-claimed twice. Both over-claims were mine.**

**1. There are no other dormant detectors. That assertion is the only one.**

| | count |
|---|---|
| `Debug.Assert` in `PhoenixmlDb.Xslt` | **1** — the one above |
| `Trace.Assert` | 0 |
| `#if DEBUG` blocks | 0 |
| `Debug.Assert` in `PhoenixmlDb.XQuery` | 0 |

So *"sweep for other dormant detectors"* is **done, and the answer is none.** This is a one-off,
not the tip of an iceberg. My line — *each assert is a detector someone wrote at the moment they
understood the invariant best* — is a fine argument about a population of **one**.

**2. The full-corpus Debug sweep found exactly the same two cases.** All chunks, not just `attr`.
**Total yield across the entire corpus: two.** Not two per chunk, not a cluster.

So **"run Debug as a routine diagnostic pass alongside Release" is not supported by this
evidence.** It costs a full extra sweep to re-find two already-known cases. Worth doing **once**
when someone adds an assert — not every run.

#### What actually survives, at its real size

The pairing is real: an assertion sited at the cause connected two cases that symptom-reading had
filed under *double-escaping* and *tunnel parameters*, and parsers2 would not have found it
otherwise. **That stands.**

But the mechanism found **two cases, in one class, from one assert that happens to exist.** The
transferable claim is narrower than a method and still true:

> **If a codebase has an assertion at the cause of a defect class, the configuration that enables
> it is worth one run.** Here that cost about ten minutes and the answer was two.

Deliberately written as *a good catch of limited scope* rather than as a method, because **the
next person acting on the larger version will spend a sweep and get nothing.**

parsers2's note on doing this to their own find: *"It looked like a method for about twenty
minutes."* Which is the useful general observation — **a real finding and an over-generalised one
are indistinguishable at the moment of discovery**, and the only thing that separates them is
running the follow-up you would otherwise have written as a recommendation.

### 97. A 222-line workaround for a downcast past an interface that already existed (2026-09-14)

`Engine/XsltSerializeFunction.cs` overrode `fn:serialize` entirely, because XQuery's serializer
emitted `xmlns:p=""` for XSLT's nodes and the output did not reparse. Its own header called this
"the third defect from that one type test" — the signature of a workaround sitting on a
structural gap rather than a bug.

**The gap:** `XQueryResultSerializer` asked `provider is XdmDocumentStore` before every
namespace-id lookup, in eleven places, and any other store answered nothing.

**The part worth recording:** the interface was already written. `INodeStore : INodeProvider`
declares `GetNamespaceUri(NamespaceId)`, and BOTH stores already implement it — `XdmDocumentStore`
explicitly, and XSLT's own `XdmInMemoryStore` as a plain member. **The serializer was downcasting
past an abstraction it already had.** Nothing needed inventing; xquery #31 is the existing
interface being used.

**Retiring the override: prepared, held on the pin.** With xquery #31 in the build, deleting the
222 lines leaves **1776 XSLT unit tests passing, XSLT 10,310/10,672 and QT3 29,534/31,414 —
unchanged**. Branch `fix/retire-serialize-override`. Against the pinned XQuery 1.8.0 the override
is genuinely still needed: removing it fails 6 tests and CI catches it. **The hold has a trigger —
the pin bump — not a date.**

#### Two environment faults found on the way, the same shape as #98

Neither was a code defect; both produced confident wrong readings:

- `phoenixmldb-core` sat on `main` but **9 commits behind origin**, at 1.6.7 while 1.7.0 is
  published. Dev mode then failed to compile (`XdmStringValueResolver` absent), which reads as
  "the dev-mode wiring is broken" rather than "your checkout is stale".
- Earlier, the XSLT unit tests were run against the **pinned package** instead of the modified
  source, and the conclusion drawn was that the XQuery fix had not worked — six tests failing with
  the original symptom, unchanged. The fix was fine; the build was not testing it.

**Three sibling checkouts, three different stale states, in one session** (#98's XQuery detached
at v1.7.0, Core nine behind, and the pin-vs-source build here). Each turned a signal into a
statement about the wrong thing.

### 95. THE WEAKEST AREA IN THE ENGINE — accumulators, and the root-walk that blocks their streaming variants (2026-09-13)

#### Reframed 2026-09-13 — this was filed as an obstacle, and it is the headline

Originally written as *"a prerequisite blocking four streaming cases"*, which treats it as
something in the way of streaming work. **parsers2 measured the areas and corrected the framing:**

| area | pass rate | failures |
|---|---|---|
| streaming (`strm1`+`2`+`3`) | **97.3%** | 64 |
| `decl` | 92.7% | 79 — **worst chunk** |
| `decl/accumulator` | **81.6%** | 19 — **worst set measured** |

> **Accumulators fail at ~18%, streaming at ~2.7% — about seven times the rate.** An evening went
> into the 97% area.

**And they are not separable problems**, which is the part that matters: **ten of the eighteen
accumulator failures are `-s` streaming variants**, and the accumulator machinery blocked four
streaming fixes measured at **+3 combined**. **Accumulators are worst *under streaming*, and
streaming progress is gated *on accumulators*.**

So the root-walk collision below is **a symptom of the weakest feature area in the engine**, not
an obstacle in front of a healthy one. **It should be the next thing started, ahead of the
remaining streaming cases** — first thing, not fourth.

The stopping call stands: three failed cycles means it wants a fresh session with the accumulator
machinery in front of it, not a fourth attempt appended. But it should be the session's first
work, not its last.

#### Measured result of the 2026-09-14 session — the area is no longer the worst

| | before | after | |
|---|---|---|---|
| `decl/accumulator` | 84/103 **81.6%** | **91/103 88.3%** | +6 net, 18 failures → 12 |
| `decl` chunk | 1000/1080 92.6% | **1007/1080 93.2%** | 80 failures → 73 |

Six fixed across five PRs, **every one A/B-swept over all eleven chunks with zero per-set
losses**: `003s` `005s` (xslt #87, the #66 carry-over), `030s` `034s` (#88), `076` (#89), `060`
(#91).

**Five of the six were static analysis, not accumulator machinery** — exactly as the correction
above predicted. None of them touched the root-walk. That is the evidence for the reframing, not
just an argument for it.

**Twelve left, and only ONE of them is unblocked and in this cluster:**

| case | status |
|---|---|
| `059` | the last open XTSE3430. Needs POSITIONAL analysis of a template body relative to its consuming instructions — a structurally different check from the four landed here, all of which were contained questions about one expression or declaration. **Deliberately not attempted late in a session**; it is a good first task, not a fifth one. |
| `009s` `019s` | blocked on #68 (spec reading) |
| `031` `038` `061` | other expected errors not raised — XTDE0640, XPTY0004, XTDE3350. Unexamined; three separate questions, not a cluster |
| `040` `048s` `049s` `062` `077s` `079s` | genuine accumulation behaviour. `077s` is #52. **This is now the largest group**, and it is where the root-walk work belongs |

**So the sequencing advice inverts from where it started.** The static-analysis cluster is spent;
what remains is mostly real accumulation behaviour, which IS the machinery this entry was
originally about. The root-walk gates that group — it just never gated the seven that went first.

#### Corrected 2026-09-14 — I had the area's shape wrong, and the correction changes what to do next

The characterisation above ("accumulators are the weakest area") is right about the *numbers* and
wrong about the *kind* of defect, because I built it from output strings without reading what each
case EXPECTED. Doing that properly:

| cluster | count | cases |
|---|---|---|
| **XTSE3430 — static streamability of the declaration** | **7** | `030s` `059` `060` `076` `009s` `019s`, plus `034s` (an OVER-rejection) |
| other expected errors not raised | 3 | `031` XTDE0640, `038` XPTY0004, `061` XTDE3350 |
| genuine accumulation behaviour | 8 | `003s` `005s` `040` `048s` `049s` `062` `077s` `079s` |

**The largest single cluster is not accumulation at all — it is static analysis**, and it lives in
`StreamabilityChecker` / `StylesheetParser`, not in the accumulator machinery. Six under-rejections
and one over-rejection, from ONE blanket rule: *"a streamable accumulator pattern must not contain
predicates."*

The right question is not "is there a predicate?" but **"does this expression read something the
stream has not delivered at this node?"**, which resolves into three clauses:

| expression | motionless? | why |
|---|---|---|
| navigates child/descendant | **no** | the content has not arrived |
| reads the matched node's own value, and it is an element | **no** | an element's string value IS its descendant text |
| reads an attribute's value | **yes** | attributes arrive with the start tag |

**Fixed on that reading: `034s` + `030s` (xslt #88, +2) and `076` (xslt #89, +1).**

The middle clause was not in the first attempt and is the useful part. Judging by written axes
alone measured **+2/−1**: it lost `stream-204`, whose pattern `part-name[$selected-parts =
current()]` writes no axis but atomizes the matched element. **That single loss is what produced
the clause** — worth more than the two wins, and an argument for the zero-per-set-loss rule
earning its keep rather than being a tax.

**What this means for sequencing.** #95 said accumulators should be first because they are worst
under streaming. That still holds. But "accumulators are broken" pointed the work at the
accumulator machinery, where three cycles had already failed — and seven of eighteen were reachable
from the streamability checker, a file with no root-walk problem in it at all. **The root-walk
blocker below gates fewer cases than this entry implied.**

Remaining, in order of tractability: `060` (`../accumulator-after()` reads the post-descent value
off a parent) and `059` (`accumulator-after()` in the pre-descent phase) — both checks on a
TEMPLATE BODY rather than on the declaration, and `059` needs positional analysis of the body
relative to its consuming instructions. `009s`/`019s` stay blocked on #68.

#### The structural finding, as originally recorded Two independent fixes, in
different drivers, measured and reverted for the same reason. That makes it a **prerequisite, not
a coincidence.**

#### The collision, hit twice

> **The accumulator machinery decides which tree a node belongs to by walking to its root.** So
> giving a streamed node a parent **moves its root**, and accumulator resolution breaks.

| attempt | fix | result |
|---|---|---|
| buffered-subtree ancestors (#93) | `bufferedElement.Parent = element.Parent` | **+2**, broke `AccumulatorAfter_CountsTheSubtree` |
| nested-dispatch parents (this entry) | one line | **+1 / −1**, broke `accumulator-080s` |

The second is the sharper demonstration: the first broke a **unit test**, this one breaks a
**conformance case the unit tests do not reach.** Same assumption, two different detectors, three
hours apart.

#### The dependency, and the sequencing that follows

```
accumulator tree resolution stops depending on root-walking
  ├─ unblocks buffered-root ancestors    streamable-138/139   measured +2
  ├─ unblocks nested-dispatch parents    doe-0802             measured +1
  └─ probably unblocks others in the missing-parent family
```

**Five drivers in this engine have now been found missing a parent link — three fixed, two
blocked — and the blocker is a single assumption in accumulator lookup, not five separate
problems.**

> **Do the accumulator work FIRST and treat the parent fixes as its dependents**, rather than
> discovering the collision a third time.

That is worth more than the three cases it unblocks. It converts a scattered family of
symptom-filed defects into one named prerequisite with known dependents — and each dependent is
already measured, so the payoff is knowable before the work starts.

#### ATTEMPTED 2026-09-13 and stopped — what it narrowed, and what it ruled out

parsers2 attempted the prerequisite, reverted, and stopped. Clean tree, 1765 unit tests passing.
**The attempt narrows the problem considerably and contradicts their own hypothesis, which is the
useful part.**

**The boundary rule is expressible with no new bookkeeping.** Materialised subtrees carry
`Document = DocumentId.None`; streamed nodes carry a real id. So the distinction wanted —

> **a parent link is for NAVIGATION and is not a claim about which tree a node belongs to**

— can be stated as: when resolving tree identity, **stop climbing at a `None` → non-`None`
transition.**

**It was implemented at both resolution sites and did not fix `accumulator-080s`:**

| site | state |
|---|---|
| `FindDocumentForNode` | patched |
| the orphan walk in `EnsureAccumulatorsComputedAsync` | patched |
| `FindDocumentIdForInput` | **delegates to the first — not a third site** |

**That last row is the most valuable line here.** A third independent walk was expected and
**there isn't one**, so the remaining cause is **not another root-walking site** — which is
exactly where #74's per-call-site pattern would have sent the next person. **The pattern would
have misled here**, and the search space is smaller than it suggests.

**The most likely remaining explanation, unverified:** the parent link changes accumulator
**values** rather than tree identity — the accumulator walk now sees different structure.
Recorded as a hypothesis and explicitly **not** acted on.

#### The stopping rule, applied

> **Three build-measure-revert cycles in one area is enough to know it is not a patch.**

Two in #95's collision, one on the boundary rule. It wants someone starting fresh with the
accumulator machinery in front of them, **not another attempt appended to this one** — which is
#84's judgement about half-built work, applied to a sequence of attempts rather than a single
unfinished change.

#### Handover — what is worth carrying into that work

- **`doe-0802` is a missing-parent defect, not disable-output-escaping.** Measured **+1** in
  isolation.
- **`streamable-138/139` are the buffered-root version.** Measured **+2** in isolation.
- **Both are blocked by the same accumulator collision**, confirmed by two different detectors.
- **The `DocumentId.None` discriminator exists and the boundary compiles cleanly.** It is a
  correct-looking start that is **provably insufficient on its own** — which is more than anyone
  had before, and saves the next person from spending the same day proving it.
- **`FindDocumentIdForInput` delegates**, so the search space is narrower than the per-call-site
  pattern implies.

A negative result of that last kind is worth as much as the positive ones: it removes the most
obvious next place to look, which is where the register's own patterns would have pointed.

#### `doe-0802` was filed wrong all day, and the DIRECTORY misled

**It is not a disable-output-escaping defect at all.** It lives in the `disable-output-escaping`
directory, its output is double-escaped, and it was filed under d-o-e three times.

The actual cause: a child dispatched by `apply-templates` inside a matched template **has no
parent** — `name(..)` empty, `count(..)` 0, `root()` answering the child itself. So `match="doc/*"`
never fires, the built-in rule shallow-copies the source element, and **the escaping is a
downstream artifact of a matching failure.**

New twist on a familiar shape. #45, #85 and #88 record error *messages* pointing away from their
cause; here **the directory name did it** — a classification made by the corpus authors, inherited
without question, and wrong for this case. The engine's assertion (#94) is what finally pointed
at the truth.

#### The diagnostic conclusion, recorded because it is cheap and would have saved hours

> **In this streaming implementation, *"does this node know its parent?"* is the first question to
> ask of any wrong-output case, not the last.**

**Five for five so far.** Every missing-parent defect presented as something else — double
escaping, a wrong ancestor axis, dropped children, a bad `position()`, structure collapsing to
text. None presented as "this node has no parent".

### 96. INDEX — what is dammed behind the XQuery hold (2026-09-13) — HOLD LIFTED 2026-09-14

> **Lucas, 2026-09-14: "nothing is necessarily frozen so much as we're not going to continuously
> push nuget updates."** The constraint was always **release cadence**, not the repositories. The
> index below is therefore a work queue, not a dam.
>
> **parsers2 had applied it far more broadly than it was meant**, declining XQuery-side fixes
> outright and paying XSLT-side costs to avoid them — most visibly a **222-line `fn:serialize`
> override** written around a one-interface problem in XQuery (#97, xquery #31). Recorded because
> the failure was not in following the instruction but in **widening it**: "advance XSLT first" was
> read as "XQuery is frozen", and the gap between those two never got checked until it was raised.
>
> A hold that is never re-examined becomes a standing cost with no owner. The release process is
> what governs shipping; source changes do not need to wait on it.



Not a defect. A single place to see what the XSLT-first order is holding back, because the items
were scattered across six entries and nobody could answer *"what does lifting the hold buy?"*
without reading the whole file.

| item | where | cost of waiting |
|---|---|---|
| **Full-text stemming silently dropped** — see the correction below; **unreachable through the product**, blocking nobody | `phoenixmldb-xquery#29` | **none** — low priority when XQuery reopens |
| **UCA collation ignores `alternate=shifted`** — backs `fn:compare`, `fn:sort`, `xsl:sort`, `for-each-group`, key comparison | #64 | wrong sort order, no diagnostic, wide surface |
| **`fn:collection()` conflates declared-but-empty with not-found** — `FODC0002` where the empty sequence is due | #65 | a valid empty collection aborts a stylesheet |
| **Undeclared prefix reports `XPST0003`** where `XPST0081` is due | #65 | sends the reader to their syntax; the fix is a namespace declaration |
| **`current-output-uri#0` as a function item / in an inline body** — needs a dynamic-vs-direct signal on `QueryExecutionContext` | #69 | **a measured +5/−2 sits parked on it** |
| **`AxisNavigationOperator` names `TextNodeItem` to the user** | #84 | an engine-internal type in a user-facing diagnostic |
| **`count([()](1))` returns 1** — empty-array-member representation | #53 | array/sequence family |

**Two of these are silent wrong answers** (collation, `count`), **two are misleading
diagnostics**, and **none is currently blocking anyone.**

#### CORRECTION — the full-text bug is not a hold-decision item

Filed twenty minutes earlier as *blocking db-engine*. **It is not, and parsers2 corrected their
own routing.**

`FullTextAnalysisOptions.Default` sets `Stemming=true` with `Language=null`, and `GetAnalyzer`
falls through to a bare `StandardAnalyzer` for anything but `"en"`/`"english"` — so
`contains text "carpenters"` will not match "carpenter". Genuine, and:

> **The engine never constructs `FullTextAnalysisOptions.Default`.** `FullTextOptions.Language`
> defaults to `"en"` (`IndexDefinitions.cs:126`), so the real indexing pipeline always passes a
> language and both sides stem.

db-engine's failing test constructed `Default` directly, which **no product code path does**.
They have changed the test; nothing is red. **The XQuery hold costs nothing here, and no
exception needs requesting.**

#### A shape we have not catalogued: a defect no caller can reach

> **A silent wrong answer that is currently unreachable, because the only configuration that
> triggers it is one the product never builds.**

That is the **inverse of a hollow pass**. #69's family is *a test that passes because the feature
is absent*; this is **a defect that is absent because the caller is.** It becomes real the moment
someone adds a caller that uses `Default` — **which is exactly what a test did.**

#### And the way it was found cuts both ways

**A test constructed configuration the product never constructs.** It found a real defect nobody
can hit — and **by the same token would not have caught a defect in the configuration the product
actually uses.** The cost is symmetric and easy to miss: a test pointed at an unused code path is
not merely low-value, it is *actively looking away* from the one that ships.

db-engine's own diagnosis — *"a defect in the plan I wrote, not in your code"* — is the right
call and an unusually clean self-correction from a routing partner.

#### How a declined item should be handled, which parsers2 got right regardless

They **declined it and told db-engine why**, rather than leaving it queued in silence. That was
correct even though the item turned out not to block anything, because the alternative was
db-engine waiting on something nobody had said was not moving.

> A hold is only honest if the people behind it are told they are behind it.

Recorded because the same applies to anything else routed in while the order stands.

#### What this index is for

**Nothing here needs doing while the hold stands.** It exists so the hold can be *re-decided on
evidence* rather than by default — the question *"is the XSLT-first order still right?"* has an
answer that changes as this table grows, and it was previously unanswerable without reading the
whole register.

### 97. A check that validates what your method guarantees (2026-09-15)

Named after parsers2's squash silently reverted two of this register's own commits. The mechanism
matters less than the shape, which is one this file has now met from five directions.

**What happened.** The squash procedure soft-resets onto `origin/main` and re-commits, which is
correct **only while the branch's base still is `origin/main`.** Two docs commits landed while the
branch was in test, so the reset replayed an older tree onto newer main and committed the
difference **as deletions** — 29 lines of `BUGS.md`, 16 of `README.md`, 7 of the release
checklist, none related to the change being merged.

**Why the guard did not fire**, in parsers2's words:

> The tree-equality check can't catch this — **the tree is unchanged by construction**, which is
> exactly why it passed.

#### The shape

> **A check that validates the property your method already guarantees will always pass, and
> tells you nothing.**

Tree-equality is the *right* check for *"did the squash preserve my work?"* — and useless for
*"is my base still current?"* Two questions, one check, and the gap never showed because the
answer to the one being asked was always yes.

That is what distinguishes this from the rest of the family. #54's gate had no baseline row, #69's
tests passed because a feature was absent, #94's assertion never ran. **Here the check ran,
answered honestly, and answered a different question than the one that mattered** — closest to
#67's proxy predicate, but arrived at from the method rather than the data.

#### The control that works

The guard parsers2 already had on every other PR that day:

```
merge-base origin/main HEAD != origin/main   ->  rebase, never squash
```

Two notes on why it belongs somewhere durable rather than in per-job habit:

- *"It was in every other PR today"* is the signature of something that **is not a control.** A
  control that depends on remembering is a habit, and habits fail on the job you were rushing.
- **Main moving under a branch in test is the normal case here**, not bad luck — two sessions
  commit to these repos all day. The control has to *assume* it rather than treat it as an
  exception. Same conclusion as #76's sequence-not-vigilance, reached from the other side.

**Detection, for when it happens anyway:** check the squashed commit's **diffstat for files the
change never touched.** That is the cheap signal, and it is visible in the PR before merge.

#### Recovery

Forward fix, not force-push. The bad merge was already on `main` and may have been pulled; a
rewrite removes the record of what happened along with the mistake. The restore was a plain
`git checkout <good-sha> -- <files>`, gated so `src` and `tests` stayed identical. Verified from
both sides afterwards rather than assumed — the content, not the merge line.

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

*Assert that it ran at the expected SCALE, not just that it did not fail.* The sharpest form of
the entry below, and the one to reach for first. One skipped test and 428 skipped tests are both
green. A guard that never engaged and a guard that engaged and found nothing both print nothing.
A build that never picked up your change and a change with no effect both leave the numbers
unmoved. In every case the pass/fail is identical and only the COUNT distinguishes them.

So the cheap control is almost always a count or a positive control, not a stronger assertion:

| the question | the useless check | the check that works |
|---|---|---|
| did the suite run? | did it fail? | how many cases ran? |
| did strict mode engage? | did anything throw? | does a known-bad input throw? |
| did the rebuild take? | did it build? | does the DLL hash differ? |
| did the branch execute? | is the output right? | log the branch and count hits |

Each of these was needed on the same day, in tooling built by two people who were not
coordinating, and in several cases by whoever had just finished explaining the pattern to the
other. Naming a failure mode does not immunise you against it; only a control does.

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

---

### 99. FIXED on `fix/schematron-conformance` — namespaced name tests inside `some`/`every` selected nothing (2026-09-15)

Found by the phx-schematron conformance harness (ISO Schematron three-step compile of 77 state EMS
schematrons + 6 national, compared against Saxon-HE 12.10). Before this, **wrong accept/reject decisions**
came from AL.EMS.v350 and PA.EMS.v350/v351 state rules, plus national `nemSch_e077`.

**Symptom.** Inside an XPath quantified expression, a prefixed or EQName element name test (`nem:x`,
`Q{uri}x`) matched only no-namespace nodes: `some` returned false and `every` returned true for any data,
with no error. `*:x` and `local-name()` tests worked, as did `for`/`if` with the same paths. The standalone XQuery
engine was correct (`xquery` CLI 1.8.0 with `declare namespace`).

    # repro + literal Saxon/xquery4 table: phx-schematron/findings/quantified-expression-namespaced-name-test/
    some $x in ../nem:eSituation.10 satisfies true()     Saxon true   1.8.0 false
    every $x in ../nem:eSituation.10 satisfies false()   Saxon false  1.8.0 true

**Cause.** The three stylesheet-namespace walkers — `StylesheetParser.ResolveExpressionNamespaces`,
`DefaultXsltExecutionContext.ResolveExpressionNamespaceIds` and `ResolveExpressionNamespacesRuntime` — were
hand-written switches covering 18–20 of the 65 expression node kinds, with no `QuantifiedExpression` case (also
missing: switch, typeswitch, try/catch, map/array constructors, lookups, string constructors; castable/treat in
the runtime walker). `InlineFunctionExpression` was an earlier instance of the same gap.

**Fix.** All three subclass `PhoenixmlDb.XQuery.Ast.XQueryExpressionWalker` (shipped in XQuery 1.8.0) and
override only the name-bearing nodes, so traversal covers every node kind. One consequence caught by the unit
suite: a function name using an XQuery-predeclared prefix the stylesheet does not declare (`xs:double(...)` with no
`xmlns:xs`, inside a map constructor the old walker never reached) is now left to the XQuery layer, as before,
instead of raising XTSE0280.

---

### 100. FIXED on `fix/schematron-conformance` — templates and functions saw their caller's local variables (2026-09-15)

**Symptom.** A template invoked by apply-templates, call-template, next-match or apply-imports, and a stylesheet
function body, resolved `$v` to the invoker's local `$v` instead of the global (XSLT 3.0 §9.9 lexical scope).
The ISO Schematron skeleton compiles each rule to a template that declares its `sch:let`s and then applies templates
to children in the same mode, so a descendant rule read an ancestor rule's value for a schema-level default. That was
seen as wrong NEMSIS diagnostic content (OR.EMS.v351 `or_e142`). Harness micro-suite case 27 shows the
decision-bearing form.

    # repro: phx-schematron/findings/template-variable-dynamic-scoping/repro.xsl
    apply-templates / call-template / next-match from a template with local $v='LOCAL', global $v='GLOBAL'
    Saxon: GLOBAL ×3      1.8.0: LOCAL-IN-PARENT, LOCAL-IN-CALLER, LOCAL-BEFORE-NEXT-MATCH

**Cause.** `TryGetVariable` / `GetVariable` / the prefix fallback searched every scope on the stack, and
`EvaluateAsync` bound every scope on the stack into the XQuery context.

**Fix.** Scopes pushed by those five invocations carry `IsVariableBarrier`. Lookups and the `EvaluateAsync` binding
stop there and fall through to globals. Engine pseudo-variables (current-group, current-grouping-key,
current-merge-group/key, regex-groups) still pass through. Two follow-ups surfaced by measurement:

- **Conditions.** The first cut covered only the `GetVariable` fast path. A bare `$v` was right while
  `test="number(.) <= $v"` still leaked. Caught by harness case 27.
- **Forwarded with-params.** The W3C A/B then showed **insn/merge merge-096, merge-097, merge-097s failing
  (XPST0008 `$nodes`)**. Built-in template rules and the streamed dispatch forwarded with-params unevaluated and
  re-evaluated the select from deeper scopes. apply-templates, apply-imports and next-match now evaluate their
  with-params once, in the caller's scope, and forward values (`XsltWithParam.RuntimeValue`).

A side effect of `EvaluateAsync` no longer binding every scope of a deep call stack: GA.EMS.v350 compile step C1
went from 40s to 18.4s, output identical.

---

### 101. FIXED on `fix/schematron-conformance` — recursion past 1,200 frames truncated silently; call-template tail calls (2026-09-15)

**Symptom.** apply-templates, call-template, xsl:element and literal result elements **returned silently** at
`MaxRecursionDepth` (1,200), truncating output. The ISO skeleton's `sch-check:strip-strings` (in
`iso_dsdl_include.xsl`) walks each assert test one character per call. For tests longer than ~1,150 characters it
returned a truncated string, and `test-paren` printed `Bad assert: XPath syntax error. Unclosed parenthesis` for
balanced expressions: 403 asserts in 52 of 77 state schematrons. Recursive `xsl:function` raised `err:XSLT0000`
instead.

    # repro: phx-schematron/findings/named-template-recursion-depth/repro.xsl
    depth 5000, named template emitting then recursing:  Saxon 5000   1.8.0 1193 (no error)
    depth 5000, tail-recursive accumulator:              Saxon 5000   1.8.0 ""   (no error)

**What did not work, measured.** Raising the cap to 200,000 exhausted the native stack at 5,000 frames
(`XTDE0000`, identical with `DOTNET_DefaultStackSize=0x10000000`) and turned the GA include step into a hard
failure. Throwing at the cap without deeper recursion would have broken the compile of 52 schematrons.

**Fix.**

- **Tail calls.** An `xsl:call-template` that is the last instruction a template body (without `as`) executes —
  directly, or last in the chosen `xsl:choose` branch or `xsl:if` — is marked at parse time (`MarkTailCalls`).
  At run time `TryScheduleTailCallAsync` accepts it only when the top scope is exactly the scope the enclosing
  call-template frame pushed. It evaluates with-params in the caller's scope and captures the tunnel parameters the
  callee would inherit. `CallTemplateAsync` then runs the call as its next iteration with constant stack and depth.
  Frames that made the focus absent, or that capture an `as` result, run calls normally.
- **Cap exceeded is an error.** The silent returns now raise `XTDE0000`.

**Measured (Release CLI, 2026-09-15).**

| | 1.8.0 | this branch |
|---|---|---|
| GA C1 time | 40s | 5.4s (Saxon 0.7s) |
| false messages | 12 | 0 |
| 20,000-deep | truncated | correct, 1.2s |
| 70,000-deep | truncated | correct, 4.4s |
| 77 state schematrons, 3-step compile, sequential | 25.1 min | 5.8 min (Saxon ~1.8 min) |
| schematrons with false C1 messages | 52 | 0 (WY.EMS.v350's one message is a real malformed assert; Saxon prints it too) |

- `TailCallTemplateTests` 8/8, including 100,000-deep recursion.
- The remaining gap to Saxon is per-evaluation overhead: in a deep-recursion profile `EvaluateAsync` is ~91%
  inclusive, `QueryExecutionContext.PushContextItem` 28%, with-param evaluation 40%. **OPEN.**

---

### 102. FIXED on `fix/schematron-conformance` — `extension-element-prefixes` on `xsl:stylesheet` not excluded from literal result elements (2026-09-15)

**Symptom.** A namespace named in `extension-element-prefixes` on `xsl:stylesheet` was copied onto literal result
elements (XSLT 3.0 §11.1.3 excludes it). Every validator compiled from `iso_schematron_skeleton_for_saxon.xsl`
therefore carried `xmlns:exsl="http://exslt.org/common"`. That was the only difference between this engine's
compiled validators and Saxon's (and the customer's production `.xsl`) across all 77 state schematrons. The
harness's canonical diff ignored namespaces until this was found.

    # repro: phx-schematron/findings/extension-element-prefixes-not-excluded/
    lre.xsl → Saxon <out xmlns:keep="urn:keep"/>   1.8.0 <out xmlns:exsl="http://exslt.org/common" xmlns:keep="urn:keep"/>

**Cause.** `StylesheetParser.Declarations` stores stylesheet-level entries as namespace URIs, but the runtime
exclusion check looked the set up by prefix. The LRE parser also inherited only `exclude-result-prefixes` from
ancestors. **Fix:** match the URI (and `#default`), and inherit ancestor `extension-element-prefixes`.

---

### 103. Measurement for 99–102 (2026-09-15)

All against `c0fc41a`, same machine, Release conformance.

| | `c0fc41a` | `fix/schematron-conformance` |
|---|---|---|
| W3C XSLT 3.0 suite failures (`scripts/conformance.sh`, all chunks) | 360 | **358** |

- **Newly failing: 0.** Newly passing: `attr/as-0702`, `strm2/si-apply-templates-006`.
- **The intermediate snapshot (entries 99, 100 before the with-param follow-up, and 102)** measured 363:
  +3 in insn/merge. That is what exposed the forwarding defect described under 100.
- **`call-template-1002` / `call-template-1003` pass on both arms.** An older committed `conformance-results/insn.log` showed them
  failing; today's baseline does not. The suite recurses only to ~1,000, so no W3C case exercises tail calls at
  the depths the Schematron skeleton needs (~70,000); `TailCallTemplateTests` covers that.
- **Unit suite:** 1,811 tests, 0 failed (new: `SchematronSkeletonConformanceTests` 16, `TailCallTemplateTests` 8).
- **phx-schematron harness micro-suite:** 58/58; before, 55/58.
- **Harness corpus (950 NEMSIS national/state cases) with the lexical-scope, namespace and exclusion fixes:**
  950/950 Tier-A-clean, 0 compiled-validator differences (was 945/950 and 83 schemas differing).

---

### 104. A skip justified by a fact nobody checked — seven si-map cases (2026-09-15)

**Symptom.** `tests/strm/si-map` reported 5/5 for months. It has twelve cases.

**What was hidden.** `XsltConformanceTests.cs` carried `config.SkipTests.Add("si-map-001")` through
`-006` plus `-008`. `SkipTests` matches by **case name**, and the broken non-catalog copy
`strm/sf-map-new` declares the *same* twelve case names, so the seven skips suppressed both sets at once.
Five cases ran in each; a perfect 5/5 on the good catalog was the visible result.

**Cause.** The skip comment — mine — read: *"tests/strm/si-map's test-set references si-map-\*.xsl files the
suite ships under sf-map-new/ as sf-map-new-\*.xsl, so they fail on a missing file rather than on engine
behaviour."* Every clause of that is false for si-map. It references **no** per-case stylesheet:

```xml
<test-case name="si-map-001">
  <environment ref="si-map-A"/>            <!-- si-map-A.xsl — present in tests/strm/si-map/ -->
  <test><initial-template name="m-001"/></test>
```

Cases 001–009 are nine named templates (`m-001`..`m-009`) inside one shared stylesheet that exists.
The missing-file failures belong entirely to sf-map-new's copies. The comment generalised sf-map-new's
breakage onto a working catalog set and closed with *"si-map IS a catalog set, so these stay skipped"* —
a reason to keep hiding it, built on a premise never verified against the corpus.

**Shape.** Hollow pass (#69/#71/#72/#75/#78), with the aggravating factor from #89: the suppression was
load-bearing prose. A skip with no comment invites checking; a skip with a confident, specific,
wrong comment stops anyone looking. Corroborated independently by parsers2, whose run showed only
si-map-007 and -009 ever appearing in the logs.

**Fix:** seven skips removed. `sf-map-new` dropped from the harness (not a catalog set); `si-map` retained.
Two cases surface as failures and are pre-existing engine gaps, not regressions from this change —
si-map-005 is the map deemed-empty exemption from #118, and si-map-006 is its own defect — see below.

**Correction 2026-09-16: si-map-006 is NOT #117, and it is not a streaming defect at all.**
Instrumentation reports `streaming=False` — the case never takes the streaming path, so nothing in
that area could ever have fixed it. It is `xsl:map-entry` storing the key expression's **node**
instead of atomizing it (XSLT 3.0 §11.6), filed as phoenixmldb-xslt#124.

Four mechanisms were proposed for this one case across three sessions before the right one, and
**every proposal including both of mine was wrong**: "`m-006` has no `xsl:for-each`" (it has one);
"the streaming subscription scanner never descends into the map body" (it descends, and the case
isn't streaming); "the keys are relative paths atomized into keys, so the entries are built with
keys that aren't the atomized text" — mine, and false in both branches, since the entries ARE built
and the keys are NOT atomized at all; and "untypedAtomic compared against xs:string".

What it actually is, confirmed on two hosts:

    key-is-node="true"  key-is-element="true"  key-is-atomic="false"
    lookup-by-the-node="MISS"  lookup-by-string="MISS"

The key is an `element()`, and **the entry is unreachable by any key, including the node itself** —
so a map built this way silently drops what was put in it. `map:size()` is right and `map:keys()`
prints the expected strings, so every diagnostic a user reaches for says the map is fine.

That last fact is why the case beat four explanations: an unbuilt map and an unreachable-keyed map
are indistinguishable at the `xsl:if`, so anyone who probes the **lookup** concludes "empty map" and
stops. The instrumentation that settled it was the map's **size and key type**. When two failure
modes are identical at the assertion, the discriminator must be somewhere other than the
assertion — which is #106's rule applied to a probe rather than a comment.

Original text of this correction, retained because it was itself a wrong mechanism confidently
stated — the keys are not atomized:

```xml
<xsl:for-each select="BOOKLIST/BOOKS/ITEM[1]">
  <xsl:variable name="m" as="map(*)">
    <xsl:map>
      <xsl:map-entry key="AUTHOR" select="true()"/>   <!-- key is that ITEM's author string -->
```

looked up later as `$m('Jane Austen')`. So the entries are either not built during dispatch, or
built with keys that are not the atomized text. Different mechanism, different fix, and #117
measured no change on this case exactly as that predicts.

Left uncorrected, this is #106 in miniature: a citation that reads as a diagnosis and sends the
next person to a scanner that already descends. Note also that `$m('Jane Austen')` returns `()`
both for an empty map and for a map keyed wrongly, so the discriminator is the map's **size**, not
a lookup. Identified by the parsers2 session, who read `m-006` instead of the description of it.

**Rule this earns.** A skip must cite evidence that can be re-run, not a description of the corpus.
`ls` the directory the comment claims is broken before believing it — including when you wrote it.

---

### 105. OPEN — call-template-1003 gives three different verdicts on identical code (2026-09-16)

**Symptom.** `insn/call-template-1003` ("test tail recursion within a singleton `xsl:for-each`")
has produced **three** outcomes from the same commit, on two machines:

| where | outcome |
|---|---|
| this machine, sweep v2 | `Test 'call-template-1003' timed out after 10s` |
| this machine, sweep v3 (180s allowed) | **passed** |
| this machine, verification sweep (180s allowed, zero timeouts) | `XTDE0000: exhausted the execution stack` |
| parsers2's machine, six saved runs | `XTDE0000`-class failure, never a timeout |

The verification run is the one that settles it, because it had the extended timeout AND recorded
no timeout anywhere: the case ran to completion and failed.

```
XTDE0000: The transformation exhausted the execution stack before reaching the
recursion-depth limit (1200). It may contain unbounded recursion
```

**So it is not the clock.** The engine ran out of *stack*, and how much stack is available varies
run to run — thread, runtime state, what ran before it in the same host. A case whose verdict
depends on that is nondeterministic, and it will be whatever the machine felt like that morning.

**This corrects two earlier readings, one of them today's.** BUGS #103 saw a committed `insn.log`
with 1002/1003 failing while the run at hand passed, and reached for recursion depth — and was
closer to right than it was given credit for. I then found the 10s timeout, established it as the
cause of two *other* sets' drift, and extended it to this case as well. The timeout finding is
real and stands for `function-0701` and `sf-fold-left-021`, which pass at 180s every time. It is
the wrong explanation here, and the neatness of one mechanism covering all six cases is exactly
what made it attractive.

**Why it matters beyond one case.** The message says the stack was exhausted *before* reaching
the recursion-depth limit of 1200 — so the guard designed to produce a clean diagnostic never
fires, and the real limit is whatever the host stack happens to allow. `TailCallTemplateTests`
covers ~70,000-deep tail calls and passes, so tail-call elimination works somewhere; it is not
reliably applying on this path. That is the thing to investigate — not the timeout, and not the
1200 limit.

**Consequence for the gate.** `insn/call-template` stays at its floor of 37. Lucas set it,
#119 deliberately held it, and it now has a better justification than "the set drifts": the set
contains a case that returns different verdicts for the same code. Do not raise it while that is
true — a baseline of 39 or 40 is a coin toss, and the floor is what stopped this being noticed
the first two times.

Found by the parsers2 session refusing to accept the timeout explanation for this case after it
had been accepted for the other five, and asking for the failure text.

**Fourth verdict, 2026-09-16.** On the *other* host, from the `fix/streamed-map-scanner` branch —
which touches only `StreamingExpressionScanner.cs` and cannot reach `insn/call-template` — 1003
**passed**, where the same host's A run had it failing. So it now disagrees with itself across
runs on one host, across hosts, and across two branches that cannot affect it. Four observations,
three of them contradicting whichever was read first. It was spotted because the branch's only
apparent "gain" was arithmetically impossible for what it touched — a case-name diff caught what a
per-set total would have banked as +1.


---

### 106. The shape our mistakes take: a claim standing where a check belongs (2026-09-16)

Four defects found in one day, by two sessions, looked unrelated. They are one shape.

| | the claim | what it displaced |
|---|---|---|
| #104 | a skip comment describing why si-map's stylesheets were missing | `ls` on the directory — they were not missing |
| the timeout gate | `timeouts=0` printed by a check whose own error was its passing value | any audit of the gate |
| `SlowTests` | "these pass given the time", asserted over six cases | measuring; it was true of two |
| #118's map exemption | a streamed map is exempt from deemed-empty *because it produces nothing* | noticing that si-coco-014 then passed whether the map had six entries or none |

Each is **a statement that reads as verified, occupying the exact spot where someone would
otherwise verify.** The wrong claim is not the damage. The damage is that the claim is
load-bearing for the decision not to look: a skip with no comment gets checked, a skip with a
confident and specific comment does not. A gate that prints nothing gets audited; one that prints
`timeouts=0` does not. An exemption with a stated rationale is not re-derived when the rationale
expires.

This is the same family as the hollow passes (#69/#71/#72/#75/#78) and the checks that validate
what the method guarantees (#97), but sharper about the mechanism: those entries describe a check
that cannot fail; this one describes **prose that prevents the check from being written**. #89's
rule — grep this file before reporting a finding as new — is the countermeasure pointed at the
register. This entry is the same rule pointed at comments.

**What actually caught all four, and neither was a better assumption:**

- **A counterfactual.** Does this test fail without the fix? Five of seven in
  `ShadowAttributeStaticValueTests` do; the two that do not are labelled guards, not coverage. The
  same question applied to #118's exemption — does si-coco-014 fail if the map is empty? — would
  have found that one the day it was written.
- **A second observer on different hardware.** Six timeouts on one host, zero in ten runs on the
  other. The one with clean numbers gets no warning and is therefore the one who believes them.

Neither scales to everything. Both work by making a claim answer for itself rather than by
doubting it harder. When a comment is doing real work — justifying a skip, an exemption, a
timeout, a floor — the cheap move is to write down what would falsify it, and then spend the two
minutes.

**The sharper form, arrived at after the fourth instance: the bound was always stated from ONE KIND
of evidence.** Not "one probe short" — that describes the remedy, not the mistake. Every time, the
evidence gathered was several samples of a single kind, and the conclusion ranged over kinds that
were never sampled:

| claim | sampled | generalised to |
|---|---|---|
| "si-map's stylesheets are missing" | a description of the corpus | the corpus |
| "timeouts=0" | the gate's output | whether the gate works |
| "these six pass given the time" | two cases | six |
| "nothing can read a node-keyed map entry" | four **lookup** forms | all access paths — `map:for-each` reads it fine |
| "+6 and +2 means +8" | two separate measurements | their combination |
| "#118's exemption is still needed" | the case it was written for | the premise it rested on |

Four of those were checked by more than one sample, which is exactly what made them feel checked.
Sampling harder inside one kind does not help.

**The practice that does:** enumerate the *kinds* first and probe one of each. For a data structure
that means keyed access, iteration, serialization, type checks, equality. For a claim about a test
suite: does it run, does it fail without the fix, does it fail for the stated reason, does it pass
on other hardware. For an arithmetic claim about two changes: measure the combination, never add.
One probe per kind beats five probes of one kind, and it is usually cheaper.
(parsers2's formulation, after retracting the node-keyed-map bound on #124.)

**The maintenance form, for claims that are true when written.** #118's exemption is the one that
rotted rather than being born wrong, and a counterfactual at authoring time would have passed. What
that case needs instead: **an exemption must name the condition that retires it, in a form someone
can evaluate.** #118 said "waits on that fix" and cited #117 — close, but nobody re-runs a
citation. The evaluable form is a test: *"remove when a streamed `xsl:map` yields entries — check
with si-coco-014 asserting count AND size."* The first is a note; the second fails when the
premise expires. (parsers2's formulation.)

**And a caveat on the evidence for that rule, from the case that appears to prove it.** #118's
exemption *did* retire the same evening, on exactly the condition above: `fix/streamed-map-scanner`
plus the exemption removal measured **+2 with si-coco-014 holding** — coco-013 and si-coco-013
newly passing, nothing newly failing, and si-coco-014 now green because its map has six entries
rather than because an empty map was being kept alive. A clean demonstration.

Except the note that actually sat in the code said *"waits on that fix"* and cited #117. It did not
fire. The exemption was re-examined because two sessions happened to be discussing expiry
conditions at the time — the evaluable form was written *afterwards*, and then matched. So what was
demonstrated is that the rule **describes** the retirement correctly, not that the artifact would
have **caused** it. The countermeasure has not yet been tested in the only condition that matters:
a note left alone for months with nobody thinking about expiry.

Recorded because the opposite conclusion is the attractive one and would be this entry's own sixth
instance — a rule that reads as validated, standing where the validation belongs. (Distinction
volunteered by parsers2, against their own result.)

A fifth instance, same day, smaller: this register attributed si-map-006 to #117 on the strength of
a description of the case rather than the case — see the correction in #104. A citation that reads
as a diagnosis is the same object as a comment that reads as a check.
---

### 107. A check that cannot fail is worse than no check (2026-09-16)

#106 is about a *claim* standing where a check belongs. This is the closer cousin: a check that
**runs**, reports success, and could never have reported anything else. The two are the same
object seen from either side, and #97 — a check that validates what the method already guarantees
— is the special case where the tautology is in the assertion rather than the plumbing.

**Why it is worse than no check.** An absent check is visibly absent; someone eventually asks
whether the thing was verified. A check that always passes is *indistinguishable from a passing
one*, and it actively consumes the attention that would have gone to verifying. It converts "not
yet checked" into "checked" without touching the underlying question.

Seven of these were found in a single day by one session, all while chasing two real defects
(parsers2, on the SchXslt2 and DocBook reports):

- output redirected to `/dev/null`, so the comparison compared nothing
- `find -xdev` that never crossed the mount the files were actually on
- a grep written for double-quoted attributes, against single-quoted source
- an instrumentation insert that landed outside the scope it was meant to observe
- **two builds that failed while `--no-build` ran the stale binaries** — a green test run of code
  that was never compiled
- a diagnostic printing `LocalName` for two globals that differ only by prefix, so it printed the
  same name twice and looked like a duplicate
- a timeout gate whose own `bc: command not found` became its passing value (see #106)

And two from this session in the same window: a `SlowTests` justification measured for two cases
and asserted over six, and an unwrap guard testing `is List<object?>` where the sequence type is
`object?[]` — so `key="(1,2)"` sailed past a guard written precisely to catch it, and the patch
would have shipped with a guard that never fires.

**The pattern in the failures themselves:** every one is a plumbing defect, not a logic defect.
Nobody wrote a wrong assertion. The assertion never received the data — wrong file, wrong quoting,
wrong scope, wrong binary, wrong stream. Reviewing the *assertion* finds none of these.

**The exception, and it is a different axis entirely: a check can be correct end-to-end and still
unable to fail, because the input contains no instance of the thing.** Correct assertion, correct
plumbing, and a dataset with nothing in it to catch. Nothing is broken anywhere.

This defeats every remedy above. Making the check fail on purpose proves the *instrument* works —
and it still cannot fail on *that corpus*. Proving it can report non-zero on the same inputs in the
same run proves it can see what is there, not that what matters is there. So it needs its own
question, and the question is not about the check at all:

> **Does this input contain the thing? Does this corpus contain the shape, does this suite contain
> a test for it, could this run have failed at all?**

Three instances today, all found by asking it rather than by anything going wrong:

- **#133's corpus gate.** 33,464 `xsl:variable`/`xsl:param` declarations in the Schematron path,
  **zero** with an `as=` attribute — so a 950-case run never constructs a typed variable and cannot
  exercise the defect. A green row there means "no regression" and nothing more. (Strengthened
  further: 0 of 77 use `@documents`, so the one typed declaration never reaches a compiled
  validator.)
- **The #129 and #132 conformance A/Bs.** Byte-identical 334/334 failure sets on both arms. That
  proves nothing broke; it proves nothing whatever about whether either fix *works*, because the
  W3C suite contains no test for either shape. Both were validated by other means — a real corpus
  and a Saxon reference verdict — and the sweeps were reported as "nothing broke", which is the
  honest reading and not the one a byte-identical result usually gets.
- **The obvious test for #133**, which is the sharpest form. Assert the bound value; assert
  XTTE0570. Both are structurally unable to fail on that defect's worst mode, because the worst
  mode *is* a value that binds cleanly — to the empty sequence, so `empty()` is true, `xsl:if`
  takes the other branch, `for-each` never runs its body, and nothing is raised. A well-written,
  correctly plumbed test that cannot fail on the failure it exists for. The fix for the test is to
  assert the **downstream effect**, not the binding.

**So "we ran it against the real corpus" is a claim needing the same treatment as any other.** It
sounds like the end of an argument and is the start of one. (Direction identified by the
schematron session, who asked it about their own corpus unprompted, and by parsers2.)

**A third kind: the data arrives intact and is mangled in transit, so the verdict is computed from
real values assembled wrongly.** Not "never received the data" and not "the data contains no
instance" — the values are right and the *aggregation* is wrong.

The instance: a blast-radius table built by accumulating probe results into a `|`-delimited string,
where the probe's own output contained `|`. Fields shifted, and the comparison ended up comparing
two fields **of the same run** against each other, printing five spurious "MOVED" verdicts. The
tell was on the same screen — a row reading `v2.0.0=[1] d89=[7] main=[1]`, where `7` is a string
value and `1` is a count. Two different quantities, formatted identically, compared as if they were
the same measurement.

This one is nastier than a plumbing defect because **every individual value in the output is
correct**. Nothing looks wrong on inspection; only the alignment is wrong, and alignment is what a
comparison *is*. Checking the inputs verifies them; checking the verdict verifies it; neither
catches the join.

Remedy, and it differs from the others: **compare artifacts, not parsed values.** `cmp` or `diff`
on separate files per arm, rather than parsing values into a shared in-memory string and comparing
fields. A delimiter that can appear in the data is not a delimiter. (parsers2, who also nearly
filed two harness artifacts as engine defects from the same run — an `XPTY0004` raised by their own
probe calling `string()` on a two-item sequence.)

---

**One roof over all three kinds** (schematron's formulation, and it holds against every instance in
this entry):

> **Any token a check relies on to be structural — delimiter, prefix, zero, denominator — must be
> shown not to occur as content, in the same run.**

Checked against each:

| token treated as structural | occurred as content | instance |
|---|---|---|
| `\|` as a field separator | `\|` inside a probe's own output | the blast-radius table |
| `w/` `b/` as path markers | the same prefixes inside file *contents* | the branch-vs-patch comparison |
| `0` as "absent" | `0` as "the glob matched nothing" | `timeouts: 0`, and the corpus scan |
| a count as a denominator | a count from a different run or file set | capability proven elsewhere |

The three kinds are then three ways the same confusion lands: the token is misread in **transit**
(delimiter collision), the token is never **produced** because the plumbing diverted the data
(`/dev/null`, stale binary, out-of-scope insert), or the token is genuinely absent because the
**input has no instance** of the thing (the corpus with no `as=` attributes). In each case the check
reports a structurally valid answer that means something other than what it appears to mean.

**A fourth kind, and the most dangerous: the check receives real, correct, well-formed data from
the WRONG INSTANCE.** Not "never received the data", not "the input has no instance", not "mangled
in transit" — everything is genuine, and it belongs to a different event.

Two traps from one diagnosis (schematron and parsers2, chasing a RED corpus row on #135):

- **Re-running to diagnose destroys the evidence.** `--no-cache` skips the cache *read* and still
  does `Directory.Delete(dir, recursive: true)` before recompiling. So the instinctive response to
  an anomaly — run it again — deletes the artifact that would have explained it. **Capture first,
  re-run second.** The only copy of the corrupt validator was lost this way.
- **A capture that fires is not a capture of the right thing.** The preservation hook worked. It
  reported success and preserved a genuinely corrupt artifact — whose mtime was the *previous
  day*, an incomplete cache directory from an unrelated failure. The artifact was real, the
  corruption was real, and the signature matched the hypothesis exactly. It was still the wrong
  file, and it would have been completely convincing.

That second one has no tell at any level: nothing is malformed, nothing is empty, no count is
wrong. The only discriminator was a **timestamp nobody would think to print**, and it arrived at
the last step of a long investigation when everyone wanted an answer — which is when scrutiny is
lowest and a matching signature is most welcome.

**A fifth variant: the change is correct, and it makes a previously-unreachable interaction
reachable.** #137 merged imported `xsl:strip-space`/`xsl:preserve-space` declarations that had been
silently dropped. Correct fix. It also made a long-correct conflict check start comparing
declarations *across import precedences* — which §4.4 resolves by precedence and is not an error —
so a legal stylesheet was rejected with a spurious XTSE0270. **The check had been safe because
imported declarations never arrived, and nothing recorded that dependency.**

It passed five instruments: a byte-identical conformance A/B, six purpose-written tests, a positive
control on real output, and schematron's full fast gate. **Every one asked "did this change do what
I intended". None asked "what does this change now make possible".** Those are different questions
and only the first has an obvious test. The second is answered by looking at what consumes the
thing you just started producing.

> > **Evidence must be shown to belong to the event it is offered as evidence of.**

Same family as the zero rule and the delimiter rule: a structural property — *this artifact is from
this run* — assumed rather than demonstrated. Print the timestamp, the run id, the commit; a
provenance field is to an artifact what a denominator is to a count.

**And a note on normalisers, which failed in both directions in one day.** Under-matching fabricated
differences twice, once in the very pattern written to fix the first. Over-matching silently erased
85 pure-decimal NEMSIS code values from *both sides* of a comparison — invisible, because deleting
the same thing from both arms leaves them equal. **Under-match is loud and costs an investigation;
over-match is silent and costs the finding.** The over-match was caught only because the under-match
screamed and prompted an audit, which is not a detection method.

Which yields the one-line version of this whole entry, and it is a question rather than a rule:
**what would this check have printed if the thing it looks for were absent — and is that
distinguishable from what it just printed?**

**One prospective use out of three opportunities, 2026-09-16 — and the two misses were the same
sentence in the same words.** #135
turns four silent wrong-cardinality bindings into XTTE0570. Its author expected the conformance
A/B to move, since the suite tests typed variables. It came back byte-identical, 334/334. Instead
of taking that as "no regressions", they asked which kind of zero it was:

    files mentioning XTTE0570 ............................... 14
    of those, binding a typed ExactlyOne/ZeroOrOne variable
      whose BODY produces more than one item ................  0
    search proven non-blind: XTTE0505 = 15, XTSE0010 = 104

The suite contains **no instance of the shape**. So the A/B proves nothing broke and cannot speak
to whether XTTE0570 is the right error — and the schematron corpus cannot either, for the same
reason on its side. **Two green gates, zero correctness evidence between them**, which side by side
in a PR would have read as validation. Note the third line: the search was made to prove it could
see before its zero was believed, per the rule above. The error choice was then justified on
§9.3 and on `Variables.cs:842-847` already shipping XTTE0570 for this condition — an established
convention propagated, not a new one invented.

**The correction that matters more than the success.** The same claim — *"the W3C suite has no test
for this shape"* — appears in the #129 and #132 PR bodies, **asserted and never measured**, in
merged work. Measured afterwards it is not obviously true: 20 functions with an atomic return type
and an `xsl:value-of` body, 3 of which could yield the zero-length string; 35 files where a typed
variable's body references a content-bound global (non-blindness: 516 files with `<xsl:function>`,
3,772 with `<xsl:value-of>`). Over-inclusive, none confirmed to hit the exact trigger — but not
zero, and **"I did not find it" is not "it is absent"**. Corrections are posted on both PRs; what
survives is the narrower true statement, *no conformance case changed outcome*. Neither fix rested
on the sweep anyway — both rested on a real-stylesheet reproduction, a Saxon reference verdict, and
the schematron corpus.

So the habit fired **once, on the third try, and only because movement was predicted and did not
arrive**. Recording the prospective use without the two misses would overstate how reliably it
fires — and the misses are the more instructive half, because that sentence is the kind that makes
a PR look rigorous and therefore goes unchecked.

It also travelled. This session repeated the unmeasured claim to the user as fact, in a summary of
why two green gates proved little. **Writing an assertion down is what makes it evidence for
everyone downstream**, and nothing in the pipeline between "plausible sentence in a PR body" and
"stated as established" asks whether it was measured. So the reconciliation rule extends from
counts to claims: **if an assertion is checkable, check it before writing it down.** The
`1,864 vs 1,874` arithmetic caught a destroyed file within minutes; two false sentences in merged
PRs were caught by nothing except someone going back to look.

**And one more instance, with a tell worth naming: structural information inside a success
message.** A `Write` intended to create a test file overwrote an existing one and destroyed ten
tests. The write reported success — it *had* succeeded. The tool's response distinguishes
"updated" from "created", and that word is the only difference between the intended operation and
a destructive one. Caught downstream by a suite total that would not reconcile: 1,864 where
baseline + 11 demanded 1,874.

Generalising: a success message often carries a field that says *which* success this was, and it
is easy to read the status and not the field. Same family as `timeouts: 0` — the value is correct,
and the thing that distinguishes the two meanings is sitting right next to it, unread. If a total
is knowable in advance, compute it and reconcile; arithmetic that refuses to balance is the
cheapest detector on this page.

**What works, and it is cheap:** make the check fail once on purpose. Break the thing it is
supposed to catch and confirm it goes red. That is the counterfactual from #106 applied to the
instrument rather than the claim, and it is the only technique on this list that catches all nine
instances above. A check never observed failing has not been tested; it has been run.

**The counterfactual has its own version of this failure, and it is easy to hit.** A reverted tree
that does not **compile** produces failing tests for the wrong reason: "3 of 10 failed" then means
"nothing ran", and the counterfactual certifies itself. The model, from #139:

- revert the source **and** any test file the fix's API change touched, together — otherwise the
  test project will not build against the old signatures
- **confirm the unfixed tree builds, exit 0**, before reading a single test result
- state both halves: **3 of 10 inverted** (the ones predicted), and **7 passed unfixed** — the
  second number matters, because a failure among those would mean something already correct had
  been broken rather than something broken fixed
- verify the restore afterwards: marker back, `git status` empty, diff against the commit empty

A counterfactual that reports failures without proving the tree compiled is the same object as a
green test run of a stale binary — the `--no-build` trap, arriving from the opposite direction.

**The inverse, and it is worse: a test that asserts the defect.** #137's own test encoded the
behaviour #139 then had to fix, so the bug was *pinned* by a passing test. This is not a check that
cannot fail — it fails precisely when the code becomes correct, which turns a fix into an apparent
regression and creates pressure to revert it. The tell is a test written from observed output
rather than from the specification: it will always pass on the day it is written. Corrected in
#141, together with the two justification comments that had explained the wrong behaviour as
intended.

Corollary for anything printing a count: **state the count even when it is zero**, and assert the
input was non-empty before trusting it. `timeouts: 0` from an empty glob and `timeouts: 0` from a
clean run are the same three characters.

**Why these cluster in instrumentation rather than in shipped code.** Instrumentation is written
once, read never, and trusted immediately. Shipped code is reviewed, exercised by tests, and
revisited; a probe is believed the moment it prints something. That is also why the cost lands on
whoever is furthest into an investigation and least inclined to doubt their own tools.
(parsers2's observation.)

**A check that reports zero must first demonstrate it can report non-zero — on the same inputs, in
the same run.** The tenth instance today, and the only one caught *before* it misled anyone — which is the order this whole entry is
supposed to run in, achieved once in ten.

Schematron scanned their corpus for the typed-variable shape in #133 and got "none". They declined
to believe it, because the same scan reported no atomic declarations and no counts of anything:
**a scan reporting zero of everything is indistinguishable from a blind scan.** So they made the
instrument prove it could see — 33,464 `xsl:variable`/`xsl:param` declarations counted across the
same files — and only then trusted the zero.

This generalises the `logs=11 timeouts=0` corollary past counters to **any check whose interesting
answer is an absence**: "no matches", "no diffs", "no regressions", "not present in the corpus".
Each is indistinguishable from a broken search. The remedy is to make the instrument produce a
known non-zero on data you control, then read its zero.

And that zero had teeth, which is the part worth carrying: 33,464 declarations with zero `as=`
attributes means their 950-case corpus **never constructs a typed variable**, so it cannot exercise
#133's defect at all. A green row there means "no regression" and nothing more. Believed without
proof, it would have read exactly like validation — a corpus gate incapable of gating the thing it
was being run for, which is #97 arriving from the data side rather than the assertion side.
**The "same inputs, same run" clause is load-bearing, and its absence has already bitten.** A
branch-vs-patch comparison worked perfectly on file *contents* and was blind to `w/` versus `b/`
path prefixes — so it failed only on paths. Any capability demonstration that did not happen to
exercise paths, or that ran yesterday, or that ran against a different file set, would have
certified a blind check as seeing. **Capability proven elsewhere is not capability proven here**,
which is the same structure as `logs=11 timeouts=0`: the denominator has to come from the same
place as the numerator or it is not a denominator for it.

Without the clause, two of the nine instances above survive their own remedy: `find -xdev` would
have found files on `/` and never proven it could cross onto the mount, and the single-quote
`grep` would have matched double-quoted attributes on any other page and never proven it could see
this one. Demonstrating on a toy file and then trusting the zero on the real corpus is the exact
loophole. (parsers2 and the schematron session; tightened after the looser form had been
written down.)

**Totals across a harness change are not comparable, in either direction.** #123 moved the set
list to 260 and the denominator to 10,672 -> 10,839, adding `fn/system-property-gen`'s 166 failing
cases alone. The same engine reports **334** failures on the old list and **493** on the new one,
and the second was nearly written up as a regression against the first. Only a within-run A-vs-B
diff carries meaning across such a change. The better signal that the harness behaved as described
was that two *predicted* per-set gains arrived exactly — `si-map` 10->11 and `type/maps` 48->49 from
the map-key fix — which a total could never have shown.

**And a defect in the timeout work recorded above, found by the labelling it shipped with.** Adding
six named cases to `SlowTests` does not converge: on the next run the three timeouts were
`sf-fold-left-016`, `si-value-of-016` and `sx-gc-gt-121` — **all new**, none on the list. A named
allow-list can only ever cover cases that have already timed out, and under load the timeout lands
wherever it lands. The labelling change works and made all three identifiable in seconds; the
`SlowTests` change treats a symptom that moves. A load-independent mechanism is needed — scale the
cap to measured machine speed, or have the gate treat a timeout as *not a measurement* rather than
as a failure, which is what it actually is. Until then, "green except some timeouts" will be the
normal state of a run on a slow host, and that is exactly how a gate stops being believed.

**Measured across hosts, which decides between the options.** The same three cases on
`mechapaluker` (32 cores, load 0.05): **0 timeouts across 12 sweeps and 4,326 FAILED lines**, with
the per-set TSV showing all three sets visited every time — `sf-fold-left` 20/20,
`sx-GeneralComp-gt` 52/52, `si-value-of` 35/39 with four failures, none of them `si-value-of-016`.
Controls, because a zero without them is worth nothing: the search was proven non-blind
(`math-3701`, known to fail there, returns 12 hits from the identical glob), and the label was
proven emittable (19 sites print it). Limits stated by the measurer: ten of the twelve sweeps
predate the label, so the *label*-based zero covers only the last two — the *name*-based count
covers all twelve, and a timeout lands in the FAILED list by name in every harness version, which
is the leg that carries.

So **the cases are not intrinsically slow**; the timeout lands where load puts it. That datum
survives both load-independent options and is the one it argues against: an allow-list can only
enumerate what has already timed out *somewhere*, and "somewhere" is a property of a host and a
moment, not of a case.

Worth recording the near-miss too, because it is this entry's own subject: the measurement was
almost sent annotated *"zero mentions = ran and passed"*, which the logs cannot support — they
never name passing cases. Absence from a failure log means "did not fail **or** did not run". The
per-set TSV, which records every set visited regardless of outcome, is what distinguishes the two.
A denominator again, in a third costume.

**A rule of its own, because it is the one most likely to ship: never pass `--no-build` in the
same breath as a build whose exit status you did not check.** A failed build followed by
`--no-build` gives a perfectly green test run of a binary compiled before the change existed.
parsers2 hit it twice in one day and was saved both times only by grepping the build output for
` error ` rather than for success. This session hit it too: after a CS0136 name collision, a repro
matrix ran clean against stale binaries and the results were read as real until the build log was
checked. The failure is silent on both sides — the build's error scrolls past, and the test run
has nothing to complain about.

---

### 108. assert-xml cannot see whitespace, and what that does to the published figure (2026-09-16)

**Mechanism, verified in both halves rather than read.** `VerifyXmlAsync` parses both sides with
`XDocument.Parse` at default `LoadOptions.None`, which discards whitespace-only text at parse time:

```
XDocument.Parse("<a><x>   </x></a>")                        -> x has 0 child nodes
XDocument.Parse(..., LoadOptions.PreserveWhitespace)        -> x has 1
```

That alone is not enough — `DeepEquals` on `<x>   </x>` vs `<x/>` after the default parse is still
`False`, because one element is `IsEmpty` and the other is not. `NormalizeEmptyElements` supplies
the second half: it sets `elem.Value = ""` on every element with no nodes, which after the
discarding parse includes elements that *had* whitespace. Together, whitespace differences are
invisible to every `assert-xml`.

**Proof it matters:** `strip-space-020` and `-027` report PASSED while the engine emits
`<abc:x>   </abc:x>` where `<abc:x/>` is required (printed from inside the fixture; the instrument
was proven non-blind in the same run — 387 comparisons recorded, 5 False / 382 True).

**Scope, and the first figure was wrong by ~8x in the dangerous direction.** Over 4,567 `assert-xml`
assertions, measured independently by two sessions:

| shape | count | reading |
|---|---|---|
| `<x>   </x>`, `<a/> <b/>` — **same line**, inside the root | **~22-24** (11-13 sets) | **genuinely unchecked** |
| `<x>\n  </x>` — true whitespace-only leaf | 19 (both sessions agree exactly) | ambiguous; excluded from the figure |
| `<a/>\n  <b/>`, `<out>\n  <a/>` — container | ~142-202 | **indentation** |

The initial 189 was dominated by the last row. That row is not a gap: **a comparison sensitive to
indentation would be wrong**, because no two serializers format identically and it would fail
constantly for no reason. Counting it as exposure implies a harness change that should never be
made — which is why an overstatement in that direction is worse than an understatement.

The 19 ambiguous leaves are left *out* of the exposure figure and said so explicitly, rather than
filed silently under "indentation". They live in `attr/match`, `attr/select`, `expr/expression`,
`insn/construct-node`, `insn/copy`, `insn/number`, `misc/xml-version` — **`decl/strip-space` is not
among them**, so the proof above does not depend on resolving them.

**What it does to the published figure:** at most ~20 of the passing cases are unchecked for
whitespace. That is a caveat worth one line next to the number, not a hole in it. **Do not "fix"
the harness alone** — making the comparison whitespace-sensitive reclassifies passes and moves the
baseline, and until the engine's import-precedence gap (#139) lands it would turn
`strip-space-020/-027` red for the wrong reason.

**Measured: a whitespace-sensitive comparison cannot be a global setting.** Scratch branch,
`LoadOptions.PreserveWhitespace` on both parses, both arms on the same commit, `decl` chunk:

    control    1032/1122   90 failed
    treatment  1019/1122  103 failed        13 newly failing

Of the 13, **7 are false failures from indentation** — `use-package-170`..`-173` and three
`accumulator` cases, whose actual output is correct and merely pretty-printed — in a chunk whose
whitespace exposure was estimated near zero. Across eleven chunks that is on the order of a hundred
false failures bought for ~20 real checks. There is no general discriminator available:
`<a/>\n  <b/>` is structurally identical whether it is meaningful whitespace or formatting, which is
presumably why the comparison discards it.

(`NormalizeEmptyElements` turned out not to need disabling: with whitespace preserved the element
has a node, so `!elem.Nodes().Any()` is false and the normaliser skips it. A caveat that dissolved
on measurement rather than needing to be wired around.)

**So the proposal is per-set opt-in** — make whitespace-sensitivity a property of the ~9-11 sets
that genuinely assert it, and leave the global comparison alone. That buys the real checks without
the false failures and makes the assumption explicit per set instead of implicit everywhere.

**And the same run produced the first conformance-level check of #141.** Under whitespace-sensitive
comparison on a tree containing that fix, `decl/strip-space` scores **25/27 with the two failures
being `strip-space-019` and `-022`** — so `strip-space-020` and `-027` *pass*. Established with a
denominator, not from absence: the log records `Running 27 tests from ...strip-space`. Before #141
those two emitted `<abc:x>   </abc:x>`, which a whitespace-sensitive comparison rejects. Under the
normal harness they pass either way, so the suite cannot currently guard that fix — **opting
`decl/strip-space` in would make the set able to catch a regression of #141**, which is the
strongest argument for the opt-in.

**Method note, which is the transferable part.** Two independent measurements disagreed (189 vs 23),
and reconciling them found what neither had alone: the broader count surfaced a bucket the narrower
one would have excluded silently, and the narrower one explained what that bucket contained — a
discriminator keying "leaf" off the character after `<`, which counts a parent's leading text and is
indentation by construction. Disagreeing measurements that get reconciled are worth more than
agreeing ones.

**And the sharper half, which cost the rule its first clause.** Both sessions independently reported
the denominator as **4,567 assertions** and treated the match as corroboration. Parsing the catalogs
as XML instead of pattern-matching them gives:

    total <assert-xml> elements  5,096
      with file="..."              539   <- expected value in an EXTERNAL FILE
      with inline content        4,555
    unparseable catalog files       21

So 4,567 was approximately the *inline* count, and **539 assertions whose expected value lives in a
separate file were opened by neither of us.** The two measurements agreed because they shared a
method — a regex over `<assert-xml>…</assert-xml>` — and therefore shared its blind spot. Checking
those 539 adds only ~2 to the exposure, so the headline survives; the lesson does not.

**Agreement between measurements is worth exactly as much as the independence of their methods.**
Two people running the same technique on the same data will agree whether or not it is right, and
the agreement feels like evidence. Vary the *method*, not just the operator: parse where you
pattern-matched, count where you sampled. (Found here by a third party's passing mention of
`assert-xml file="…"` — nothing in either measurement could have surfaced it, because the blind
spot was in what both looked at.)

---

### 109. A check whose result is discarded hides the bug in whatever computed it (2026-09-18)

`xsl:expose` had two defects, and neither was visible while the other stood.

1. A token in `@names` that matched no component **did nothing**, silently, instead of raising
   XTSE3020.
2. `MatchesExposePattern` understood two of the four token forms §3.6.3.1 defines — `*` and
   `prefix:*` — and not `*:local` or any `Q{uri}…` form, which therefore matched nothing.

Defect 2 had **no symptom**, and could not have one, *because of defect 1*: a token that matched
nothing was ignored, so a matcher that wrongly found nothing produced the same observable behaviour
as one that correctly found nothing. The bug was in the value of an expression whose value was
thrown away.

Adding the XTSE3020 check surfaced it instantly and unmistakably — every package using `*:a2`
started reporting "matches no attribute-set" — but only *after* the first fix was written. Looking
for defect 2 first would have found nothing to look at.

> **A discarded result cannot be wrong.** When a computation feeds nothing that is observed, no test
> of the observable behaviour constrains it, and it is free to drift. The way in is not to test
> harder; it is to make the result matter, and then read what falls out.

**This is the counterpart to #107** ("a check that cannot fail is worse than no check"). There the
check existed and could not fail. Here the check did not exist, and its absence made a *different*
piece of code unfalsifiable. Both are the same failure of the same kind: a place where nothing can
go wrong because nothing is looking.

**Two practical corollaries.**

- **When adding a check to a code path that previously ignored its own result, expect the first run
  to fail loudly and do not assume the new check is wrong.** The spurious-looking XTSE3020s on
  `*:a2` were the correct output of a new check reading a long-broken matcher. The instinct to
  narrow the new check to make the noise stop would have preserved the actual bug.
- **The asymmetric pair (#40) is where to look first.** `AcceptTokenMatches` — the same rule for
  `xsl:accept` — had handled every form all along. The fix was to delete the weaker twin and
  delegate, not to write the missing cases a third time. That is the third instance this week:
  `BindParamAsync`'s siblings in #15, `json-to-xml`'s `liberal` option beside `validate`/`escape` in
  #155, and this.

**Measured** (phoenixmldb-xslt#157): 10,361 -> 10,374 of 10,839, 13 newly passing, 0 newly failing,
no set lower. `decl/expose` 21 -> 34 of 42.

### 110. A check that cannot PASS — the mirror of #107, and it hides in the operand (2026-09-18)

#107 is about checks that cannot fail. This is the same defect with the sign flipped, and it is
better camouflaged: a check that can only report failure looks like vigilance.

Two instances in one evening, each found by the other session, each shipped by the person who had
just corrected it in the other's work.

**1. A probe drawn from the commit subject rather than the diff.** Verifying that PR #160's BUGS.md
amendment had landed:

```
git show origin/main:BUGS.md | grep -c "host-speed assertion flipped"   ->  0
```

The phrase is the PR **title**. It is not in the file body. The merge commit `d820a9d` was in
`origin/main` and content probes returned 1 each. That grep returns 0 forever, merged or not — it
carries no information in either direction. Both of us ran it; one of us reached the right
conclusion anyway, because the conclusion happened to be independently true.

**2. `merge-base --is-ancestor` with the wrong operand, on a squash-merging repo.** Proposed as the
fix for instance 1, adopted on the proposer's word, and wrong:

```
mergeCommit = 944ffaa   pr-head = 086a3c5      (#162, verified MERGED)
is-ancestor <mergeCommit> origin/main  ->  IN MAIN
is-ancestor <pr-head>     origin/main  ->  NOT IN MAIN
```

A squash creates a new commit, so the branch head is never an ancestor of `main` even on a perfect
merge. The **pr-head** form reports every merged PR as dropped and cannot distinguish a real drop
from a normal squash. The **mergeCommit** form is sound. *The check was fine; the operand was the
bug* — which matters, because the first retraction was "drop `merge-base`", and an over-broad
retraction destroys a good check.

**What replaced it — three legs, each cheap, each failing closed:**

```
gh pr view N --json state,mergeCommit    MERGED + a mergeCommit   GitHub's account of what it did
is-ancestor <mergeCommit> origin/main    still reachable          catches a later force-push
grep <token from the DIFF> in the file   the change is really there
```

Ask GitHub, which knows what it *did*, rather than git, which only sees the resulting graph. Never
take the probe token from a commit subject.

**The motivating failure, worth recording on its own.** Six PRs merged in one loop with
`--delete-branch`. #157 was stacked on #155's head; deleting that branch **closed the dependent PR
rather than retargeting it**, dropping +13 W3C cases. Every visible indicator was healthy — six
green merges, `main` advanced, no failed check, no conflict, no error. The work was simply absent.
The stack was known and written down an hour earlier; the loop did not distinguish it.

**Before a merge wave:** `gh pr list --json number,baseRefName` and refuse `--delete-branch` on any
branch another PR is based on.

**The property both instances share:** understanding a failure mode in someone else's work does not
inoculate you against it in your own an hour later, because the recognition attaches to their
example rather than to the shape.

### 111. Stale artifacts: four kinds in three days, one rule (2026-09-18)

Four times in three days, a measurement was taken from an artifact that was real, internally
consistent, and belonged to a **different run**. Different artifact kind every time, so pattern-
matching on the last one does not help.

| # | artifact | what it produced |
|---|---|---|
| 1 | a leftover `conformance-results/summary.txt` from an earlier run | a phantom "+12 gains" against the wrong baseline |
| 2 | two on-disk conformance baselines predating #149 | a regression reported that did not exist |
| 3 | a wait loop keyed to "newest run on `main`" | a deploy reported successful from a run that **predated the merge** |
| 4 | a stale **local** git ref (`dc9904c`) while the pushed head was `086a3c5` | nearly measured a pre-rebase tree and reported it as someone else's PR |

Instance 3 is the sharpest, because it *looks like waiting*: the loop asked for the most recent run,
that run was already complete, and it exited instantly with an answer about a different commit.

Instance 4 was the closest call — it would have produced a plausible number, attributed to a named
branch, with nothing visibly wrong.

**The rule:** key the lookup to the thing being verified — the commit SHA, the run that wrote the
file, the remote ref — **never to what is locally present or most recent.** Before measuring someone
else's branch: `git fetch && git rev-parse origin/<branch>`, never the local ref of the same name.

**Corollary for wait loops:** they must fail closed when nothing matches yet. "No run for `<sha>`"
has to block, not fall through to the newest run, or the bug is rebuilt with extra steps.

---

### 112. The harness ran cases it had declared itself unable to run — and fixing it RAISES the published figure by 0.78 points (2026-09-22)

Three defects in how the QT3 harness decides what to run. All the same fail-open shape, all found by
auditing capability claims after #163 rather than by any test going red.

| # | defect | evidence |
|---|---|---|
| 1 | `schemaValidation` declared, never implemented | zero code reads a source's `validation` attribute |
| 2 | `staticTyping` declared, never implemented | `XPST0005` appears **nowhere** in either engine |
| 3 | set-level `<dependency>` never read **at all** | the parse loop is inside `ParseTestCase`, reading `<test-case>` children only |

**Defect 2 is the sharpest.** `XqtsConfiguration` already carried
`public bool SupportsStaticTyping { get; init; } = false;` — read by **nothing**, zero call sites —
while `SupportedFeatures` said the opposite and was the set actually consulted. The harness had
contradicted itself since the feature list was written, and the dead property was the half telling
the truth.

**Defect 3 is the largest.** 234 cases across 32 test-sets ran in defiance of their own set's
declared requirements (`schemaValidation` 108, `fn-load-xquery-module` 83, `staticTyping` 43).
`prod/AxisStep.static-typing` sat at **0/15** — fifteen cases failing with errors about axes,
because the one mechanism the catalog provides for saying "skip these unless you implement X" was
being ignored.

## The number, and why this entry exists

    before   29803/31379 = 94.98%
    after    29742/31061 = 95.75%      +0.78 points, ZERO engine change

    318 cases stop running
      257 were FAILING  -> never applicable, correctly gone
       61 were PASSING  -> credit we were taking for cases whose prerequisites we do not meet

**The honest fix and the flattering fix are the same edit.** Nothing about the engine improved; the
figure rose because 257 failures left the denominator. A reader seeing 94.98 → 95.75 in STATUS.md —
which is generated from `conformance-baseline.tsv` twice a day, with no human in the loop — would
reasonably conclude the engine got better. It did not.

> **A conformance percentage is only comparable against itself when the denominator is fixed.** Any
> change to what the harness runs must state the before/after case counts, not just the percentage,
> and must say which direction the denominator moved. This is [[#111]]'s stale-artifact rule applied
> to the denominator instead of to a file.

Sits beside **#57** (published figures describing a build that does not ship) and **#28** (a harness
that scored an expected-error case as passing on any exception). Three entries now saying the same
thing: *the measurement has been wrong more often, and more flatteringly, than the engine has.*

## Re-baselining was legitimate here, and the check that makes it so

The per-set gate failed, correctly — 13 sets lost passing cases. Re-baselining past a red gate is
exactly how a real regression gets laundered, so the precondition was proved rather than assumed:

    sets whose PASSED count dropped              : 13
      explained entirely by cases that STOPPED RUNNING : 13
      NOT explained (a case started failing)     : 0

Every lost pass is matched by a case that left the suite, and **zero** cases went from passing to
failing. That is the only condition under which `CONFORMANCE_UPDATE_BASELINE=1` is honest.


---

### 113. Static declarations came into scope in the wrong order — four defects, one path (2026-09-22)

`fn/system-property-gen` scores 0/166 and #47 attributes that to an absent feature: compile-time
XPath evaluation with inline function items. That is true, and it is not the first thing that goes
wrong. Instrumenting the real stylesheet rather than reasoning about it showed the very first
failures are not "cannot evaluate this expression" but **`Variable $ns-scope is not defined`** —
a static parameter declared at line 93 of the principal module, not in scope for a module included
at line 101. Four separate defects in `ResolveShadowAttributes`/`CollectStaticDeclarations`, each
producing that same symptom:

1. **Every `xsl:import`/`xsl:include` was swept before the principal module's own declarations.**
   So a param declared *above* an `xsl:include` was collected *after* the module that reads it.
   XSLT 3.0 §3.9 orders static declarations by declaration order, with an included module's
   declarations taking effect at the point of inclusion.

2. **The sweep looked exactly one level deep.** A module included by an included module
   contributed nothing, however it was ordered.

3. **Externally supplied static params were merged in AFTER every declaration was evaluated.** A
   supplied value outranks the declared default, so `<xsl:variable name="ns-normal"
   select="$ns-scope = 'normal'"/>` was computed from the default while the shadow attributes
   around it resolved against the supplied value — two halves of one stylesheet disagreeing about
   one parameter.

4. **Collection read only the `select` attribute.** A static variable whose expression arrives
   through the shadow attribute `_select` was registered nowhere, so the next declaration to
   reference it failed. `system-property-100-data.xsl` chains six of these.

And a fifth, found while fixing the fourth: `ResolveShadowValue` treated **any** expression
beginning with `$` as a bare variable name, so `{$wrap($d:args)}` was looked up as a static
variable literally called `wrap($d:args)`. Nothing is called that, so the whole shadow attribute
was reported unresolvable — indistinguishable from a genuinely unknown variable, which is why it
survived #47's four fixes to the same file.

**Import is not include, and treating them alike breaks one or the other.** The first version of
the fix put both in declaration order and lost W3C `attr/static/static-022`, which imports a module
declaring `$p` as 1 from one that declares it as 3 — with the `xsl:import` written *between* two
uses — and requires 3 to win. An imported module has LOWER import precedence, so its declarations
must lose to the importing module's whatever their relative position; an included module has the
SAME precedence and takes effect where it is written. Imports are now hoisted, includes are not.
That case is pinned by a guard test which passes before the change as well, because its job is to
fail if anyone unifies the two paths again.

**Conformance effect: none. Measured, not assumed.** Same-checkout A/B over all 11 XSLT chunks:
457 failures before, 457 after, no per-set differences. `system-property-gen` stays at 168 failing
in both arms. The first A/B — before the import/include split — reported `LOST ['static-022']`,
which is what establishes that the two arms are distinguishable and that "no difference" here is a
measurement rather than a mis-run.

So this is a correctness fix the suite does not currently reward. It matters because it is the
**first** blocker in that set, not the last: with all five defects fixed the errors move on from
"variable not defined" to the genuine feature gap #47 names — dynamic invocation of static function
items at compile time. Eight regression tests in `StaticDeclarationScopeOrderTests`; five of them
fail on unfixed source, and the remaining three are guards that must pass on both.
