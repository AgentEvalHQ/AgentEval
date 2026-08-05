# LongMemEval trustworthiness and diagnostics improvement plan

> **Status:** implementation complete and merged into `main`
> **Prepared:** 2026-07-29
> **Target baseline:** the current AgentEval release line
> **Scope:** AgentEval's .NET LongMemEval runner, judge, result contracts, reporting, tests, samples, and documentation
> **Non-goal:** implementing AgentMemory retrieval, extraction, reranking, graph, or storage logic

## Contents

- [Executive decision](#executive-decision)
- [Implementation tracker](#implementation-tracker)
- [Evidence reviewed](#evidence-reviewed)
- [Current architecture](#current-architecture)
- [Root-cause analysis](#root-cause-analysis)
- [Assessment of question d24813b1](#assessment-of-question-d24813b1)
- [Additional audit findings](#additional-audit-findings)
- [Required invariants](#required-invariants)
- [Target API design](#target-api-design)
- [Evidence extension design](#evidence-extension-design)
- [Oracle-reader design](#oracle-reader-design)
- [Aggregation and reporting semantics](#aggregation-and-reporting-semantics)
- [Backward compatibility and migration](#backward-compatibility-and-migration)
- [Phased implementation plan](#phased-implementation-plan)
- [Test matrix](#test-matrix)
- [Security and privacy review](#security-and-privacy-review)
- [Definition of done](#definition-of-done)
- [AgentEval versus memory-system responsibilities](#agenteval-versus-memory-system-responsibilities)

## Executive decision

The reported blank preference judgment is a real evaluator defect, not evidence that the
agent answer was wrong.

`LongMemEvalJudge` currently reduces every response other than a leading `yes` to
`Correct = false`. That includes an explicit `no`, an empty response, malformed output,
and a caught provider exception. `LongMemEvalBenchmarkRunner` then places all of those
results in the ordinary product-quality denominator. The implementation therefore cannot
distinguish "the answer was judged incorrect" from "the judge did not produce a usable
judgment."

The fix must be semantic, not a special case for `d24813b1`:

1. Represent judge outcomes as `Yes`, `No`, `Empty`, `Invalid`, or `ProviderError`.
2. Make binary correctness nullable: `true` only for `Yes`, `false` only for `No`, and
   `null` for every inconclusive outcome.
3. Retry only inconclusive outcomes, under an explicit bounded policy, and count every
   attempted call exactly once.
4. Exclude inconclusive and agent-failed questions from quality denominators by default.
5. Preserve successful yes/no JSON values and the existing 0–100 public accuracy scale.
6. Add a content-free, allowlisted evidence envelope that an agent adapter can return
   without receiving evaluator-side gold labels.
7. Add a separate oracle-reader result using the same selected question IDs and answer
   model, with no path for oracle evidence to enter the normal run.
8. Correct the existing CLI percentage-scale defect and failure-count presentation as part
   of the same trustworthiness release.

The preference prompt already contains the official flexible criterion. It should be
aligned textually with the official template and protected by a prompt-fidelity test, but
prompt wording is not the cause of the blank result.

## Implementation tracker

Percentages below track implementation, not completion of this planning document. A task
becomes `100%` only after its focused tests pass and its task review has fixed all findings.
`Reviewed` becomes `✅` only after that review.

| Phase | Task | Description | Effort | % Done | Reviewed | Depends on | Implementation notes |
|---|---|---|---:|---:|:---:|---|---|
| 0 | 0.1 | Freeze current successful-result JSON and public API baselines | S | 100 | ✅ | — | Yes/no values, legacy status inference, and 0–100 scale are covered |
| 0 | 0.2 | Add red-first judge, runner, leakage, evidence, and compatibility tests | M | 100 | ✅ | 0.1 | Judge, runner, leakage, evidence, compatibility, and oracle matrices complete |
| 0 | 0.R | Contract and threat-model review | S | 100 | ✅ | 0.1–0.2 | Judge, evidence, and oracle boundaries reviewed and promoted |
| 1 | 1.1 | Add typed judge outcomes and validated judge options | M | 100 | ✅ | 0.R | Nullable results, owned statuses, and retry bound `0..3` implemented |
| 1 | 1.2 | Extract strict response parser and align official prompts | M | 100 | ✅ | 1.1 | All five official prompt families plus general preference corpus covered |
| 1 | 1.3 | Implement retry, cancellation, sanitization, and call accounting | M | 100 | ✅ | 1.1–1.2 | Per-question counters are the sole source for total LLM calls |
| 1 | 1.4 | Propagate outcomes through runner and aggregates | L | 100 | ✅ | 1.3 | Null accuracy, explicit denominators, and distinct failure counts implemented |
| 1 | 1.R | Judge-semantics promotion review | M | 100 | ✅ | 1.1–1.4 | Fixed duplicate accounting and legacy TypeResult denominator findings |
| 2 | 2.1 | Add generic typed question-evidence contracts | M | 100 | ✅ | 1.R | Versioned envelope, content-free references, modes, diagnostics, and public bounds implemented |
| 2 | 2.2 | Capture reserved, allowlisted `AgentResponse.AdditionalProperties` evidence | L | 100 | ✅ | 2.1 | Copy-owned strict DTO/JSON bridge; arbitrary provider properties and objects are never retained |
| 2 | 2.3 | Derive evaluator-side retrieval diagnostics from gold labels | L | 100 | ✅ | 2.2 | Post-answer top-K/session/turn/rank/diversity/timestamp/order diagnostics implemented |
| 2 | 2.4 | Serialize evidence according to capture mode | M | 100 | ✅ | 2.2–2.3 | `None` performs no property access and omits both evidence fields; invalid evidence cannot change score |
| 2 | 2.R | Evidence security and compatibility review | M | 100 | ✅ | 2.1–2.4 | Fixed hostile-dictionary escape; 35 focused tests and 572 tests × 3 TFMs pass |
| 3 | 3.1 | Add selected-ID oracle projection | M | 100 | ✅ | 1.R | Deep-clones only labelled sessions; strips session IDs, answer IDs, and turn labels |
| 3 | 3.2 | Add retrieval-bypassing oracle reader using the answer client | L | 100 | ✅ | 3.1 | Direct isolated chat path clears per-question state and records declared/reported model identity |
| 3 | 3.3 | Add paired normal/oracle result without score mixing | M | 100 | ✅ | 3.2 | One frozen ID set; normal completes first; separate results, counters, evidence, and diagnostic gap |
| 3 | 3.R | Oracle integrity review | M | 100 | ✅ | 3.1–3.3 | 9 focused tests; 581 tests × 3 TFMs pass; metadata capture hardened and solution builds |
| 4 | 4.1 | Correct CLI denominators, counts, threshold, and percent rendering | M | 100 | ✅ | 1.R | Mixed and zero-scored command tests verify 0–100 output, scored denominators, WARN persistence, and inconclusive exit |
| 4 | 4.2 | Update native report, sample synthesis, and Mission Control assumptions | M | 100 | ✅ | 1.R, 2.R | Reusable content-free adapter, explicit status/count dimensions, warning aggregation, and opt-in sensitive sample-native report |
| 4 | 4.3 | Update docs, programmatic sample, and `0.16.0-beta` migration note | M | 100 | ✅ | 3.R, 4.1–4.2 | Correct six labels, scales, policies, evidence adapter, paired oracle, ownership, privacy, and migration documented |
| 4 | 4.R | Reporting and documentation review | S | 100 | ✅ | 4.1–4.3 | 2 CLI + 41 evidence/report tests passed; samples build clean; DocFX build succeeded; review hardened thresholds and status sanitization |
| 5 | 5.1 | Run full offline test suite across supported TFMs | M | 100 | ✅ | 4.R | Fresh integrated build: 0 errors and 175 inherited analyzer/XML-doc warnings; 28,745 tests passed, 4 intentional manual skips, 0 failures; Memory: 595 × 3 |
| 5 | 5.2 | Run authorized live diagnostic and oracle comparison | M | 100 | ✅ | 5.1 | Seeded 10-item `gpt-5.5` run including `d24813b1`: normal 8/9 (88.9%, 90% completion with induced empty); oracle 10/10 (100%); 11.1 pp gap; no sensitive content persisted |
| 5 | 5.3 | Final API, security, and migration review | M | 100 | ✅ | 5.1–5.2 | Fixed reasoning-model temperature/output-budget compatibility, bounded `JsonElement` capture before allocation, and recorded evidence/judge policies in reports |
| 5 | 5.R | Release-readiness review | S | 100 | ✅ | 5.3 | All feature acceptance criteria complete; full tests pass, build has 0 errors with inherited warnings recorded honestly; DocFX succeeds with 32 pre-existing warnings and no LongMemEval warnings |

Estimated implementation size: roughly 8–12 focused engineering days, excluding live
provider latency and review turnaround. Phase 1 is the critical correctness fix and should
ship before evidence or oracle support if the work must be split.

## Evidence reviewed

### Repository implementation

- `src/AgentEval.Memory/External/LongMemEval/LongMemEvalJudge.cs`
- `src/AgentEval.Memory/External/LongMemEval/LongMemEvalJudgePrompts.cs`
- `src/AgentEval.Memory/External/LongMemEval/LongMemEvalBenchmarkRunner.cs`
- `src/AgentEval.Memory/External/LongMemEval/LongMemEvalHistoryFormatter.cs`
- `src/AgentEval.Memory/External/LongMemEval/LongMemEvalEntry.cs`
- `src/AgentEval.Memory/External/LongMemEval/LongMemEvalDataLoader.cs`
- `src/AgentEval.Memory/External/Models/ExternalJudgmentResult.cs`
- `src/AgentEval.Memory/External/Models/ExternalBenchmarkResult.cs`
- `src/AgentEval.Memory/External/Models/ExternalBenchmarkOptions.cs`
- `src/AgentEval.Abstractions/Core/IEvaluableAgent.cs`
- `src/AgentEval.Abstractions/Core/IHistoryInjectableAgent.cs`
- `src/AgentEval.Cli/Commands/BenchLongMemEvalCommand.cs`
- LongMemEval tests, samples, native reports, and getting-started documentation

### Benchmark sources

- [Official LongMemEval repository](https://github.com/xiaowu0162/LongMemEval)
- [Official QA evaluation script](https://github.com/xiaowu0162/LongMemEval/blob/main/src/evaluation/evaluate_qa.py)
- [LongMemEval paper](https://arxiv.org/abs/2410.10813)
- The repository's real `longmemeval_s_cleaned.json` and
  `longmemeval_oracle.json` data

The official evaluator uses a flexible preference rubric: a response need not satisfy every
rubric point; it is correct when it correctly recalls and uses the user's personal
information. The official data exposes `answer_session_ids` and turn-level `has_answer`
labels for evaluator-side retrieval analysis.

## Current architecture

The current runner performs this sequence for each selected entry:

1. Reset the agent when it implements `ISessionResettableAgent`.
2. Format and inject `haystack_sessions`, or prepend them as a text blob.
3. Invoke the agent once.
4. Build an `ExternalBenchmarkQuestion` containing the gold answer.
5. Invoke `LongMemEvalJudge` once.
6. Store a required Boolean `Correct` and a 0-or-100 `RawScore`.
7. Aggregate every selected question into overall and per-type denominators.

The history formatter intentionally drops `has_answer`, and it does not add
`answer_session_ids` to the agent prompt. That is a sound starting boundary and must be
retained.

`AgentResponse` already has `AdditionalProperties`, but the runner currently discards them
and keeps only `response.Text`. No extension point can therefore contribute retrieval
diagnostics to the native report.

## Root-cause analysis

### Why blank became incorrect

The decisive line in `LongMemEvalJudge.JudgeAsync` is equivalent to:

```csharp
var correct = string.Equals(firstToken, "yes", StringComparison.OrdinalIgnoreCase);
```

For an empty or whitespace response:

1. `rawResponseText` becomes `""`.
2. `normalizedResponseText` remains `""`.
3. `firstLine` remains `null`.
4. `firstToken` remains `null`.
5. Comparing `null` to `"yes"` produces `false`.
6. The result is returned as `Correct = false`, `RawScore = 0`, and
   `Explanation = "Judge said: "`.

The runner increments its judge-call counter, copies that false result to
`QuestionResult`, labels it `WRONG`, and includes it in both accuracy denominators.

### Provider errors have the same defect

`LongMemEvalJudge` catches every exception, including provider failures, and returns
`Correct = false`. It also persists `ex.Message`, which can contain arbitrary provider
text and is forbidden by the requested diagnostic security boundary.

The runner's outer catch similarly converts agent invocation errors into incorrect
answers. Judge failure and agent failure are therefore both indistinguishable from
ordinary product failure.

### What cannot be determined from the existing result

The existing report does not preserve:

- judge model or deployment identity;
- finish reason;
- whether the response was null, empty, filtered, or truncated;
- provider-error category;
- judge attempt count;
- judge token usage, although the judge computes it;
- raw response under a controlled diagnostic mode.

Consequently, the cause of the provider's blank output cannot be recovered from the
record. It could be a provider/adapter empty response, content filtering, truncation, or
another transport condition. The misclassification root cause is certain; the upstream
reason for the blank is not. The corrected result for this historical run is
**inconclusive**, not retrospectively `yes` or `no`.

## Assessment of question d24813b1

The gold rubric says that suggestions should build on the user's successful lemon
poppyseed cake experience and balance impressiveness with manageability. The supplied
agent answer:

- explicitly recommends lemon poppyseed cake;
- recalls that it was made for a colleague's going-away party;
- proposes related lemon variations; and
- offers a manageable cake-plus-cookies plan.

Under the official flexible preference criterion, that is a strong `yes`: the answer
correctly recalls and uses the user's personal information, and it need not reproduce
every rubric point.

This is a rubric assessment, not a replacement for a recorded judge call. The historical
blank response remains inconclusive. After Phase 1, the example should be re-judged through
the normal judge path and included in a multi-example preference calibration set. No
question ID, lemon keyword, or answer fragment may appear in production scoring logic.

The current preference prompt is semantically faithful to the official flexible rule.
Phase 1 will align its text with the official template and add a fidelity test so future
wording changes cannot accidentally make preference grading strict.

## Additional audit findings

These findings affect LongMemEval result trustworthiness and are in scope:

1. **Accuracy scale mismatch in the CLI.** The runner stores accuracy as `0..100`, as
   demonstrated by native reports (`40`, `50`, `100`). The CLI compares it with `0.5`
   and renders it using `:P1`, which expects `0..1`. A 40% result therefore passes the
   0.5 gate and renders as 4,000.0%.
2. **Inconclusive accounting is impossible.** `TypeResult` and `ExternalBenchmarkResult`
   contain only total and correct counts. They cannot expose a scored denominator or
   infrastructure-failure rate.
3. **Call accounting is not attempt accounting.** The agent counter increments only after
   a successful invocation. Judge retries do not exist, and judge token usage is discarded.
4. **Successful versus failed execution is conflated.** Agent errors, judge errors, and
   explicit `no` all become incorrect.
5. **Exception persistence is unsafe.** Arbitrary `Exception.Message` content is written
   to per-question results.
6. **Prompt fidelity is incomplete outside preference.** The local standard prompt omits
   the official equivalence/intermediate-steps allowance, and the abstention prompt omits
   the official explanation field. All five prompt families need conformance fixtures.
7. **Documentation names the wrong six labels.** The actual cleaned dataset labels are
   `single-session-user`, `single-session-assistant`, `single-session-preference`,
   `multi-session`, `temporal-reasoning`, and `knowledge-update`.
8. **Documentation claims behavior not implemented.** It says judge calls become
   judge-failures and that per-question results carry LLM-call counts; neither is true.
9. **Configuration documentation calls `ExternalBenchmarkOptions` a record and recommends
   `with`, but it is a class.**
10. **Judge provenance is absent.** Model/deployment identity and evidence mode should be
    present in run-level diagnostics without credentials or request headers.

The plan retains `OverallAccuracy`, `TaskAveragedAccuracy`, `TypeResult.Accuracy`, and
`RawScore` on their existing 0–100 scale for compatibility. It fixes consumers and docs
rather than silently changing those public units.

## Required invariants

1. Only a successfully parsed explicit `yes` or `no` is a scored quality judgment.
2. `Yes` maps to `Correct = true`; `No` maps to `Correct = false`; every other status maps
   to `Correct = null` and `RawScore = null`.
3. Empty, invalid, truncated, filtered, and provider-failed judge responses never enter the
   default quality denominator.
4. `OperationCanceledException` is propagated and is never converted to provider error.
5. Retry count is validated, bounded, and reflected exactly in call accounting.
6. Agent-execution failures are distinct from judge failures.
7. Gold answer text, `answer_session_ids`, and `has_answer` never enter the normal agent
   history, query, `AdditionalProperties`, retrieval query, memory, embedding, or answer
   prompt.
8. Evidence capture defaults to `None`; references are content-free; full content requires
   explicit opt-in.
9. Evidence from `AdditionalProperties` is accepted only through one reserved key and a
   strict allowlisted schema.
10. Oracle execution and normal execution have separate agents/clients, histories, results,
    counters, and aggregates.
11. Existing successful yes/no values remain `true`/`false` and `100`/`0` in JSON.
12. No credential, embedding vector, arbitrary exception text, provider header, or raw
    provider request is persisted.

## Target API design

Names may be adjusted during API review, but the following semantics are binding.

### Judge outcomes

```csharp
public enum JudgeOutcomeStatus
{
    Yes,
    No,
    Empty,
    Invalid,
    ProviderError
}

public enum JudgeFailurePolicy
{
    FailRun,
    RetryThenInconclusive,
    RetryThenIncorrect // explicit compatibility escape hatch; never the default
}

public enum JudgeEvidenceMode
{
    None,
    Outcome,
    Explanation,
    Raw
}
```

`RetryThenIncorrect` is the documented opt-in alternative for consumers that require the
legacy denominator. It must be visibly recorded in `ExternalBenchmarkResult.Options` and
run metadata. It must not rewrite the original status; instead, aggregation applies the
policy while the per-question result remains `Empty`, `Invalid`, or `ProviderError`.

### Options

Add to `ExternalBenchmarkOptions` (currently used only by LongMemEval):

```csharp
public JudgeFailurePolicy JudgeFailurePolicy { get; init; }
    = JudgeFailurePolicy.RetryThenInconclusive;

public int MaxJudgeRetries { get; init; } = 1; // validate 0..3

public JudgeEvidenceMode JudgeEvidenceMode { get; init; }
    = JudgeEvidenceMode.Outcome;

public EvidenceCaptureMode EvidenceCaptureMode { get; init; }
    = EvidenceCaptureMode.None;

public int EvidenceTopK { get; init; } = 10; // validate 1..100
```

Validation must run once before dataset loading or provider calls. Invalid enum values,
negative retries, excessive retries, or incompatible evidence settings fail early.

### Judgment result

Evolve `ExternalJudgmentResult` to include:

```csharp
public required JudgeOutcomeStatus Status { get; init; }
public required bool? Correct { get; init; }
public required double? RawScore { get; init; }
public int LlmCallCount { get; init; }
public int TokensUsed { get; init; }
public int AttemptCount { get; init; }
public string? SafeFailureCode { get; init; }
public JudgeEvidence? Evidence { get; init; }
```

`SafeFailureCode` is an AgentEval-owned bounded code such as `rate_limited`,
`content_filtered`, `timeout`, `invalid_finish_reason`, or `provider_error`; it is never
the exception message.

### Question result

Evolve `QuestionResult` with:

- nullable `Correct` and `RawScore`;
- required `JudgeStatus` when judging was attempted;
- `QuestionExecutionStatus` (`Completed` or `AgentError`);
- `AgentLlmCallCount`, `JudgeLlmCallCount`, and `JudgeTokensUsed`;
- optional `JudgeEvidence`;
- optional `QuestionEvidenceEnvelope`;
- safe failure code, never arbitrary exception text.

For a failed agent call, no judge is invoked and `JudgeStatus` is absent. For an
inconclusive judge, agent execution remains `Completed`.

`ExternalBenchmarkResult.OverallAccuracy`,
`ExternalBenchmarkResult.TaskAveragedAccuracy`, and `TypeResult.Accuracy` become
nullable so a zero-scored run cannot be represented as a genuine 0% product score.
Successful runs retain the same 0–100 numeric values.

### Parser

Extract an internal pure parser returning a typed outcome. Its grammar is:

- trim whitespace and a leading BOM;
- reject a response whose finish reason indicates truncation or content filtering;
- accept `yes` or `no` only when it is the first lexical token;
- allow punctuation or an explanation after that token;
- reject responses without a leading decision;
- reject conflicting or structurally ambiguous output;
- classify null/empty/whitespace as `Empty`.

This is deliberately stricter than the official Python expression
`"yes" in response.lower()`, which can classify text such as "not yes" incorrectly. It
preserves official binary methodology while making parsing trustworthy.

### Retry and call accounting

The judge owns judge-attempt accounting. The runner must not add an unconditional `+1`
after `JudgeAsync`. Each provider invocation increments its attempt counter immediately
before the call. The returned judgment supplies its actual `LlmCallCount`.

The runner similarly counts an attempted agent call even if the provider throws. Total
run calls equal:

```text
sum(question.AgentLlmCallCount + question.JudgeLlmCallCount)
```

No second counter may independently infer the same number. Cancellation ends the run and
does not fabricate a result.

## Evidence extension design

### Boundary

AgentEval remains a black-box evaluator. It does not inspect AgentMemory internals and does
not define retrieval algorithms. It accepts a normalized trace summary supplied by the
adapter after agent execution.

Use one reserved `AgentResponse.AdditionalProperties` key:

```text
agenteval.question_evidence.v1
```

The value must be either the AgentEval-owned DTO or JSON that strictly deserializes to it.
All other additional properties are ignored by LongMemEval. The runner never serializes
the entire `AdditionalProperties` dictionary.


The runner supplies the question identity and dataset provenance when it attaches the
validated envelope to `QuestionResult`; the adapter does not need, and must not receive,
gold metadata. This is the evaluator-side question context. A separate observer interface
is unnecessary unless a future adapter proves that returning evidence with the current
`AgentResponse` is insufficient.
### Generic envelope

```csharp
public sealed class QuestionEvidenceEnvelope
{
    public required string SchemaVersion { get; init; } // "1.0"
    public IReadOnlyList<EvidenceReference> Retrieved { get; init; } = [];
    public IReadOnlyList<EvidenceReference> AnswerContext { get; init; } = [];
}

public sealed class EvidenceReference
{
    public required string Id { get; init; }
    public required int Rank { get; init; }
    public double? SimilarityScore { get; init; }
    public string? SourceSessionId { get; init; }
    public int? SourceTurnIndex { get; init; }
    public DateTimeOffset? SourceTimestamp { get; init; }
    public int? AnswerContextOrder { get; init; }
    public string? Content { get; init; } // allowed only in Full mode
}
```

The exact answer-model ordering comes from `AnswerContext`, not from re-sorting
`Retrieved`. This avoids pretending that retrieval rank and prompt order are identical.

### Validation and bounds

- Maximum 100 retrieved references and 100 answer-context references.
- IDs and session IDs are trimmed, bounded, control-character-free strings.
- Rank and context order must be positive and unique within their lists.
- Similarity must be finite; no `NaN` or infinity.
- Content is rejected unless mode is `Full`; in `Full`, each item and total envelope size
  are bounded.
- Unknown schema versions and unknown fields fail evidence capture safely but do not
  change the answer's quality judgment.
- Keys or values representing credentials, headers, exceptions, embeddings, vectors, or
  provider requests are rejected.
- Evidence validation failures produce a safe evidence diagnostic, not an arbitrary
  serialized exception.

### Evaluator-side derived diagnostics

After the agent has returned, the evaluator may join the normalized references with the
entry's gold labels. It computes:

- whether any `answer_session_id` appears in final top K;
- whether a referenced `(session ID, turn index)` maps to `has_answer = true`;
- rank of first gold evidence;
- distinct source-session count and diversity ratio;
- source timestamps represented in the final answer context;
- exact final answer-context ordering.

Only the derived Boolean/count/rank fields enter default reports. Gold session IDs and
`has_answer` values are not copied into adapter input or normal answer context.

When no envelope is supplied, diagnostics are `NotObserved`, not zero or false. Absence of
instrumentation must never be presented as retrieval failure. A supplied session-level
reference without a turn index can support session recall, diversity, and timestamp
diagnostics, but turn-level `has_answer` remains `NotObserved`.

### Capture modes

- `None`: do not inspect the reserved property; serialize no evidence field. This is the
  default and is protected by a golden serialization test.
- `References`: validate and persist content-free IDs, ranks, scores, timestamps, and
  evaluator-derived counts.
- `Full`: additionally allow bounded evidence text after an explicit privacy warning.

## Oracle-reader design

### Purpose

The oracle result isolates answer-model/judge capability from normal retrieval quality. It
is a diagnostic ceiling, not a replacement score.

### Selected-ID projection

Sampling happens once against the normal dataset. The selected question IDs are frozen.
For each selected entry, an evaluator-only projector:

1. maps `haystack_session_ids` to sessions;
2. selects only sessions whose IDs occur in `answer_session_ids`;
3. clones those sessions;
4. removes `has_answer`, session labels, answer-session IDs, and other gold metadata;
5. preserves session timestamp and content ordering required by the answer model.

For abstention entries with no labelled evidence, the oracle history is empty and the
question remains an abstention test.

The projector can be cross-checked against `longmemeval_oracle.json` in tests, but it
should not require a second dataset version at runtime.

### Retrieval-bypassing reader

Add a dedicated oracle reader over an `IChatClient` (or a deliberately retrieval-disabled
`IEvaluableAgent`) rather than reusing the normal memory agent. It receives only the
sanitized oracle history and question. This prevents the normal memory system from adding
retrieved items or persisting oracle content.

The caller supplies the same answer-model deployment/configuration used by the normal
agent and records its model identity. AgentEval can validate declared identities but
cannot prove two opaque providers are the same model.

### Result shape

Return a paired result such as:

```csharp
public sealed class LongMemEvalPairedResult
{
    public required ExternalBenchmarkResult Normal { get; init; }
    public required ExternalBenchmarkResult Oracle { get; init; }
    public required IReadOnlyList<string> SelectedQuestionIds { get; init; }
    public double? OracleGapPercentagePoints { get; init; }
}
```

`Normal` and `Oracle` retain separate question results, denominators, failures, durations,
and call counts. `OracleGapPercentagePoints` is derived for diagnosis only. It must not be
fed into normal retrieval recall, normal accuracy, CLI pass/fail, or baseline comparison.

## Aggregation and reporting semantics

### Default denominator

For each type and for the whole run:

```text
scored = count(JudgeStatus is Yes or No)
correct = count(JudgeStatus is Yes)
accuracy = correct / scored * 100
```

Agent errors, `Empty`, `Invalid`, and `ProviderError` are excluded. If `scored == 0`,
accuracy is `null`, not zero.

Add:

- `SelectedQuestions`
- `AgentCompletedQuestions`
- `ScoredQuestions`
- `CorrectQuestions`
- `IncorrectQuestions`
- `InconclusiveQuestions`
- `AgentFailureQuestions`
- `JudgeFailureRate`

`TypeResult.TotalQuestions` may remain as the selected count for JSON compatibility, but
`Accuracy` must use `ScoredQuestions`. Document this clearly.

Task-averaged accuracy averages only types with at least one scored question and reports
how many types contributed. The result is inconclusive when no type contributed.

### Compatibility policy

When `RetryThenIncorrect` is explicitly selected, aggregates include exhausted
inconclusive outcomes as incorrect, but per-question `Correct` remains `null` and status
remains truthful. Reports mark the denominator policy prominently.

### CLI corrections

Because accuracy remains `0..100`:

- PASS threshold `0.5` becomes `50.0`;
- `:P1` becomes `{value:F1}%`;
- null accuracy produces an inconclusive/setup exit, never PASS;
- passed = explicit `Yes`;
- failed = explicit `No`;
- warnings/inconclusive = judge failures plus agent failures;
- console output includes `scored / selected` and failure counts.

The current fixed 50% smoke threshold remains a product decision, not an official
LongMemEval acceptance threshold.

## Backward compatibility and migration

### Preserved

- Existing `Yes` judgments serialize `Correct: true`, `RawScore: 100`.
- Existing `No` judgments serialize `Correct: false`, `RawScore: 0`.
- Public accuracy units remain 0–100.
- Existing runner factories and successful call flow remain available.
- `EvidenceCaptureMode.None` remains the default and emits no evidence envelope.
- History formatting continues stripping gold labels.

### Intentional beta break

`Correct` and `RawScore` become nullable in judgment and question results. This is a
necessary source-level break: a non-null Boolean cannot represent inconclusive without
lying. AgentEval is on `0.16.0-beta`, so this is the right release window to make the
semantic correction.

Consumers migrate from:

```csharp
if (question.Correct) { ... }
```

to:

```csharp
switch (question.JudgeStatus)
{
    case JudgeOutcomeStatus.Yes:
        break; // correct
    case JudgeOutcomeStatus.No:
        break; // incorrect
    default:
        break; // judge failure / inconclusive
}
```

or use `question.Correct is true` when inconclusive can be ignored.

### Serialization

Create golden `0.16.0-beta` fixtures for successful yes/no results before changing
contracts. New optional properties use null/default omission. The compatibility test is:

- successful Boolean and numeric fields retain their values and JSON types;
- evidence mode `None` adds no evidence property;
- failure results serialize `Correct`/`RawScore` as null or omit them consistently and
  include the typed status;
- old successful JSON deserializes into the new model with inferred status when necessary.

A small custom converter is acceptable if needed to infer `Yes`/`No` from legacy JSON
without weakening the runtime invariant.

## Phased implementation plan

### Phase 0 — red-first contract freeze

#### Task 0.1 — freeze compatibility baselines

- Capture minimal successful `yes` and `no` `ExternalJudgmentResult`,
  `QuestionResult`, `TypeResult`, and `ExternalBenchmarkResult` JSON.
- Capture current programmatic construction patterns.
- Add an explicit scale test proving `40` means 40%, not 0.4.
- Record affected CLI/sample/report consumers.

#### Task 0.2 — add failing tests

Add the complete required test matrix before production changes. Use deterministic fake
chat clients and spy agents. Provider exceptions, finish reasons, usage, and response text
must be controllable independently.

#### Task 0.R — review

Confirm the tests fail against current behavior for the expected reasons. Review the API
proposal, nullable migration, retry defaults, denominator policy, gold boundary, and
serialization posture before coding.

### Phase 1 — trustworthy judge semantics

#### Task 1.1 — contracts and validation

- Add the outcome, policy, and evidence enums.
- Add validated options.
- Change binary properties to nullable.
- Add safe status/counter fields.
- Keep retry maximum deliberately small (`0..3`).

#### Task 1.2 — parser and prompts

- Extract the pure parser.
- Detect empty, invalid, conflicting, truncated, and filtered responses.
- Align all official prompt variants, not only preference.
- Snapshot the flexible preference sentence.
- Add a general preference corpus; include `d24813b1` only as a regression fixture, never
  as production logic.

#### Task 1.3 — retry and diagnostics

- Retry only `Empty`, `Invalid`, and retryable provider errors.
- Do not retry explicit `No`.
- Propagate cancellation.
- Sanitize provider failures to owned codes.
- Capture raw text only in `Raw` mode and apply length bounds.
- Count attempts and tokens at the judge boundary.

#### Task 1.4 — runner and aggregation

- Separate the agent call try/catch from the judge call.
- Count attempted agent calls.
- Propagate typed judge results.
- Exclude inconclusive outcomes by default.
- Add selected/scored/failure counts.
- Make zero-denominator accuracy nullable.

#### Task 1.R — promotion review

Review parser edge cases, retry exhaustion, call/token totals, cancellation, no-score
aggregation, legacy deserialization, and all changed consumers. Fix findings before Phase 2.

### Phase 2 — safe diagnostic evidence

#### Task 2.1 — contracts

Add the generic envelope, reference DTOs, derived-diagnostic DTO, capture mode, schema
version, and bounds. Do not add an interface for a passive data object.

#### Task 2.2 — adapter bridge

Read only `agenteval.question_evidence.v1` from the full `AgentResponse`. Validate into a
fresh immutable/copy-owned DTO. Never retain arbitrary provider objects.

#### Task 2.3 — gold-side derivation

Join references with dataset labels after answer generation. Compute top-K gold presence,
has-answer presence, first rank, diversity, timestamps, and context ordering. Represent
unknown instrumentation as `NotObserved`.

#### Task 2.4 — reporting

Attach the envelope and derived diagnostics to `QuestionResult` only when configured.
Guarantee that `None` serializes no evidence field and performs no property parsing.

#### Task 2.R — review

Use hostile dictionaries, deep objects, excessive arrays, invalid scores, control
characters, sensitive key names, embeddings, exceptions, and headers. Confirm no gold
labels appear in spy-agent payloads or normal evidence.

### Phase 3 — oracle reader

#### Task 3.1 — projection

Implement a pure evaluator-side projector from selected normal entries to sanitized oracle
entries. Test one-session, multi-session, temporal, and abstention cases.

#### Task 3.2 — reader

Use a separate retrieval-bypassing answer client/agent. Reuse the same history formatter
and question-type judge. Record answer and judge model identities independently.

#### Task 3.3 — paired execution

Freeze IDs once, run normal and oracle paths separately, and return the paired shape. Make
normal execution complete before oracle projection is exposed to any answer path.

#### Task 3.R — review

Prove selected-ID equality, label stripping, separate reset/state, separate counters, and
no score mixing. Verify oracle evidence cannot enter normal `AdditionalProperties`.

### Phase 4 — consumers, reports, and migration

#### Task 4.1 — CLI

Fix scale, threshold, percent display, summary counts, null-score exit behavior, and
warnings. Add command-level tests with mixed yes/no/inconclusive results.

#### Task 4.2 — native and synthesized reports

Update the sample's `EvalResult` synthesis and any Mission Control projection so
inconclusive is not failed. Preserve native detail and expose judge/evidence status without
raw content by default.

#### Task 4.3 — documentation and sample

- Correct the six dataset labels.
- Correct the 0–100 examples and formatting.
- Document judge failure policy and evidence modes.
- Add a programmatic adapter evidence example.
- Add the oracle-reader example.
- Add the `0.16.0-beta` migration section.
- State explicitly what AgentMemory still owns.

#### Task 4.R — review

Diff golden JSON, inspect console snapshots, compile all samples, validate documentation
links/code, and review privacy warnings.

### Phase 5 — validation and release

#### Task 5.1 — offline verification

Run focused tests, the full `AgentEval.Memory.Tests` suite across all target frameworks,
then the full solution build and tests. Record exact totals at implementation time.

#### Task 5.2 — live verification

Only with explicit data/provider authorization:

- rerun the same deterministic 10-question sample;
- include `d24813b1`;
- run normal and oracle paths with declared identical answer-model configuration;
- record yes/no/inconclusive counts, retry attempts, model/deployment identity, and call
  totals;
- inject or safely induce one empty/invalid/provider-error case if the provider cannot
  naturally reproduce it;
- publish an before/after table separating product accuracy from judge reliability.

Do not claim that the fix improves product accuracy merely because failures are excluded.
Report both scored accuracy and judge completion rate.

#### Task 5.3 — final review

Review changed code for correctness, API clarity, concurrency/cancellation, data leakage,
serialization, and migration. Fix all findings, rerun verification, then update the tracker.

## Test matrix

The following tests are mandatory:

| # | Scenario | Required assertion |
|---:|---|---|
| 1 | `yes` | `Status=Yes`, `Correct=true`, scored |
| 2 | `no` | `Status=No`, `Correct=false`, scored |
| 3 | null/empty/whitespace | `Status=Empty`, `Correct=null`, not scored |
| 4 | malformed or ambiguous output | `Status=Invalid`, `Correct=null`, not scored |
| 5 | provider exception | `Status=ProviderError`, safe code, no exception text |
| 6 | bounded retry | attempts and `TotalLlmCalls` equal real calls; no double count |
| 7 | preference prompt | official flexible criterion is retained |
| 8 | gold leakage | no answer, answer-session ID, or has-answer label enters spy agent payload |
| 9 | evidence mode `None` | no evidence property and legacy successful values preserved |
| 10 | extension evidence | only reserved allowlisted fields survive safe serialization |
| 11 | successful compatibility | legacy yes/no JSON round-trips |
| 12 | truncation/filter finish reason | invalid/inconclusive even if text begins with yes/no |
| 13 | cancellation | cancellation propagates; no fabricated provider-error result |
| 14 | agent exception | agent failure distinct; judge not called |
| 15 | mixed aggregation | denominator contains only explicit yes/no |
| 16 | zero scored | accuracy null and run cannot pass |
| 17 | opt-in legacy denominator | status remains truthful while policy counts as incorrect |
| 18 | raw evidence modes | none/outcome/explanation/raw reveal only their promised level |
| 19 | hostile evidence | bounds and sensitive-value rejection are deterministic |
| 20 | missing evidence | diagnostic is `NotObserved`, not retrieval failure |
| 21 | gold derivation | ranks/top-K/has-answer/diversity/context ordering are correct |
| 22 | oracle selected IDs | exactly equal to normal selected IDs |
| 23 | oracle sanitization | no label fields enter oracle answer payload |
| 24 | oracle isolation | normal result/counters unchanged by oracle execution |
| 25 | CLI scale | 40 means `40.0%` and fails a 50 threshold |
| 26 | CLI inconclusive | warning/inconclusive, not failed or passed |

Use named tests following repository convention, for example:

```text
JudgeAsync_WhitespaceResponse_ReturnsEmptyInconclusive
JudgeAsync_ProviderException_ReturnsProviderErrorWithoutExceptionText
RunAsync_MixedJudgeOutcomes_ExcludesInconclusiveFromAccuracy
RunAsync_GoldLabels_DoNotEnterHistoryInjectableAgentPayload
RunAsync_EvidenceCaptureNone_OmitsEvidenceFromSerializedResult
RunPairedAsync_OracleExecution_DoesNotChangeNormalResult
```

## Security and privacy review

The implementation review must explicitly answer:

- Can any gold label reach the normal agent before or during its answer?
- Can an adapter smuggle arbitrary provider objects into `report-native.json`?
- Can raw evidence be enabled accidentally through a preset default?
- Are content sizes bounded before allocation and serialization?
- Are IDs normalized without changing their equality semantics?
- Are embeddings, credentials, headers, and exception objects rejected?
- Are provider errors represented by AgentEval-owned codes?
- Does oracle state survive into a subsequent normal question?
- Can an inconclusive-heavy run pass because of a denominator or null-handling bug?
- Does the manifest record failure and evidence policies needed to interpret the score?

`Full` evidence mode requires a prominent warning that retrieved content may contain user
PII and will be persisted in native reports. It should not be enabled by CLI presets
without an explicit flag.

## Definition of done

The improvement is complete only when:

- all mandatory red-first tests pass;
- blank, malformed, truncated, and provider-failed judge responses are never ordinary
  incorrect results under the default policy;
- retry and token/call accounting are exact;
- the flexible preference criterion is protected;
- default accuracy excludes inconclusive and agent-failed questions;
- the CLI uses the correct 0–100 scale and displays correct percentages;
- gold labels do not enter normal or oracle answer payloads as metadata;
- `EvidenceCaptureMode.None` preserves default no-evidence behavior;
- references and full evidence obey the allowlist and privacy policy;
- oracle and normal results remain separate;
- documentation and migration examples compile;
- focused, project, and full-solution tests pass across supported TFMs;
- an implementation-only review is completed and all findings are fixed;
- the tracker is updated with exact test evidence and review notes.

## AgentEval versus memory-system responsibilities

### AgentEval owns

- dataset selection and evaluator-side gold labels;
- safe history/oracle projection with labels stripped;
- type-specific judge prompts;
- judge parsing, retry, failure semantics, and accounting;
- binary-scoring denominators and failure-rate reporting;
- the generic evidence schema, allowlist, validation, and serialization;
- evaluator-side joins that turn supplied references into benchmark diagnostics;
- separate normal/oracle execution and reporting;
- migration documentation and benchmark-integrity tests.

### AgentMemory or another memory system owns

- how conversations are ingested and stored;
- extraction, chunking, summarization, embeddings, indexes, and graph construction;
- retrieval queries, candidate generation, similarity calculation, reranking, and top-K;
- mapping internal items back to stable session/turn identifiers;
- deciding the exact evidence order supplied to its answer model;
- emitting the normalized evidence envelope through its adapter;
- credentials, provider configuration, retention, and product-specific trace storage;
- ensuring its oracle reader uses the same answer model with retrieval disabled.

AgentEval must never implement AgentMemory-specific retrieval, reranking, extraction, graph,
or persistence logic. Its job is to make the evaluation result honest and to provide a
safe, generic place for the memory system to report what it did.
