# LongMemEval benchmark

> Status: beta in AgentEval `0.16.0-beta`.
>
> LongMemEval is an external academic benchmark (ICLR 2025). AgentEval runs the
> real `longmemeval_s_cleaned.json` dataset through an `IEvaluableAgent`, then
> grades each answer with the task-specific LongMemEval judge. AgentEval does not
> bundle or synthesize replacement dataset entries.

## What it measures

The cleaned dataset uses these six labels:

- `single-session-user`
- `single-session-assistant`
- `single-session-preference`
- `multi-session`
- `temporal-reasoning`
- `knowledge-update`

The published paper groups the three `single-session-*` labels into one
single-session category. Use the six labels when inspecting dataset/report
breakdowns and the paper's grouped view when comparing paper tables.

The normal path measures the complete memory system visible through the agent:
history injection, retrieval, ranking, context assembly, reducers, prompting,
and answer generation. It does not isolate any one of those components.

AgentMemory (or your own memory implementation) still owns:

- storage, indexing, embeddings, and retrieval;
- ranking, filtering, and context-budget decisions;
- session/user partitioning and lifecycle;
- reducer and summarization policy;
- emitting normalized evidence references when diagnostics are enabled.

AgentEval owns deterministic question selection, history injection, judging,
aggregation, safe evidence validation/diagnostics, oracle projection, and
reporting. It never reaches into AgentMemory's store to infer retrieval behavior.

## Scores and denominators

`ExternalBenchmarkResult.OverallAccuracy` and each `TypeResult.Accuracy` are
nullable percentages on a **0–100** scale:

```csharp
Console.WriteLine(
    result.OverallAccuracy is { } score ? $"{score:F1}%" : "n/a");
```

The default failure policy excludes inconclusive judge outcomes from the
accuracy denominator:

```text
overall accuracy = correct questions / scored questions * 100
```

The result separately reports selected, agent-completed, scored, correct,
incorrect, inconclusive, and agent-failure counts. A run with zero scored
questions has `OverallAccuracy == null`; it is inconclusive, not a 0% failure.
The CLI returns `GateInconclusive` for that case.

Do not use the `.NET` `P1` formatter on these properties: `57.7` is already
57.7%, and `57.7:P1` would incorrectly render as 5,770.0%.

## Dataset acquisition

Download `longmemeval_s_cleaned.json` from the
[LongMemEval cleaned dataset](https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/tree/main).
The [research repository](https://github.com/xiaowu0162/LongMemEval) contains
the reference implementation and cleanup pipeline.

Resolution order for the `subset` preset:

1. `ExternalBenchmarkOptions.DatasetPath`;
2. `LONGMEMEVAL_DATASET_PATH`;
3. `<workspace-root>/src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json`.

The `full` preset deliberately requires `LONGMEMEVAL_DATASET_PATH`.

## Presets

| Preset | Selection | Dataset requirement |
|---|---:|---|
| `subset` | 50 stratified questions by default | Explicit path, environment variable, or canonical local path |
| `full` | Entire cleaned dataset (~500 questions) | `LONGMEMEVAL_DATASET_PATH` required |

Question selection is reproducible when `RandomSeed` is set. Provider retries
can make total judge calls exceed one call per selected question, so use
`TotalLlmCalls`, not `selected * 2`, for accounting.

## CLI

```powershell
# Initialize the canonical store once.
agenteval init

# Default 50-question stratified run.
agenteval bench longmemeval --preset subset --subject MyAgent

# Deliberate full run.
$env:LONGMEMEVAL_DATASET_PATH = "C:\data\longmemeval_s_cleaned.json"
agenteval bench longmemeval --preset full --subject MyAgent
```

The built-in CLI binding requires `AZURE_OPENAI_ENDPOINT`,
`AZURE_OPENAI_API_KEY`, and `AZURE_OPENAI_DEPLOYMENT`. Programmatic callers can
use any `IChatClient` and any `IEvaluableAgent`.

The CLI writes a canonical manifest, summary, and `report-native.json`. The
native report intentionally preserves questions, gold answers, agent responses,
judge detail allowed by `JudgeEvidenceMode`, and normalized evidence allowed by
`EvidenceCaptureMode`. Treat it as sensitive data: restrict access, retention,
and publication.

The benchmark sample's JSON/HTML/PDF projection is safer by default: it contains
counts and typed status only. The sample writes the raw native sidecar only when
`AGENTEVAL_LONGMEMEVAL_WRITE_NATIVE_REPORT=true`.

## Judge failure policy

Configure `ExternalBenchmarkOptions.JudgeFailurePolicy`:

| Policy | Behavior after a non-binary judge outcome |
|---|---|
| `FailRun` | Throws immediately; use when every judgment is mandatory |
| `RetryThenInconclusive` | Retries up to `MaxJudgeRetries`, then retains `Correct == null` and excludes it from accuracy (default) |
| `RetryThenIncorrect` | Retries, then counts the item as incorrect for legacy/gate compatibility while retaining its typed judge status |

`MaxJudgeRetries` is bounded from 0 through 3. Cancellation is never converted
into an inconclusive result. Provider exceptions are represented by bounded safe
failure codes; provider messages and secrets are not copied into result fields.

`JudgeTemperature` defaults to `null`, which uses the provider/model default.
Set an explicit value only when the deployment supports it; some reasoning
deployments reject explicit temperature. `JudgeMaxOutputTokens` defaults to 256
and includes reasoning tokens on reasoning models. A very small budget can
produce empty or truncated judgments even when the visible answer should be only
`yes` or `no`.

Configure retained judge information separately:

| `JudgeEvidenceMode` | Retained judge information |
|---|---|
| `None` | No judge evidence |
| `Outcome` | Typed yes/no/empty/invalid/provider-error status |
| `Explanation` | Outcome plus bounded explanation |
| `Raw` | Bounded raw judge response; use only with an explicit data-handling decision |

## Retrieval evidence modes

Evidence instrumentation is optional and cannot change correctness:

| `EvidenceCaptureMode` | Behavior |
|---|---|
| `None` | Does not access `AgentResponse.AdditionalProperties`; no evidence fields are serialized |
| `References` | Retains bounded, content-free references and evaluator-derived diagnostics |
| `Full` | Also permits bounded evidence content; this can contain user data |

Only the reserved, versioned property
`QuestionEvidenceEnvelope.AdditionalPropertiesKey` is read. Arbitrary provider
properties are ignored. Invalid or hostile evidence is rejected to a safe
diagnostic status without changing the answer or judge score.

The evaluator derives top-K gold-session/turn presence, first-gold rank,
source-session diversity, timestamp coverage, and answer-context order from the
normalized references. Adapters must not put evaluator gold labels in the
envelope.

## Programmatic run

```csharp
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;

IChatClient judgeClient = /* task-specific judge deployment */;
IEvaluableAgent memoryAgent = /* the agent under test */;

var runner = LongMemEvalBenchmark.Subset(judgeClient);
var options = new ExternalBenchmarkOptions
{
    MaxQuestions = 50,
    RandomSeed = 42,
    JudgeFailurePolicy = JudgeFailurePolicy.RetryThenInconclusive,
    MaxJudgeRetries = 1,
    JudgeTemperature = null,       // provider default; widest model compatibility
    JudgeMaxOutputTokens = 256,    // includes reasoning tokens
    JudgeEvidenceMode = JudgeEvidenceMode.Outcome,
    EvidenceCaptureMode = EvidenceCaptureMode.References,
};
var config = new AgentBenchmarkConfig
{
    AgentName = memoryAgent.Name,
    ModelId = "answer-deployment",
    MemoryProvider = "AgentMemory",
};

var result = await runner.RunAsync(memoryAgent, config, options);
Console.WriteLine(
    result.OverallAccuracy is { } score
        ? $"{score:F1}% ({result.CorrectQuestions}/{result.ScoredQuestions} scored)"
        : $"n/a (0/{result.SelectedQuestions} scored)");
```

## Adapter evidence example

An agent adapter can attach normalized retrieval evidence to its response:

```csharp
using AgentEval.Core;
using AgentEval.Memory.External.Models;

var normalizedEvidence = new QuestionEvidenceEnvelope
{
    SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
    Retrieved =
    [
        new EvidenceReference
        {
            Id = "memory-item-42",
            Rank = 1,
            SimilarityScore = 0.91,
            SourceSessionId = "session-7",
            SourceTurnIndex = 3,
        }
    ],
    AnswerContext =
    [
        new EvidenceReference
        {
            Id = "memory-item-42",
            Rank = 1,
            SourceSessionId = "session-7",
            SourceTurnIndex = 3,
            AnswerContextOrder = 1,
        }
    ],
};

return new AgentResponse
{
    Text = answerText,
    AdditionalProperties = new Dictionary<string, object?>
    {
        [QuestionEvidenceEnvelope.AdditionalPropertiesKey] = normalizedEvidence,
    },
};
```

Use stable adapter-owned identifiers. In `References` mode, `Content` is removed.
In `Full` mode, content is bounded but still sensitive.

## Paired normal/oracle diagnostic

`RunPairedAsync` runs the normal memory agent to completion first, then uses an
isolated retrieval-bypassing reader over an evaluator-projected answer-session
history. One frozen selected-ID set is used for both paths. Normal and oracle
scores, calls, and evidence remain separate; the oracle gap is diagnostic and
never changes the normal score.

```csharp
IChatClient judgeClient = /* LongMemEval judge */;
IChatClient answerClient = /* same answer deployment as the normal path */;
IEvaluableAgent normalAgent = /* agent with its real memory pipeline */;

var runner = LongMemEvalBenchmark.Subset(judgeClient);
var normalConfig = new AgentBenchmarkConfig
{
    AgentName = normalAgent.Name,
    ModelId = "answer-deployment",
    ModelVersion = "2026-06-01",
    Temperature = 0,
    MaxTokens = 512,
};
var oracleConfig = new AgentBenchmarkConfig
{
    AgentName = "LongMemEval oracle reader",
    ModelId = normalConfig.ModelId,
    ModelVersion = normalConfig.ModelVersion,
    Temperature = normalConfig.Temperature,
    MaxTokens = normalConfig.MaxTokens,
};

var paired = await runner.RunPairedAsync(
    normalAgent,
    normalConfig,
    answerClient,
    oracleConfig,
    options);

static string Percent(double? value) => value is { } score ? $"{score:F1}%" : "n/a";
static string Points(double? value) =>
    value is { } points ? $"{points:F1} percentage points" : "n/a";
Console.WriteLine($"Normal: {Percent(paired.Normal.OverallAccuracy)}");
Console.WriteLine($"Oracle: {Percent(paired.Oracle.OverallAccuracy)}");
Console.WriteLine($"Gap:    {Points(paired.OracleGapPercentagePoints)}");
```

The declared answer model/version/temperature/max-token values must match. This
prevents a model change from being mislabeled as a retrieval gap. AgentEval
records observable client model metadata but cannot prove the configuration
inside an opaque normal-agent implementation.

Interpretation:

- low normal and high oracle suggests retrieval/context assembly is the main bottleneck;
- low normal and low oracle suggests answer-model, prompt, or task difficulty dominates;
- a negative gap warrants investigation; it is not automatically a normal-path win.

## Pinning the answer model

`JudgeTemperature` pins the grader. `AnswerTemperature` and `AnswerSeed` pin the
call being graded:

```csharp
var options = new ExternalBenchmarkOptions
{
    MaxQuestions = 50,
    RandomSeed = 42,
    AnswerTemperature = 0.0,   // passed through as given — see the note below
    AnswerSeed = 4242,
};
```

Why it matters: left at the provider default the answer model disagrees with
itself, and that disagreement is the floor beneath which no memory improvement is
detectable. Repeats of one configuration can flip verdicts with byte-identical
retrieval — same sessions, same config, same retrieved items — and nothing in a
default result says so.

**AgentEval cannot set sampling on an agent it does not own.** `IEvaluableAgent`
is prompt-in/text-out with no provider surface. Your adapter opts in:

```csharp
public sealed class MyAgent : IEvaluableAgent, IAnswerSamplingConfigurableAgent
{
    private ChatOptions _options = new();

    public AnswerSamplingAcknowledgement ConfigureAnswerSampling(AnswerSamplingRequest request)
    {
        _options = new ChatOptions { Temperature = (float?)request.Temperature, Seed = request.Seed };
        return AnswerSamplingAcknowledgement.AppliedFrom(request);   // only claim what you applied
    }
    // ...
}
```

`ChatClientAgentAdapter` (what `chatClient.AsEvaluableAgent(...)` returns) and
`LongMemEvalOracleReader` already implement it, so AgentEval's own agents and the
oracle arm are pinnable without extra code.

`ExternalBenchmarkResult.AnswerSampling` reports what each parameter's request
actually achieved, per question:

| Disposition | Means |
|---|---|
| `NotRequested` | You did not ask for it. |
| `NotSupportedByAgent` | The agent does not implement the interface. The run is **not** pinned. |
| `DeclinedByAgent` | The agent took the request and declined this parameter. |
| `SentUnverified` | The agent reported attaching it and the provider did not reject it. Both halves are weak: the attachment is the adapter's own claim, and a provider that ignores a parameter answers exactly like one that used it. |
| `SentAndEchoed` | The provider echoed the same value back — the strongest available confirmation. |
| `EchoedDifferentValue` | The provider echoed a *different* value. Not reproducible on this parameter. |
| `RejectedByProvider` | The provider refused the call because of it. |

Two deliberate behaviours:

- **Values pass through as given.** AgentEval does not assume `0` works; some
  deployments reject an explicit temperature, and some reject `0` specifically.
- **A rejection fails the question** with `SafeFailureCode ==
  "answer_sampling_rejected"` rather than being retried without the parameter.
  A silent downgrade would produce a run that looks pinned and is not.

`SentUnverified` is never rounded up to "honoured". A provider that ignores a seed
answers exactly like one that used it, and only an echo distinguishes them —
surface one on `AgentResponse.AdditionalProperties` under `"seed"` or
`"temperature"` if your provider returns it.

If you cannot change the adapter, you can still pin the call one layer down by
wrapping the agent's `IChatClient` in a `DelegatingChatClient` that fills the
values when the caller left them unset. The run then reports
`NotSupportedByAgent`, which stays accurate: AgentEval's request did not reach the
call, and something else pinned it.

**To find out empirically whether the seed took effect**, run the same sample twice
under the same `RandomSeed` and diff `QuestionResult.AgentResponse` across the two
results. Identical text on identical inputs is evidence the seed was honoured;
differing text on identical inputs is proof it was not. That is a stronger claim
than any disposition here can make, and it costs a second run —
`JudgeAgreementHarness` does the same thing for the judge side.

## The oracle arm on its own

`RunPairedAsync` runs the normal arm and the ceiling together. `RunOracleAsync`
runs just the ceiling, and returns the ordinary result shape — a `QuestionResult`
per question, `SampleComposition`, the usual counters:

```csharp
var runner = LongMemEvalBenchmark.Subset(judgeClient);
var ceiling = await runner.RunOracleAsync(answerClient, options);

Console.WriteLine($"Ceiling: {ceiling.OverallAccuracy:F1}%");
```

The arm measures the dataset and the answer model, not a memory system: nothing is
stored and nothing is retrieved. Its number is the ceiling every other arm is read
against, which is a reason for everyone to run the *same* one rather than each
re-deriving it — `LongMemEvalOracleProjector` and `LongMemEvalOracleReader` are
public for that purpose.

Two controls move it off the ceiling deliberately:

```csharp
var stressed = await runner.RunOracleAsync(answerClient, options, new LongMemEvalOracleOptions
{
    DistractorSessions = 25,     // non-evidence sessions from the question's OWN haystack
    GoldSessionFraction = 0.5,   // keep half the evidence, rounded up
});

var realised = stressed.OracleProjection!;
Console.WriteLine($"evidence kept {realised.GoldSessionsKept}/{realised.GoldSessionsAvailable}");
Console.WriteLine($"distractors added {realised.DistractorSessionsAdded} " +
                  $"(request fully met: {realised.DistractorRequestFullyMet})");
```

- Distractors come from the question's own haystack. Sessions borrowed from another
  question are about another user's life and are trivially ignorable, so padding
  with them measures a strawman.
- `GoldSessionFraction` rounds **up** and never to zero: rounding a
  one-evidence-session question to zero makes it unanswerable by construction, and
  the score would then measure the arithmetic. `0` is rejected outright.
- Both draws are reproducible under `RandomSeed`, derived per question id, so
  adding a question does not re-roll another question's sessions.
- The realised counts are reported because a level that degraded nothing and a
  level whose degradation did not matter are different findings that look identical
  in a score.
- Selected sessions keep their dataset order. Appending distractors after the
  evidence would put the gold first in every question and measure position.

## Time-grounded runs

`TemporalGrounding` delivers session dates as real `DateTimeOffset` values through
`ITimestampedHistoryInjectableAgent`, and under `TimestampsOnly` removes the
harness's in-text date scaffolding, so a system that does not place messages in
time has nothing left to read. It applies to any LongMemEval-shaped dataset, and
ships with a small authored corpus of clock-dependent questions.

See [Time-grounded probe](time-grounded-probe.md).

## Safe report projection

Use `LongMemEvalEvalResultAdapter.ToEvalResult` when an `EvalResult` tree is
required for HTML/PDF or generic consumers. The projection exposes execution,
judge, evidence-capture, denominator, and call status without copying raw
question/answer/evidence content.

```csharp
var report = LongMemEvalEvalResultAdapter.ToEvalResult(
    result,
    presetName: "subset",
    judgeModel: "judge-deployment");
```

An inconclusive leaf has `Label == "inconclusive"` and is aggregated as a
warning by the sample's canonical summary. `EvalScore.Passed` remains a Boolean
because that shared contract predates tri-state results; consumers must use
`Label` and the explicit count dimensions for LongMemEval tri-state semantics.

## Migration to 0.16.0-beta

When upgrading from the earlier LongMemEval implementation:

1. Treat `OverallAccuracy`, `TaskAveragedAccuracy`, and per-type accuracy as
   nullable 0–100 percentages.
2. Replace total-selected denominators with `ScoredQuestions`; display
   `SelectedQuestions` separately.
3. Handle `QuestionResult.Correct == null`, `JudgeStatus`,
   `ExecutionStatus`, and safe failure codes.
4. Choose a `JudgeFailurePolicy`; the new default is
   `RetryThenInconclusive` with one retry.
5. Remove assumptions that every judge deployment accepts `Temperature=0`.
   Leave `JudgeTemperature` null or configure it explicitly after a provider
   compatibility check, and budget reasoning tokens with `JudgeMaxOutputTokens`.
6. Update percent output from `{score:P1}` to `{score:F1}%`.
7. If you emit retrieval diagnostics, use
   `QuestionEvidenceEnvelope.AdditionalPropertiesKey` and choose an
   `EvidenceCaptureMode`. `Full` is an explicit content-persistence decision.
8. Use `RunPairedAsync` for oracle diagnostics; do not combine oracle questions,
   scores, evidence, or calls with the normal result.
9. Expect zero-scored CLI runs to return `GateInconclusive`; canonical summaries
   encode that supported state as `WARN`.
10. Access-control `report-native.json`. Prefer
   `LongMemEvalEvalResultAdapter` for content-free generic reports.

No retrieval implementation moved into AgentEval. Existing AgentMemory storage,
retrieval, ranking, reducers, and context-provider configuration remain the
application's responsibility.

## Limitations

- LongMemEval is English-only and LLM-judged.
- Dataset and model/deployment identity must be held constant for comparisons.
- Small subsets have high variance and are not paper-comparable audit results.
- The built-in 50% CLI threshold is a smoke gate, not a published production
  acceptance criterion.
- Evidence diagnostics measure what the adapter reports. They do not independently
  inspect or attest the memory store.
- The oracle is an upper-bound diagnostic for the selected answer sessions, not a
  replacement for normal-path evaluation.
- Answer-sampling dispositions record what reached the provider, not what the
  provider did with it. `SentUnverified` is the honest ceiling of that claim, and
  even `SentAndEchoed` is confirmation of receipt rather than proof of determinism.
- Time-grounding removes the date scaffolding *AgentEval* adds. Dates the speakers
  themselves wrote stay in the conversation;
  `TemporalGroundingReport.SessionsWithDateLikeContent` counts how many sessions
  still contain one.

See also [Memory benchmark](../memory/getting-started.md), the
[CLI reference](../../cli.md), and the
[LongMemEval paper](https://arxiv.org/abs/2410.10813). The
[2026-07-29 live validation](live-validation-2026-07-29.md) records the
provider-compatibility and judge-reliability calibration for this release.
