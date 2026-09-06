# RUN_PROTOCOL — verify small and live before every full run

**Standing rule, set 2026-09-06. Applies to every wave of the plan, and to anyone picking this up later.**

## The rule

> **Verify the wiring live, in the smallest possible unit, BEFORE any full run.**
> Real credentials are approved for this. A full run is never the first live thing you do.

This is stricter than "dry-run first". A dry run proves the plumbing accepts a stub; it does **not**
prove the thing works against a real model. Both are required, in this order.

## The stages

| # | Stage | Spends | Proves |
|---|---|---|---|
| 1 | **Dry run — every case** | nothing | the code path executes; arguments survive the round trip; nothing throws. ⚠ A dry run that passes proves the *stub* was accepted, nothing more. |
| 2 | **One real unit, live** | one turn | the wiring reaches a real model and comes back usable: the tool channel was used rather than prose, usage is reported, the result is not empty or degenerate. **If this is wrong, STOP — do not proceed to stage 3.** |
| 3 | **The full run** | the rest | the measurement itself. |

`--only <id>` exists on Evals 02, 02b, 02c **and, from 2026-09-06, 09** precisely to make stage 2
possible; it addresses a probe snapshot key, never the cohort key, and is ignored under `--ci`.

> ✅ **EXTENDED 2026-09-06 — and the eval it was missing from was the one that needed it most**
> (`MEASUREMENT_STATUS` §61.1). **Eval 09 had no probe form at all**: its stage 2 *was* the cohort —
> 12 personas × 4 arms, 2 live reps, the most expensive command in the suite. **A stage that can only
> be run at full price is a stage that will be skipped**, and it was: plan item 8.16 #5 sat open on
> *"confirming the new numbers needs a judged run"* while the only judged run purchasable was the
> whole thing. `-- 9 --only <persona-id>` now runs one persona to the probe key
> `eval09_hypothesis_ab_probe`, and the full-cohort record is untouched — **verified by KEY and by
> the cohort file's mtime, not by a file count**.
>
> **MEASURED, on `-- 9 --quick --only USR-MI-02`, foreground, exit 0:** 22 model round-trips
> (12 agent · 6 workflow · 4 judge), **0 cancelled, 0 failed, 0 usage-less on all three ledgers**,
> **USD 1.4725** — tokens from the provider's own usage blocks, priced at
> `ModelPricing["gpt-5.5"]`. That is roughly a twentieth of the cohort, and it was enough to
> establish the item's headline finding before the cohort was bought.
>
> ⚠️ **And building it exposed a defect of the recurring shape, in the dry run's own checks.** Five
> of Eval 09's plumbing checks assert properties of an injection that lands on ONE named persona —
> the cancelled `InterestMapper` on `USR-MB-13`, the instructed silence on `USR-JV-08`. Under
> `--only` those personas need not be in the run, and all five printed **❌** on the first
> `-- 9 --dry-run --only USR-MI-02`: five red ticks for injections that were never issued.
> **Applicability was being read out of the RESULT.** They now print `⏭ NOT APPLICABLE`, which is
> deliberately not a tick, and the conjuncts are dropped from the verdict rather than assumed true.
> **A red tick for an absent subject is the same defect as a green one.**

## Stage 0 — RE-EXECUTE the ablation you are about to build on

**Added 2026-09-06 (Wave 4). It sits before stage 1 because it is cheaper than stage 1 and it caught
three things in one wave.**

> **A published ablation is a claim, not a measurement you already have.** Before you cite one — to
> justify an item, to price a change, or to defer something — re-run it. Not re-read it: run it.

Wave 4 worked five items and re-executed the ablations behind three of them. **All three did not
reproduce**, and two were in text written specifically to correct an earlier stale claim:

| the published claim | what re-running it gave |
|---|---|
| `MEASUREMENT_STATUS` §31.1: the attribution gate leaves **GATE B at 4 of 5**, *"Renzo ❌, Nadia ❌, Marco ✅ recovered"* | **3 of 5.** Two failures out of five is three matching, and Marco was ✅ at baseline, so nothing recovered. **Flattering to the gate.** §31.1 was itself the correction of a stale cost quoted for two waves |
| `MASTER_PLAN` §0.6: widening the schema makes **two** named test files fail by design | **One.** The second file's facts assert only that no field is WRITTEN, which the widening does not touch |
| `MASTER_PLAN` 8.21: **3 of 16** COVERED rows carry nothing the interest names, on two customers | **7 rows on 4 customers** across the authored cohort — and two of those customers appear in no eval's per-case table |

**Why re-reading does not substitute.** Every one of these was internally plausible, correctly
formatted, and had survived at least one review pass. The first is arithmetically self-contradicting
on its own line and three waves of readers went past it. What separated the claim from the fact was
not attention; it was **execution**.

**The cheap form of this rule:** an ablation that is worth writing down is worth writing down *with
the command that produces it*, so re-running it is one paste. Every §-section in
`MEASUREMENT_STATUS.md` that records an ablation now ends with that command block, and the ones that
did not are the ones that went stale.

### Stage 0c — an ablation that plants a REAL SECRET must name its cleanup in the same breath

**Added 2026-09-06 (Wave 7's review pass, `MEASUREMENT_STATUS` §57.4a). It is stage 0 applied to what
the ablation LEAVES BEHIND.**

> **Running an ablation is not free of side effects, and the ones that matter most are the ones the
> standing rules forbid.** A control whose subject is a credential will be ablated by planting a real
> one. Every eval in this suite that persists does so at the END of the run the ablation is executed
> by — so the plant lands in a stored snapshot — and `EvalResultStore.Write` archives the previous
> file before writing the new one, so the *restore* run copies the polluted document into a dated
> archive that nothing ever overwrites. **Restoring the code is not restoring the tree.**

Measured, not imagined: `EverySnapshotSaysWhatProducedIt`'s ablation C splices the endpoint host into
the provenance note. Re-executing it as published put the host into `eval03_controls.json`; a scan of
`.agenteval/` then found **the review's own file and an archive from the authoring run four hours
earlier**. `.agenteval/` is gitignored, so nothing reached the repository — the exposure is the
working tree, which is precisely where a repo credential scan does not look.

**The rule:** delete the canonical snapshot BEFORE the restore run (so the archive-first rule has
nothing to copy), then prove the store is clean with a scan that reports a COUNT and never a value.
The commands are in `MEASUREMENT_STATUS` §57.4a. **A published ablation that plants a secret and does
not publish its cleanup is a defect in the ablation, not in whoever ran it.**

### Stage 0b — RE-EXECUTE IN **BOTH SPACES**, and that includes the control you just wrote

**Added 2026-09-06 (Wave-4 verification run, `MEASUREMENT_STATUS` §42). It is stage 0 applied to your
own work rather than to somebody else's.**

> **A control verified in one embedding space is unverified.** Run every new or changed gating row
> **in both spaces before the wave closes** — `-- 3` and `-- 3 --real-vectors`, `-- 7` and
> `-- 7 --real-vectors`. They are free, they take seconds, and the numbers underneath them are not
> the same numbers.

Wave 4 added the gating row `TopologyCaseProseMatchesTheRun`, verified it under `-- 3`, and its own
review then re-executed four of the wave's ablations — all in the **default concept space**. The
first `-- 3 --real-vectors` anyone ran afterwards came back **exit 1**, against a published exit code
of **0**. The wave's own new control was red on the tree the wave declared clean.

**Why one space is not a sample of the other, measured:** the deterministic discovery loop is **not
space-invariant**. Two of Eval 07's five customers **swap round counts** between the spaces, one
flips DEGRADED → APPROVED, and one of the four frozen stop reasons is **unreachable** on the real
path. The gates did not move — GATE A/B/C are ✅/❌/✅ in both — so nothing about a gate's verdict
warned that everything underneath it had.

**The general form, which is not about embeddings.** This suite has two resolved *configurations* that
both claim to be the product: a default and a flag. Anything you assert without naming one of them is
an assertion about both, and it is checked against whichever one you happened to run. The fix in this
instance was to make the artefact **name the configuration it describes** and let the control read the
**resolved** one — never the requested one, because `--real-vectors` falls back to concept without
credentials and a check that reads the request asserts the wrong configuration on every machine
without a key.

## Why stage 2 exists — the evidence from this repository

Every one of these passed a dry run and was still broken:

| What passed dry-run | What the live run found |
|---|---|
| Eval 01, all plumbing green | `wahl` — German for both *election* and *choice* — tripped a zero-tolerance GDPR detector on de-language personas. 3 of 6 failures. **Structurally invisible offline**: the deterministic arm composes its reasons in English. |
| Demo 2, exit 0 | 6 of 7 model calls timed out at 60 s. The printed verdict "the loop bought nothing" came from **timeouts, not the reviewer**. |
| The ranker, offline | Rule 6 tells the model to cite `grounding_attribute_key` from a list whose tokens the resolver **rejects** — the agent punished for obeying its instructions. Visible only in a live trace. |
| Eval 02, `--dry-run` exit 0 | **Crashes on the paid path.** The dry run cannot see the branch that crashes, so two gates and the cost panel never printed. ✅ *Fixed `cef95b6c` — the dry run now runs that branch AND varies the stub's `k`, and says which.* |
| Eval 05, judged cells | The judge returned **criteria nobody declared**, on 3 of 10 cells. 🔴 *Corrected `a78d05e5`: it did not. It echoed OUR rubric with the ordinal `ChatClientEvaluator.cs:46` prints itself, and our matcher failed to recognise our own text. Same lesson, opposite subject — the live model behaved more faithfully than the stub, which stripped the ordinal.* |
| Eval 06, `-- 6 --dry-run` exit 0, and the detector had just been REPAIRED | `ToolJson.SearchCapExhausted` carries `status = "budget_exhausted"` beside `code = "search_cap_exhausted"`, so a substring detector charged case T-03 with overrunning a **24-call** budget it had spent **16** of — the DISTINCT-SEARCH cap at 8/8 was what refused. ✅ *Fixed `4d35aaa2`; gating row `RefusalCodesDoNotAnswerForEachOther`.* **A stubbed tool result carries exactly one refusal code, so the two codes never meet in a dry run.** |
| Eval 02, every dry run green, and a whole review pass over the panel | The forced-choice panel printed **`0.667 (0 of 1)`** — a rate and a count contradicting each other on one line — and, twelve lines above, *"NO arm beats the forced-choice chance rate of **1.000**"* beside a panel whose header said 0.083. A cell is `CoverageScore.Mean`'s average over reps, so it is not a Bernoulli outcome, and the caveat derived 1/N from the personas that RAN rather than the golds competed against. ✅ *Fixed `906d5f1f`; gating row `ForcedChoiceCountIsACountOfPersonas`, `MEASUREMENT_STATUS` §34.1–2.* **Both are INVISIBLE at n = 12 and both fire at n = 1 — the probe size stage 2 mandates. The second was also live on the paid cohort: 6 of 12 printed where the cells say 7.** |

The pattern: a dry run exercises the code the stub reaches. The defects live in the code only a real model
reaches — different text, different language, different failure modes, different timing.

**And one refinement the 2026-09-06 VERIFICATION run added, which is about stage 2's SIZE.** The last
row above was not found because the model said something surprising. It was found because
`--only` runs the panel at **n = 1**, and two of the panel's reductions are degenerate there and
correct at n = 12. **Stage 2's one-unit shape is not only cheap, it is a different test**: extreme
values are wiring faults until proven otherwise, and a probe manufactures the extreme values a
cohort never shows you. Read the probe's panel, do not just check its exit code.

**And one refinement the 2026-09-06 wave added, which is the harder half.** Two of those rows were reachable
from stage 1 all along; what stopped them was that the **stub behaved better than any real model would**. The
Eval 05 stub echoed each criterion with the ordinal *stripped*; the Eval 02 stub presented a constant `k` and
ran one repetition. A stub that is more convenient than reality is not a conservative test, it is a blind one.
Both stubs now exercise the awkward form — Eval 05 alternates ordinal / bare and asserts both were seen, Eval
02 alternates 2 / 3 products across two reps — and where a dry run genuinely **cannot** see its subject it now
prints a check that says so rather than a green tick beside it.

## Persistence

Every eval run that produces a verdict must persist a snapshot to
`.agenteval/samples/Galaxus.RecommendationAgent.Evals/snapshots/` via `EvalResultStore`.
**Verify the file landed** — list it with its timestamp. A run whose result was not written did not happen,
as far as the next person is concerned.

**A dry run of a MODEL-BACKED eval must not write a snapshot** — it has no result to record. A dry run of a
**model-free** eval writes normally, because there is nothing to stub: it is the same measurement either way.

> ✅ **CORRECTED 2026-09-06 (Wave 2, plan item 8.19).** The rule above used to read *"a dry run must NOT
> write a snapshot"* full stop, and `--ci --dry-run` falsified it on every run: Evals 03 and 04 call no
> model, so the CI chain hands them no `dryRun` argument, so they run for real and persist —
> `eval03_controls.json` and `eval04_injection.json` moved at 01:26:14 inside a dry run that ran
> 01:26:12–01:26:19 (`MEASUREMENT_STATUS` §24.7 item 1), and again inside the run that verified the write-up.
> **The writes were always correct; the claim was the defect.**
>
> **Decided, not deferred:** 03 and 04 do **not** gain a `dryRun` parameter. Replacing a real, model-free
> measurement with a stubbed copy of itself inside a dry run would make the cheapest honest measurement in
> the suite worse in order to make a sentence true.
>
> **What changed instead:** `EvalResultStore` keeps a write ledger (`KeysWrittenThisRun` /
> `SnapshotsWrittenThisRun`), fed by both write chokepoints, and the closing banner **names every snapshot
> the run actually wrote** instead of asserting there were none. It reports only keys whose file is still on
> disk, so a reader who is told a snapshot was written can go and look at it. Pinned by Eval 03's gating
> control `WriteLedgerMatchesTheStore`, proven red by removing either chokepoint's `RecordWrite`.

> ✅ **EXTENDED 2026-09-06 (verification run, `MEASUREMENT_STATUS` §34.3) — and 8.19's decision had a
> third case nobody had applied it to.** Eval 07 also calls no model on any path, and it *does* have a
> `dryRun` parameter: by hand, `-- 7 --dry-run` runs ONE of five cases and asserts only the plumbing.
> The CI chain was passing `parsed.DryRun` into it, so **`--ci --dry-run` printed *"Eval 07: passed"*
> and exited 0 while `-- 7` exited 1 with GATE B ❌** — the identical free measurement, two opposite
> answers. `RunCiAsync`'s own header puts Eval 07 in the chain so that *"an eval that is not in the
> chain has its failures reported nowhere at all"*, and under the recommended invocation they were.
>
> **Decided:** the CHAIN passes `dryRun: false` for every model-free eval; `-- 7 --dry-run` by hand is
> unchanged and still useful. **`--ci --dry-run` now exits 1**, correctly, and the write ledger names
> **three** snapshots rather than two. Pinned by Eval 03's gating row
> `CiChainRunsModelFreeEvalsForReal`, which reads `Program.cs` rather than trusting it.

**Which evals persist at all.** Do not read this list off a document again — read it off the code. Every
eval file carries a `// SNAPSHOT-POLICY:` line on line 4, and Eval 03's gating control
`EveryEvalDeclaresItsSnapshotPolicy` checks each declaration **against that file's actual store calls, in
both directions**, so a stale declaration fails the build rather than misleading a reader:

```
grep -n "SNAPSHOT-POLICY" samples/Galaxus.RecommendationAgent.Evals/Evals/*.cs
```

Measured 2026-09-06 at `046f5425` and **re-executed at `8af63683`, unchanged** (stage 0 applies to
this claim too — it is one `grep`): **11 files, 10 `writes`, 1 `deliberately-none`.** The one is **Eval 08**,
and its reason is stated in code (`Eval08:316-319`) — nothing consumes a stability snapshot, and a number in
a shared store that no gate reads is a hazard a later reader can mistake for one that is.

> ✅ **CORRECTED 2026-09-06 (Wave 2, plan item 8.20).** This paragraph used to read *"01, 02, 02b, 02c, 03,
> 04, 07, 09 do; **05, 06 and 08 do not**"*, and item **8.20** closed exactly that: `eval05_quality` and
> `eval06_trajectory` are new typed records, written on the **live** branch only — so a dry run still writes
> neither, and a paid run now leaves both. **The silence was the defect, not the missing file**: three
> identical-looking silences, one deliberate and two accidental, with no way to tell them apart without
> reading three files. That is what the declaration and its control remove.
>
> ✅ **VERIFIED ON PAID RUNS 2026-09-06** (`MEASUREMENT_STATUS` §27.5), because a claim that an eval persists
> is worth nothing without the file: `eval05_quality.json` (3,257 B, 04:24:53) and `eval06_trajectory.json`
> (4,137 B, 04:05:56, and a second time at 03:53:49 before the fix that run found). All **thirteen** pointers
> are listed there with timestamps and bytes.

## Capture the exit code — including, especially, of the expensive command

`MEASUREMENT_STATUS` §27.4 records the one place this run failed its own standard: `-- 2` (36 live turns,
¤27.1208, ~32 minutes) was launched **detached**, so no shell captured `$?`. Its exit had to be *derived*
from the two printed gates plus `Eval02_LatentInterestCoverage.cs:765`. A derived exit code is not a
measured one, and the command it was derived for is the most expensive in the suite. **Run the long one in
the foreground, or redirect through a wrapper that writes the code beside the log.**

## Cost discipline

Report cost from the provider's own usage blocks, never from an estimate. If a run reports
`token-estimated > 0` or `unaccounted > 0`, say so — a currency figure derived from a guess is not a
measurement. Prompt tokens dominate in a tool loop (measured: 96% of the bill), so a per-turn cost is
driven by context re-sending, not generation.

> ⚠️ **THAT 96% IS THE TOOL LOOP'S AND DOES NOT TRANSPORT TO THE WORKFLOW LANE — measured 2026-09-06
> (`MEASUREMENT_STATUS` §55.2), on the first two runs that could report it at all.** On the discovery
> loop, **completion is 56% of tokens** — 9,202 of 16,404 and 6,779 of 12,123, two runs of one
> persona. At `ModelPricing`'s row for this deployment output is priced **6× input**, so that lane's
> bill is dominated by **generation**, not by context re-sending. Two lanes, opposite cost shapes:
> name the lane before quoting either.

> ✅ **STAGE 2 CAN NOW SATISFY ITS OWN CHECKLIST ON THE AGENT'S DEMO LANE — fixed 2026-09-06
> (`MEASUREMENT_STATUS` §55).** ⚠️ **SUPERSEDED, and the superseded text is kept because the reason
> it stood for two runs matters.** It read: *"the stage-2 table above requires 'usage is reported'.
> `agent -- 2 --user <id>` makes real model calls and prints no token count, no usage block and no
> currency figure at all — measured twice, in Wave 4's smoke (§40.4) and in the verification run
> (§42.8). Plan item 8.17."* That was true and the cause was ours: `DiscoveryModelCall.RunAsync`
> called `AIAgent.RunAsync` and returned `response.Text`, **dropping `AgentResponse.Usage`** — the
> property `MAFAgentAdapter` reads off the identical call against the same deployment. The usage was
> never absent and was never un-asked-for.
>
> **What a stage-2 unit on that lane now reports**, measured on `agent -- 2 --user USR-NB-01`, exit
> **0**, foreground: `3 model call(s) · 5,344 prompt + 6,779 completion = 12,123 token(s), read from
> the provider's own usage blocks`. **Quote the invariant, not the digits** — the lane is stochastic
> and a second run of the same persona gave 4 calls and 16,404 tokens. The invariant is
> `CallsWithoutUsage = 0`.
>
> ⚠️ **The lane still reports `cost: UNKNOWN IN THIS PROCESS`, and that IS inside this rule.** The
> demo project has no rate table to reach — no AgentEval dependency, by design — so it names the
> tokens and refuses to invent a rate. Eval 08's workflow panel, which can reach `ModelPricing`,
> prints the money **with the rate and its source on the line above**. **UNKNOWN is never rendered as
> zero anywhere**: a call whose response carried no usage block is counted separately and the total
> is labelled a LOWER BOUND. Pinned by Eval 03's gating row `TheChatLaneSaysWhatItSpent`, which fails
> when an absence renders as a zero.
>
> ⚠️ **`ARunThatSaysItSpendsSaysHowMuch` did not catch any of this, and it is not inert.** It is
> **scoped to the EMBEDDING lane** — every check in it names `EmbeddingSpace` — and it stays green
> under all four ablations of the chat-lane row. A row's NAME is not its subject; read its body.

> ✅ **AND THE SUITE NOW OBEYS IT — 2026-09-06 (`MEASUREMENT_STATUS` §34.4).** Until this run, every
> `--real-vectors` command printed *"This run EMBEDS QUERIES LIVE … it spends — a fraction of a cent,
> but not zero"* and then **no figure at all**: `EmbeddingSpace.PrintLiveSpend` was called from Demo
> 01 and from `cal`, and from no eval. **13 of the 14 commands in the real-vector sweep declared a
> cost and reported none**, leaving "a fraction of a cent" as the only number a reader had — an
> assertion nobody measured. **Reporting nothing is not the conservative end of this rule; it is
> outside it.** Both entry points now call the reporter in a `finally`, it is print-once per process
> so Demo 01 cannot bill a reader twice, and Eval 03's gating row `ARunThatSaysItSpendsSaysHowMuch`
> holds it there — including the LOWER BOUND caveat that stops a response with no usage block being
> reported as free.
>
> ⚠️ **§20 item 3 had already FOUND this on 2026-09-05 and deferred it** — *"not fixed here, because
> the fix is a shared meter and that is its own change."* There was no shared meter: two call sites
> and a latch. **A deferral's stated cost is a claim like any other, and this one was never checked.**

## What this protocol does not do

It does not make a run *correct*. Stage 2 proves the wiring is live; it says nothing about whether the
metric is meaningful, whether the arms are comparable, or whether the gate can fail. Those are the
instrument's job — chance floors, negative controls, equal-k pairing, attainable p — and they are
documented in `MEASUREMENT_STATUS.md` and the ADRs, not here.
