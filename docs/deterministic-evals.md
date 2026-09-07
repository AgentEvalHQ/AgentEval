# Deterministic evals

A deterministic eval is one measurement of an agent run, computed in code. No model, no cost, and the
same input always gives the same answer. This page is the whole contract: how one is admitted, what
it sees, what its chance floor means, and what happens when it cannot measure anything at all.

It exists because until recently there was no way to reach `IEval` from a real agent run. Three
separate sample projects wrote their own evaluation harness rather than use the library's, because
the library's could not be got at — not because they preferred their own.

---

## The door

An eval is registered through `AgentEvalBuilder.AddEval`, and the door takes **two** things: the eval
and the chance floor it is judged against.

```csharp
var runner = await new AgentEvalBuilder()
    .AddEval(new AskedCityWasLookedUpEval(city), ChanceFloor.UniformChoice(cities.Count))
    .BuildAsync(ct);
```

**There is no floorless overload, and that is deliberate.** A score without a floor cannot be told
from luck: `0.75` means one thing when a coin flip scores `0.15` and something else entirely when a
coin flip scores `0.70`. The rule this enforces is written down in the project's own defect register:

> The one thing that must not happen: wiring many evals into the primary entry point **while none of
> them has a chance floor** takes a contained problem and makes it the product's front door.

If no draw model exists for your eval, say so explicitly rather than inventing a number:

```csharp
.AddEval(myEval, ChanceFloor.NotDerivable("no draw model: the answer set is unbounded"))
```

A `NotDerivable` floor with a blank reason is refused. *"Nobody could derive one"* and *"nobody
tried"* are different facts, and only the stated reason separates them.

The door refuses three more things, each because it would let an eval supply its own bar:

| Refused | Why |
|---|---|
| An eval already admitted under another floor | The outer floor would silently win over the one the eval was actually measured against. |
| A result that already carries its own `chance_floor` | The floor an eval is judged against may not be supplied by the eval. This is the gate-self-examination failure, and it fails in the flattering direction. |
| A result carrying `SubResults` | A composite has one score over leaves that were never individually admitted, so one floor on the root would certify every floorless leaf beneath it. Admit each leaf with its own floor. |

---

## What the eval sees

`TestCase.ToEvalInput(TestResult)` projects a completed run into an `EvalInput`. It needs both,
because the question asked is not recorded on the answer given: the query lives on the case, the
response and the tool trace on the result.

### Tool calls: `null` and `[]` are different answers

This is the single most important thing on this page.

| `EvalInput.ToolCalls` | Means |
|---|---|
| `null` | **Unknown.** No recorder ran, or one ran that could not see the whole run. |
| `[]` | **A measured zero.** A complete recorder was attached and observed no calls. |
| non-empty | The calls, in chronological order. |

An eval asking *"did the agent avoid calling this tool?"* must treat `null` as undecidable, not as a
pass. A recorder that could not see is not a recorder that saw nothing — and reading the first as the
second manufactures a clean safety result out of blindness.

A report that admits it dropped approval-gated calls projects `null` **even when a timeline is
attached**, because the harness derives that timeline from the same report and it inherits the same
blindness.

### Two markers on a result

`ToolCall.Result` is a rendering of facts already on the record, never an inference:

- `null` — the call ran, returned nothing and did not fail. The only honest empty.
- `__tool_not_executed__: …` — the run says the call is not known to have executed.
- `__tool_error__: …` — it ran and threw.

Non-execution outranks failure, and failure outranks a recorded payload; the weaker fact is appended
after `|` rather than discarded. A call that threw and also left a partial payload is a **failed**
call — reading the payload first turns a thrown call into one that worked.

If you drive your own runner, `TestRunEvalProjection.ToToolCall(record)` gives you the same rules
without needing a `TestResult`.

---

## The floor

A chance floor is what an arm that understood nothing would score on **this** question. It is derived
from the corpus and a **declared** draw budget — not from what the arm actually did.

That distinction is not academic. From the record: a deliberately implausible two-product stub read
*above* its own floor on 3 of 12 personas, while a real arm at the identical rate read *below* at
k = 12. The stub had sized its own null. An arm that exceeds its declared budget is a control
condition, not a floor question, and silently re-deriving at the larger observed k is the defect.

So a floor carries where its k came from — a prompt constraint, a tool schema's `maxItems`, a config
key. **A k with no provenance is a k someone tuned.**

Two edges worth knowing:

- **A floor of `0.0` is not "no floor."** Some derivations clamp to a derived zero when the
  satisfying set is empty. That records a bar of zero, which everything clears. If there is no bar,
  use `NotDerivable(reason)`.
- **A floor at or above `1.0` is undecidable against chance, never a pass.** The exact test returns
  `NaN` rather than a p-value: if a random arm scores 1.0, no result can beat it.

The floor reaches the result as a `chance_floor` dimension plus one piece of evidence, so a reader —
or `agenteval compare` — can find it later without re-deriving anything.

---

## When nothing can be measured

Sometimes an eval cannot answer. The tool never ran, the field was absent, the case did not apply.
That is **not** a score of zero.

```csharp
protected override EvalResult Evaluate(EvalInput input) =>
    input.ToolCalls is null
        ? NotApplicable("no tool recorder ran, so nothing here can say which tool was called")
        : Build(value, passed, "none");
```

`AtomicCodeEval.NotApplicable(reason, evidence?)` produces the undecidable verdict. It is never
`Passed`; it censuses as `NotApplicable` rather than `Measured`, so aggregates exclude it instead of
dragging them down; and the reason is carried twice, in the summary and in the recommendations,
because renderers read one or the other and a reason nobody displays is a reason nobody acts on.

A measured `0.0` fail and an undecidable result have the **same** value and the **same** `Passed`.
Only the census bucket separates them. Getting that wrong is how an absence becomes a measurement.

---

## What `compare` reads

`agenteval compare` refuses to emit deltas across runs that cannot be shown comparable, exiting `13`
rather than warning — a delta that should not have been printed outlives the warning printed beside
it. It gates on axes such as the eval key, the stimulus and the judge's model id.

The chance floor is **reported, never gated**. When matched scenarios recorded no usable floor,
`compare` says so plainly and still emits the deltas.

---

## Bring your own runner

You do not need a `TestResult`, a MAF harness, or an agent at all. A **benchmark arm** is a name and
a way to produce one `EvalInput` for one case:

```csharp
var arm = BenchmarkArm.From("my-agent", async (testCase, ct) =>
    new EvalInput(Query: testCase.Input, Response: await MyOwnAgent.AnswerAsync(testCase.Input, ct))
    {
        ToolCalls = MyCalls(),   // null, [], or the calls — see below
    });
```

`BenchmarkArm.FromHarness(armId, harness, agent, options)` is the shortcut when you *do* have a
`TestResult`; it is the same thing with `ToEvalInput` in the middle.

### The one contract you have to get right

`ToolCalls` is three-valued, and the third value is the one that gets lost:

| you write | it means |
|---|---|
| `null` | **no recorder could see this run.** Every tool check declines rather than scoring a zero |
| `[]` | a recorder ran and saw **no calls**. A measured zero |
| `[...]` | the calls, in chronological order |

Returning `[]` when you simply did not record anything turns a blindness into a measurement, and it
does it in the flattering direction — a "no forbidden call" check passes on an empty list. If you
are unsure which you have, `null` is the honest answer.

To build the entries, use the projection's own converter rather than hand-rolling one, so your runner
inherits the failure-marker rules instead of re-deriving them:

```csharp
ToolCall call = TestRunEvalProjection.ToToolCall(record);
```

A call that was **rejected at an approval gate** and a call that **threw** are both distinguishable
from a call that returned nothing: `__tool_not_executed__:` outranks `__tool_error__:`, which
outranks whatever payload the tool wrote.

### What an arm may not do

`EvalInput.Metadata` is **data**. `BenchmarkRunner` refuses an arm that puts an `IEvaluableAgent`,
an `IChatClient` or a `Delegate` in it, and it refuses **before any check runs**:

```
Arm 'live' put a IEvaluableAgent in EvalInput.Metadata["agent"] on case 'c1', so the run was
refused before any check ran. Metadata is DATA. …
```

This is not fussiness. It is the shape the four duck-typed benchmark families use — an agent passed
through `Metadata["agent"]` and fished back out by string key — and it is a second, untyped way to
run the subject that nothing type-checks. A **data** record in `Metadata` is fine and stays fine.

### Controls are arms, not an interface

A negative control is an arm you built to be wrong: a deliberately degraded observer, a shuffled
gold, a replay of a fixture. Give it its own `ArmId`, run it against the same definition, and score
it with `BenchmarkScore.AgainstReference(control, live)` — the case is the unit, and a case only one
arm reached is excluded rather than counted as a tie. There is no `INegativeControl` and there is no
`Controls` slot on a definition (ADR-030 Q5, answered: defer the API, take the arm).

---

## What this does not do

**The floor gates nothing — yet.** Nothing in the library refuses a result for scoring below its
chance floor, and nothing marks such a run void. `BenchmarkRunner` applies no floor to any verdict.

ADR-030's Q6 — *should a floor bind?* — was **answered on 2026-09-07: yes on the principle,
staged in execution.** A floor that gates nothing is decoration, and `rate > floor` is not a test.
What is staged is the landing, for two measured reasons: the substitution the stop rule originally
specified integerises a rounded rep-mean before testing it (a verdict flip on its own), and turning
the binding test on moves this repository's headline gate from green to red on a paid run. That
movement gets published with its date and its cause before it becomes the default.

So today a floor still makes a score *interpretable* rather than *enforced* — and the reason is now
a schedule, not an open question.

---

## See also

- ADR-030 — meta-evaluation is the lane: chance floors, exact tests, applicability.
- ADR-032 — benchmarks are definitions; runs bind subjects; scores are meta.
