# ASSERT Interoperability

[ASSERT](https://github.com/responsibleai/ASSERT) (`assert-ai` on PyPI) is Microsoft Responsible AI's Python harness.
It turns a written behavior description into a taxonomy and test cases, sends the cases to a target, and has an LLM
judge flag violations. Its two headline numbers are how often the target did something it must not do (harm) and
how often it failed to help with something it was allowed to do (over-refusal).

AgentEval works with ASSERT without either tool changing:

| | What it does | Where |
|---|---|---|
| **1. Target** | Serves a .NET chat client or Microsoft Agent Framework agent at the HTTP endpoint ASSERT calls, tool calls included | `AssertAiTarget`, `AssertAiTargetServer`, `agenteval assert-ai serve` |
| **2. Import** | Reads an ASSERT run directory into AgentEval results, one per test case, so ASSERT's verdicts can sit in a `CompositeEval` beside AgentEval's own checks | `AssertAiRun`, `AssertAiResults`, `AssertAiVerdictEval`, `AssertAiTranscripts`, `agenteval assert-ai import` |
| **3. Headline numbers** | ASSERT's harm and over-refusal rates, computed exactly as ASSERT computes them | `AssertAiHeadline` |
| **4. Export** | Writes conversations AgentEval already has as an ASSERT judge-only run, so ASSERT's judge grades them without calling the agent again | `AssertAiJudgeKit`, `agenteval assert-ai export` |
| **5. Calibrate** | Compares ASSERT's verdicts on AgentEval's labelled cases with their labels: accuracy, Cohen's κ, dangerous errors | `AssertAiCalibration`, `agenteval assert-ai calibrate` |

The types are in the `AgentEval` package (namespace `AgentEval.Interop.AssertAi`). Sample P1
(`dotnet run -- 106` in `samples/AgentEval.Samples`) shows all of them; `samples/interop/assert-ai/` explains how to
run it against a real ASSERT.

## Which ASSERT

The formats are **assert-ai 0.3.0**. They were read from ASSERT's source (release `v0.3.0` and `main` at
`e03aa809`, 2026-10-06, which do not differ in any of these formats), not from its documentation, which disagrees
with the code in places. ASSERT writes no version into its files and has no versioned schema, so a run cannot say
which ASSERT wrote it: AgentEval reads every run as 0.3 and labels its results `assert-ai-0.3`. The tests check the
reader against ASSERT's own test expectations and fixtures (`tests/AgentEval.Tests/Interop/AssertAi/Fixtures`).

**Not yet verified:** a round trip with a running ASSERT (ASSERT driving the target, then its run imported). Until it
has run, compatibility rests on ASSERT's source and its own test files.

## 1. A .NET agent as ASSERT's target

ASSERT POSTs `{"message": …, "history": [{"role": "user"|"assistant", "content": …}]}`, where `history` already ends
with the current message, and reads back `{"response": …, "events": […]}`. It sends no system prompt, no tool
messages and no headers.

```csharp
using AgentEval.Interop.AssertAi;

// Any IChatClient (add UseFunctionInvocation() for tools to run) …
var target = new AssertAiTarget(chatClient, new ChatOptions { Tools = [lookup] }, systemPrompt: "You are …");

// … or a Microsoft Agent Framework agent. ASSERT sends the whole conversation each turn, so no session is needed.
var target2 = new AssertAiTarget(async (messages, ct) =>
    (await agent.RunAsync(messages, null, null, ct)).Messages.ToList());

// Host it in your web app …
app.MapPost("/assert", async (HttpRequest request) =>
    Results.Content(await target.RespondJsonAsync(await new StreamReader(request.Body).ReadToEndAsync()), "application/json"));

// … or with the built-in server.
await using var server = AssertAiTargetServer.Start(target, port: 8765, path: "/assert");
```

`agenteval assert-ai serve --from-env` serves the model your provider variables select, without tools.

What to know:

- **Tool calls reach ASSERT's judge only as results.** ASSERT turns a `tool_call` event with no matching
  `tool_result` into an empty assistant message and nothing else. So every call is sent as a `tool_result` event
  carrying its name and arguments (as ASSERT's own reference endpoint does), and a call that got no result is sent
  with empty content, so the judge still sees that it was made.
- **Use `localhost`, not `127.0.0.1`.** ASSERT refuses a literal private address unless
  `ASSERT_ALLOW_PRIVATE_ENDPOINTS=1` is set; the host name `localhost` is allowed. A target ASSERT reaches from a
  container (`host.docker.internal`) needs that variable.
- **Answer within ASSERT's timeout**: `pipeline.inference.tool_timeout_s`, 60 seconds when unset, with no retry.
  ASSERT runs up to `pipeline.inference.concurrency` cases at once (10 by default); the built-in server answers them
  concurrently.
- The reply must be `application/json`. A 4xx or 5xx reply is a target error to ASSERT: it drops a prompt case and
  records a scenario case as `target_error`.
- ASSERT redacts what looks like a credential in the reply before its judge sees it.

## 2. Reading an ASSERT run

```csharp
var run = AssertAiRun.Read("artifacts/results/my-suite/20261008T100000");   // the directory holding scores.jsonl
foreach (var (key, result) in AssertAiResults.ToEvalResults(run))
    Console.WriteLine($"{key}: {result.Score.Label} {result.Details.Summary}");
```

```
agenteval assert-ai import artifacts/results/my-suite/20261008T100000 [--format text|json|markdown] [-o report]
                           [--taxonomy taxonomy.json] [--test-set test_set.jsonl] [--calibration calibration.json]
                           [--max-harm-rate 0.05] [--max-over-refusal-rate 0.2]
```

The reader takes the taxonomy from the suite directory (the one ASSERT's own metrics read) and the test set the run's
`manifest.json` names. Test cases are keyed by type and id (`prompt:test_case_000001`): ASSERT numbers them by
position, and a prompt case and a scenario case can share an id.

**How each case becomes a result.** ASSERT has some result shapes that read as good news when they are not; the
reader maps them to what they mean:

| ASSERT | AgentEval result |
|---|---|
| Judged, a category that is not permissible violated | `fail`, severity `high` (harm) |
| Judged, only a permissible category violated | `fail`, severity `medium` (failing to help) |
| Judged, another dimension flagged (a preset's `wrong_tool`, …) | `fail`, severity `medium` |
| Judged, no violation | `pass` |
| Judged, no category of the taxonomy relevant | `inapplicable`: ASSERT counts it in neither rate |
| `judge_failed`, or `filter_skipped` (the judge refused the input) | `error`. ASSERT writes `0.0` scores for a failed judge; they are never read as "no violation" |
| `scoring_skipped` (the target or tester refused the input, or the target errored) | `skipped`, with ASSERT's reason |
| A case in the test set or the transcripts with **no score row** | `skipped`, named. ASSERT writes no row when a judge call is stopped by a content filter or a prompt case's target call fails, so its own numbers leave these cases out without saying so |

A row is judged only under ASSERT's own rule (`infer_judge_status`): its verdict must hold a value of the right kind
for every dimension the run asked for. Each dimension becomes a sub-result; an ordinal grade is reported as evidence,
not judged, because which end of a custom scale is good is the scale's author's call. Every result says whether
ASSERT's judge has been calibrated (part 5).

**ASSERT's verdicts in a composite.** `AssertAiVerdictEval` finds a case by `EvalInput.CaseId`;
`AssertAiTranscripts.Read` turns `inference_set.jsonl` into inputs with those ids, so AgentEval's own checks run on
exactly what ASSERT's judge saw:

```csharp
var composite = new CompositeEval("billing_case", "Billing case", "interop", "1.0.0",
    [new EvalComponent(new AssertAiVerdictEval(run)), new EvalComponent(new NoUnconfirmedPayoutChange())],   // sample P1's tool check
    MinAggregation.Instance);
foreach (var (_, input) in AssertAiTranscripts.Read(Path.Combine(run.RunDirectory, "inference_set.jsonl")))
    Console.WriteLine((await composite.EvaluateAsync(input)).Score.Label);
```

## 3. ASSERT's headline numbers

`AssertAiHeadline.Compute(run)` follows `assert_ai/results.py`:

- prompt and scenario cases are reported apart (ASSERT tells them apart by whether a tester model is set) and never
  pooled;
- only judged (`ok`) rows count;
- **harm** (`not_permissible_policy_violation_rate`): of the cases where the judge found a relevant category that is
  not permissible, the share where one of them is violated, one vote per case;
- **over-refusal** (`permissible_policy_violation_rate`): the same over permissible categories;
- a case with no relevant category of a kind is left out of that rate's denominator and counted apart; a rate with an
  empty denominator is `null`.

ASSERT prints its headline without a confidence interval; AgentEval adds a 95% Wilson interval, labelled as its own.
`--max-harm-rate` and `--max-over-refusal-rate` turn a rate into a CI gate: exit 1 above the limit, 11 when no rate
could be measured.

## 4. Writing AgentEval's conversations for ASSERT's judge

```
agenteval assert-ai export --golden tests/AgentEval.Tests/Agentic/Calibration/Golden/golden-hate-unfairness.jsonl \
                           --taxonomy taxonomy.json --judge-model azure/gpt-5.4 --out assert-kit
assert-ai run --config assert-kit/assert-judge-config.yaml
```

`export` writes an ASSERT run that needs only ASSERT's judge stage: `results/<suite>/taxonomy.json`,
`results/<suite>/<run>/inference_set.jsonl` (each row in the shape ASSERT's inference stage writes), a judge-only
`assert-judge-config.yaml`, and `agenteval-cases.json`, which maps ASSERT's positional ids back to AgentEval's case ids
with each case's label. From code: `AssertAiJudgeKit.Write(directory, transcripts, options)`, with transcripts built
from chat messages or `AssertAiTranscript.FromEvalInput`.

ASSERT's judge cannot run without a taxonomy (`behavior_categories` with unique names and `permissible` flags), so
`--taxonomy` is required: write one for what the cases test, or reuse one ASSERT generated. A case's retrieved context
is not sent; ASSERT's transcripts have no place for it. `artifacts_root` is written as an absolute path because ASSERT
resolves a relative one against its own install; `--assert-root` writes the path ASSERT will see when it runs
elsewhere.

## 5. Calibrating ASSERT's judge

ASSERT's documentation says its automated judgments are "signals for investigation, not definitive truth", and it
ships no judge-agreement measurement. AgentEval's labelled golden cases supply one:

```
agenteval assert-ai calibrate assert-kit/results/agenteval/judge-1 --cases assert-kit/agenteval-cases.json -o calibration.json
agenteval assert-ai import <a run judged by the same model> --calibration calibration.json
```

`calibrate` reports, over the cases ASSERT's judge decided: accuracy with a 95% Wilson interval, Cohen's κ, dangerous
errors (a labelled failure the judge passed) and false alarms. A case the judge did not decide (failed, not judged, no
score row, or no relevant category) is counted as not measured, never as agreement. Imported results of the same
judge model then carry those numbers instead of "not calibrated".

## Not included

- **Running ASSERT from .NET.** ASSERT has no public Python API and no judge service; it runs as its own program.
- **ASSERT's case generation** (systematize, test set) stays in ASSERT.
- **Its OpenTelemetry import** (`assert-ai judge-traces`): it converts traces but writes rows without the case ids
  its judge stage needs.
