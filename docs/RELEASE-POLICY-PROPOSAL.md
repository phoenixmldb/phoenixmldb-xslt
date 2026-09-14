# Release policy — proposal

**Status: §5 DECIDED, the rest still proposal.** Lucas ruled 2026-09-11: **hold 1.8.0 and get
lockstep in place first**, and **lockstep includes the MCP servers** — added below in §3a.
Nothing else here is implemented. Written in response to four directives given 2026-09-11:

> slow down the blistering cadence of NuGet releases — we have over 50 incremental releases
> public on NuGet and this isn't a good look for us / XSLT, XQuery and XSpec releases in
> lockstep / Set up dependencies however we need to for development mode (typically project
> reference) so we can fix bugs, switching back before git commits and/or releases / the
> db-engine agent can wait — we have far too many open issues right now

## 1. The numbers, since "over 50" understates it

Counted from nuget.org, 2026-09-11:

| package | published versions | latest | source repo |
|---|---|---|---|
| `PhoenixmlDb.Xslt` | **128** | 1.7.0 | `phoenixmldb-xslt` |
| `PhoenixmlDb.XQuery` | **118** | 1.7.0 | `phoenixmldb-xquery` |
| `PhoenixmlDb.Core` | **40** | 1.7.0 | `phoenixmldb-core` |
| `xslt` (tool) | **113** | 1.7.0 | `phoenixmldb-xslt` |
| `xquery4` (tool) | **89** | 1.7.0 | `phoenixmldb-xquery` |
| `PhoenixmlDb.Xslt.Cli` | 7 | **1.4.10** | `phoenixmldb-cli` — abandoned |
| `PhoenixmlDb.XQuery.Cli` | 6 | **1.4.10** | `phoenixmldb-cli` — abandoned |
| `PhoenixmlDb.XSpec.Cli` | 2 | 1.6.11 | `xspec` (fork, branch `phxspec`) |
| `xquery-mcp` | 7 | **1.4.0.3** | `xquery-mcp` |
| `xslt-mcp` | 10 | **1.5.0.1** | `xslt-mcp` |
| `crucible.cli` | 8 | **1.1.76** | `crucible` |

**528 published versions across 11 packages.** Publishing is tag-only in every repo — nothing
publishes per push — so the count is purely how often someone tags.

## 2. Lockstep is easier than it looks, because the blocking fact is wrong

It was reported that `PhoenixmlDb.XSpec.Cli` is built from the database repo, which would make
lockstep depend on db-engine — who has just been deprioritised. **That is not where it lives.**

Verified: it is in the **`xspec` fork** (`phoenixmldb/xspec`, branch `phxspec`), at
`dotnet/PhoenixmlDb.XSpec.Cli/`, published by `.github/workflows/phxspec-publish.yml` on the tag
pattern `phxspec-v*`.

The confusion is worth recording, because it will recur: `phoenixml/xspec` is a **git submodule**
of the database repo pointing at this same fork. A grep that walks into the submodule reads as
though the source lives in the database repo. It does not — the fork is the source of truth, and
the submodule is a consumer of it.

**So "XSpec in lockstep" does not require db-engine, and the two directives do not conflict.**

Its dependency is a clean single edge — `PhoenixmlDb.Xslt` only — so it slots into the train
after Xslt with no cycle.

## 3. There is exactly one cycle, and it is already documented

`phoenixmldb-xquery/Directory.Packages.props` explains it: `PhoenixmlDb.XQuery.Cli` needs
`PhoenixmlDb.Xslt` for `fn:transform`, while `PhoenixmlDb.Xslt` needs `PhoenixmlDb.XQuery`. At
the library layer there is no cycle — XQuery depends only on Core — but the xquery repo
publishes library and CLI together at one version, so the CLI's Xslt pin can only ever point at
the *previous* train. Today it is `1.6.15` in the `1.7.0` train. That file already names the fix:

> The real fix is to publish the CLIs separately from the libraries so tools and engines version
> independently.

That is also what lockstep needs. **Same fix, two motivations.**

## 3b. Ordering constraint, added 2026-09-11

Lucas:

> parsers2 is under orders to advance as far as possible with xslt before updating xquery AND
> before doing any releases. No more increments — we are far past daily half-measured public
> nuget pushes

Two things, and they compose with everything above:

1. **XSLT work runs to its natural end first**, then XQuery, then a release. The train is not
   cut while either engine still has obvious ground to cover.
2. **No increments.** The policy in §4 stops being a proposal about cadence and becomes the
   operating rule: a train is cut for a cause, not because work landed.

Worth being concrete about what "no more increments" already means mechanically. Three repos
published on **every merge to main** — `xquery-mcp`, `xslt-mcp` and `crucible` — with versions
derived from commit counts. Those were not cadence *decisions* that anyone made too often; they
were the absence of a decision. Making all three tag-only (this session) removes more actual
publishing frequency than any policy could.

## 3a. The MCP servers join the train

Added on Lucas's instruction. Both are `dotnet tool` packages published on a `v*` tag with
trusted publishing, so mechanically they are ready; three things need deciding or fixing.

| repo | package | tool command | pins | latest published |
|---|---|---|---|---|
| `xquery-mcp` | `xquery-mcp` | `xquery-mcp` | `PhoenixmlDb.XQuery` **1.6.14** | 1.4.0.3 |
| `xslt-mcp` | `xslt-mcp` | `xslt-mcp` | `PhoenixmlDb.Xslt` **1.6.14** | 1.5.0.1 |

**Dependencies are clean.** Each pins exactly one engine library and nothing pins back, so they
slot in after the libraries with no cycle — unlike `XQuery.Cli`.

**Both are already a train behind**, pinned at 1.6.14 while the engines are at 1.7.0. Neither
has been rebuilt against the current engine, so nothing has verified that the MCP servers work
against what we ship today.

**Their version lines do not match the engines'** — `xquery-mcp` is at 1.4.0.3 and `xslt-mcp` at
1.5.0.1, both four-part, against the engines' three-part 1.7.0. Lockstep means jumping both to
the train version. That is a visible discontinuity in those packages' histories, and it is the
right price: a user who installs `xslt-mcp 1.8.0` should get the 1.8.0 engine, which is the
entire point.

**`crucible` was worse than either, and is now fixed (PR #9).** It had **no tag trigger at
all**: publishing ran on every merge and `VERSION="1.1.${COMMIT_COUNT}"`, so `crucible.cli
1.1.76` meant the 76th commit. It had **no pin check of any kind**. And its pin was
`PhoenixmlDb.Xslt` **1.6.13** — the sole Tier 1 known-bad version, which ships depending on
XQuery 1.6.12. crucible builds **phoenixml.dev**, so our own documentation was rendered for
three trains by an engine we had already flagged, and nothing noticed because nothing was
looking.

**Neither MCP repo has `check-release-train.sh`.** Both run `check-pins.sh`, so pins are checked for
internal consistency, but nothing asserts *pin == release version* at pack time. That is the
control that makes lockstep real rather than aspirational, and it is exactly what caught nothing
when these drifted to 1.6.14. Port it from `phoenixmldb-xslt` and mark the engine pins
`check-pins: train-locked`.

### Proposed train order

Libraries first, tools second, one version number across all of it:

```
phase 1  Core            (only if changed)
phase 2  XQuery library
phase 3  Xslt library            — pins XQuery at the train version
phase 4  xslt, xquery4, XSpec.Cli, [XQuery.Cli, Xslt.Cli if revived]
phase 5  xquery-mcp, xslt-mcp    — pin XQuery / Xslt at the train version
```

Concretely, for a 1.8.0 train:

| # | action | publishes |
|---|---|---|
| 1 | tag `v1.8.0` in `phoenixmldb-core`, if Core changed | `PhoenixmlDb.Core` |
| 2 | tag `v1.8.0` in `phoenixmldb-xquery` | `PhoenixmlDb.XQuery` |
| 3 | tag `v1.8.0` in `phoenixmldb-xslt` | `PhoenixmlDb.Xslt` + `xslt` |
| 4 | bump Xslt pin in `phoenixmldb-xquery`, tag `cli-v1.8.0` | `xquery4` |
| 5 | bump pins, tag `v1.8.0` in `xquery-mcp` / `xslt-mcp` | `xquery-mcp`, `xslt-mcp` |
| 6 | bump pin, tag `phxspec-v1.8.0` in the `xspec` fork | `PhoenixmlDb.XSpec.Cli` |
| 7 | bump pin, tag `v1.8.0` in `crucible` | `crucible.cli` |

**Step 4 needs step 2's package to be *published*, not merely tagged.** `PhoenixmlDb.XQuery.Cli`
carries a `ProjectReference` to the library, which packs as a dependency on
`PhoenixmlDb.XQuery` at the release version — so `cli-v1.8.0` produces a package that cannot
restore until `PhoenixmlDb.XQuery 1.8.0` is live on nuget.org. Wait for the package to appear,
not for the tag to go green. The same applies to steps 5 and 6 against step 3.

**A pin bump needs a two-checkout comparison, not `ab-sweep.sh`.** The sweep swaps `src` and
`tests` between arms; a pin lives in `Directory.Packages.props`, which **both arms share**, so it
cannot see the change. Guard 1 refuses rather than reporting a meaningless clean result —

```
A/B ABORT: src and tests are identical — the two arms would measure the same thing.
```

— which is right, but **the abort does not tell you what to do instead**, and the natural
instinct is to reach for the sweep. The substitute is **two full runs at the two commits and a
per-set diff of the failing cases.** Compare by *set*, not by totals: equal totals can hide
offsetting moves, which is why the per-set gate exists.

Worth stating plainly: **every engine merge is measured by a tool that is structurally blind to
dependency changes.** Fine for engine work, wrong the moment someone reaches for it to check a
pin.

**Before step 2, re-run the external reporter's own cases against the tip you are about to tag.**
parsers2 re-verified Martin Honnen's three reproductions after five streaming merges had landed —
they still pass — and made the point that matters: **that check is worth repeating immediately
before cutting rather than trusting an earlier run**, because more merges land in between.

The reasoning generalises past Martin. A conformance sweep tells you which corpus cases moved; it
does not tell you whether the thing a real user reported still works. Those are different
questions (BUGS.md #71), and the second one is the one that gets asked publicly after a release.
It costs one command per reproduction and it is the cheapest insurance in this list.

**Two things about that step, learned by nearly getting it wrong (2026-09-13).**

**It verifies REPORTED BUGS, not conformance.** Those reproductions confirm that specific
user-reported defects are still fixed. They say **nothing** about whether conformance held up
under a dependency bump — and this checklist's author came within one command of treating them as
the whole gate before cutting 1.8.0. Both checks are required; neither substitutes for the other.

**Name where the reproductions live.** The person cutting a release may not be the person holding
them — that is exactly what happened here, and the step was briefly unexecutable by the only
person able to tag. A checklist step only one participant can perform is not a checklist step.

**After the train is published, install a tool from nuget.org and run one query.** Nothing in
this process does that, and nothing did it for the 1.8.0 train until an unrelated task happened to
need the CLI:

```sh
dotnet tool install --global xquery4 --version <train>
xquery 'sum((xs:integer("10"), xs:integer("30")))' ctx.xml    # -> 40
```

**Every check before this point runs against a build tree or a local package cache.** CI proves the
code compiles and the suites pass; `check-release-train.sh` proves the pins agree; the publish job
proves nuget accepted the upload. **None of them proves the artifact a user downloads installs and
executes.** A packaging fault — a missing dependency, a bad tool manifest, a runtime that resolves
differently outside the build tree — would pass every gate we have and be discovered by the first
person to install it.

**The test must be unable to resolve anything but the package.** A project reference, a sibling
checkout, a stale `bin/`, or a dev-mode marker is a path by which it silently tests the wrong
artifact — and reports success. Two specific traps in this workspace:

- **Dev mode activates from a marker file outside every repo.** `Directory.Build.targets` turns
  `PhoenixmlDb.*` package references into project references when
  `<workspace>/.phoenixml-dev` exists or `PHOENIXML_DEV=1` is set. A scratch project created
  *inside* the workspace inherits that. **Create it outside the tree** — `$(mktemp -d)` — and
  assert dev mode is off before trusting the result. The existing guards (pack refuses, CI
  errors) do not cover a smoke test, which runs *after* pack.
- **`--no-build` against a source project cannot fail for a packaging reason**, because
  packaging is not in its path. The engine repo's existing "smoke tests" are this shape: they
  verify the code in the tree and look like a release gate.

**Run it twice, and for different reasons.** Once against the packed `.nupkg` — that catches a
bad nuspec, a missing dependency, a wrong tool manifest. Once against nuget.org at the published
version — that catches anything the feed does to it. They fail differently and neither implies
the other.

**And the two runs collapse into one silently, via the global packages folder** (db-engine).
Packing locally puts the same id and version into `~/.nuget/packages`; the "against nuget.org"
run is then satisfied from cache and **never touches the feed.** Both runs pass, one of them
proved nothing, and nothing says so.

So the second run needs:

- `NUGET_PACKAGES=$(mktemp -d)` — a packages folder of its own
- a generated `nuget.config` with `<clear/>` and **one** source
- **a post-restore assertion over `obj/project.assets.json`**: every `PhoenixmlDb.*` is type
  `package`, at the expected version, from the expected source

That third item is the one that generalises. The first two **arrange** for the right resolution;
only the assertion **verifies** it happened. Every other trap in this section — dev mode, a
sibling checkout, a stale `bin/`, the cache — is defeated by the same check, because they all
end in the assets file disagreeing with what you intended.

It costs two commands, and it retired an open issue on its first use: `xquery4 1.8.0` installed
clean and returned `80` for `phoenixmldb-xquery#4`'s own scenario, which is the difference between
*"the fix is in the tag"* and *"the fix is in the thing people get"*.

Every step whose pin must match is gated by `check-release-train.sh`, which fails closed. The
steps that are *not* machine-checked are the tags themselves — forgetting step 4 or 6 publishes
nothing and fails silently. That is the remaining manual risk in this design, and it is the
thing a release checklist exists to cover.

Phases 4 and 5 can run together — nothing in either depends on the other — but keeping them
distinct makes the failure obvious if an MCP server cannot build against the engine it is
supposed to ship with, which is a thing we would rather learn at release than from a user.

Phase 4 is where the cycle dissolves: by then Xslt at the train version exists, so `xquery4` can
pin it directly instead of trailing. `check-release-train.sh` already enforces train-locked pins
at pack time and fails closed, so the ordering is machine-checked once the phases are split.

**This is now implemented for `phoenixmldb-xquery` — PR #28.** The tag scheme:

```
v<version>      packs PhoenixmlDb.XQuery      — needs Core only
cli-v<version>  packs PhoenixmlDb.XQuery.Cli  — needs Xslt of this train
```

The two artifacts have different dependency sets, so separate tags let each be checked against
what it actually needs. `check-release-train.sh` is ported and runs on the CLI tag only —
demanding equality on a library tag would fail for a pin the library never uses.

`trails-by-1` **stays**, and is not redundant with `train-locked`: `check-pins.sh` tolerates the
window between Xslt publishing and this repo bumping to it, while `check-release-train.sh`
demands equality at the only moment it matters, packing the CLI. Tolerance between trains,
equality at the tag. What changes is that trailing by one is no longer *permanent* — which is
how that pin sat at 1.6.11 through both the 1.6.12 and 1.6.13 trains.

**`phoenixmldb-xslt` deliberately unchanged.** Its CLI pins XQuery, already published by the
time that repo is tagged, so there is no cycle to dissolve. A second tag there would be symmetry
for its own sake, and a tag someone can forget. XSpec.Cli additionally hardcodes `<Version>` in its csproj rather than taking it from the
tag; that should move to tag-driven like the others.

## 4. Cadence

The tag-only trigger means cadence is entirely a human decision, so the policy is a rule, not a
mechanism:

1. **Release on a cause, not on a merge.** A fix reaching main is not a reason to tag. The
   reasons to tag are: a correctness defect users can hit, a security issue, or a consumer
   blocked on an API that exists.
2. **One train, all packages, one version number.** No more partial trains — that is what
   produced the mismatched `Xslt 1.6.13`.
3. **A minimum interval between trains** — two weeks is a starting number, to be argued with.
   Anything more urgent should be justified in the release notes by which of the reasons in (1)
   it meets.
4. **Unlist aggressively in parallel.** Cadence and clutter are separate problems: slowing down
   stops the pile growing, but 503 versions are already published. See `RELEASE-HYGIENE.md`,
   including the Tier 1b abandoned CLI packages that shadow the current tools by command name.

## 5. The 1.8.0 question

**DECIDED 2026-09-11: hold it. Lockstep goes in first, and 1.8.0 becomes the first lockstep
train.** The argument below is left as written because it records what was weighed.

My recommendation was to cut it as the last train under the old process; parsers2's was to hold.
Lucas took parsers2's. The cost is that #51 and #52 stay unfixed in the wild for as long as the
lockstep work takes, which makes that work the thing standing between users and two silent
wrong-answer defects — worth saying plainly so it is scheduled like it matters, not treated as
cleanup.

*Original recommendation, superseded:*

The directive objects to *incremental* releases. 1.8.0 is the opposite of incremental — it is
the consolidation of everything since 1.7.0, and its case rests on correctness rather than on
conformance points:

- **BUGS.md #51** — `cache="yes"` returned other calls' results. All 47 published tags.
- **BUGS.md #52** — accumulators read a later-declared accumulator one node late. All 47 tags,
  and **not opt-in**.

Holding it does not reduce the 503 versions already public; it only prolongs how long those two
defects are the only thing a user can install. Under the proposed policy, 1.8.0 is precisely the
shape of release that qualifies — which is the argument for cutting it rather than an exception
to the rule.

**Counter-argument, stated fairly:** parsers2 recommends holding until the policy is agreed, and
if lockstep is implemented first, 1.8.0 becomes the first train under the new process rather
than the last under the old. That is tidier, and costs only the time it takes to split the CLI
publishing. If that work is days rather than weeks, holding is defensible.

**What is NOT a reason to hold:** waiting for a green Conformance workflow. It has failed on
every run including both `v1.7.0` tag runs, for a CI configuration reason unrelated to engine
quality (BUGS.md #56). Conformance is actually verified by the committed baseline and the
per-set gate in `ci.yml`.

## 6. Decisions needed

1. ~~Cut 1.8.0 now, or hold it?~~ **DECIDED: hold; it becomes the first lockstep train.**
2. **Approve the train order** in §3, and the CLI/library publishing split it requires.
3. **A minimum interval** — is two weeks right?
4. **Tier 1b unlisting** — the two abandoned CLI packages that shadow `xslt` and `xquery` by
   command name, at 1.4.10, missing every fix including #51 and #52.
