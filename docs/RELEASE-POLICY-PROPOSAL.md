# Release policy — proposal

**Status: PROPOSAL, awaiting Lucas.** Nothing here is implemented. Written in response to four
directives given 2026-09-11:

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

**503 published versions across 8 packages.** Publishing is tag-only in every repo — nothing
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

### Proposed train order

Libraries first, tools second, one version number across all of it:

```
phase 1  Core            (only if changed)
phase 2  XQuery library
phase 3  Xslt library            — pins XQuery at the train version
phase 4  xslt, xquery4, XSpec.Cli, [XQuery.Cli, Xslt.Cli if revived]
```

Phase 4 is where the cycle dissolves: by then Xslt at the train version exists, so `xquery4` can
pin it directly instead of trailing. `check-release-train.sh` already enforces train-locked pins
at pack time and fails closed, so the ordering is machine-checked once the phases are split.

**The work this needs:** split the CLI pack/push out of the library job in `phoenixmldb-xquery`
and `phoenixmldb-xslt`, triggered by a separate tag (`cli-v*`) or a second job gated on the
library being live. Then delete the trails-by-1 policy, which exists only to paper over the
cycle. XSpec.Cli additionally hardcodes `<Version>` in its csproj rather than taking it from the
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

**Recommendation: cut it, as the last train under the old process, then adopt the above.**

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

1. **Cut 1.8.0 now, or hold it to be the first lockstep train?**
2. **Approve the train order** in §3, and the CLI/library publishing split it requires.
3. **A minimum interval** — is two weeks right?
4. **Tier 1b unlisting** — the two abandoned CLI packages that shadow `xslt` and `xquery` by
   command name, at 1.4.10, missing every fix including #51 and #52.
