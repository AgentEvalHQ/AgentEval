# LongMemEval live validation — 2026-07-29

This record validates the `0.16.0-beta` LongMemEval trustworthiness changes
against the Azure OpenAI deployment configured in the workspace. The user
explicitly authorized sending the reviewed LongMemEval corpus to that deployment.
No credential, endpoint, header, raw question, gold answer, agent response,
judge response, or evidence content is recorded here.

## Fixed configuration

| Field | Value |
|---|---|
| Answer deployment | `gpt-5.5` |
| Judge deployment | `gpt-5.5` |
| Normal/oracle declared answer model | Identical: `gpt-5.5` |
| Selection | 10 questions, stratified, seed 42 |
| Required regression question | `d24813b1` included |
| Judge failure policy | `RetryThenInconclusive` |
| Maximum retries | 1 |
| Evidence capture | `None` |
| Judge evidence | `Outcome` |
| Normal history mode | `TextBlob` |
| Oracle | Isolated answer-session projection, same frozen IDs |

Selected IDs, in frozen execution order:

```text
19b5f2b3
gpt4_15e38248
2e6d26dc
d24813b1
gpt4_7abb270c
8077ef71
gpt4_483dd43c
0e4e4c46
4d6b87c8
4388e9dd
```

The validation harness locally returned an empty response for the first normal
judge attempt and its one retry. This deliberately produced one final `Empty`
outcome without sending malformed content to Azure. It makes 90% the expected
normal-path judge-completion ceiling. The injection was exhausted before oracle
execution.

## Before/after reliability table

Product accuracy is shown only over explicit `Yes`/`No` outcomes. Judge
completion is `(Yes + No) / agent-completed questions`. These are deliberately
separate signals.

| Stage and path | Product accuracy | Scored / selected | Judge completion | Yes / No / Empty / Invalid / ProviderError | Retry attempts | Agent / judge / total calls |
|---|---:|---:|---:|---:|---:|---:|
| Before: explicit `Temperature=0`, 30-token budget — normal | n/a | 0 / 10 | 0% | 0 / 0 / 1 / 0 / 9 | 10 | 10 / 20 / 30 |
| Before: explicit `Temperature=0`, 30-token budget — oracle | n/a | 0 / 10 | 0% | 0 / 0 / 0 / 0 / 10 | 10 | 10 / 20 / 30 |
| Temperature fixed, 30-token budget — normal | 100.0% | 8 / 10 | 80% | 8 / 0 / 1 / 1 / 0 | 4 | 10 / 14 / 24 |
| Temperature fixed, 30-token budget — oracle | 100.0% | 8 / 10 | 80% | 8 / 0 / 0 / 2 / 0 | 2 | 10 / 12 / 22 |
| Final: provider-default temperature, 256-token budget — normal | 88.9% | 9 / 10 | 90% | 8 / 1 / 1 / 0 / 0 | 1 | 10 / 11 / 21 |
| Final: provider-default temperature, 256-token budget — oracle | 100.0% | 10 / 10 | 100% | 10 / 0 / 0 / 0 / 0 | 0 | 10 / 10 / 20 |

Final task-averaged accuracy was 93.3% normal and 100.0% oracle. The oracle gap
was 11.1 percentage points.

## Findings and fixes

1. The configured `gpt-5.5` deployment accepted default chat options and
   `MaxOutputTokens`, but rejected requests carrying an explicit
   `Temperature=0`. The judge now uses provider-default temperature unless
   `ExternalBenchmarkOptions.JudgeTemperature` is explicitly configured.
2. A 30-token output budget produced incomplete/invalid results on a reasoning
   deployment because reasoning tokens share the output budget. The default is
   now 256 and is configurable through `JudgeMaxOutputTokens`.
3. The typed failure model worked as designed during both defects: provider
   failures, empty output, and invalid output stayed inconclusive; they never
   became ordinary incorrect answers or a passing zero-score run.
4. Call totals matched real attempts. The final normal path made ten answer
   calls and eleven judge calls; the single extra judge call is the induced
   retry. Oracle made exactly ten plus ten.

## Interpretation boundary

The move from `n/a` to 88.9% normal accuracy does **not** prove that the memory
product improved. It proves the judge request became compatible and sufficiently
budgeted, allowing nine product answers to be scored. Likewise, excluding the
deliberate empty judgment repaired denominator honesty but did not make that
answer correct.

The final comparison supports a narrower conclusion:

- judge reliability reached the expected 90% normal ceiling and 100% oracle;
- scored normal accuracy was 88.9%;
- oracle accuracy was 100%;
- the 11.1-point oracle gap is a retrieval/context diagnostic, not part of the
  normal score.

## Final security and API review

| Review question | Result |
|---|---|
| Can gold labels reach the normal answer path? | No. Normal history is formatted from label-stripped turns; spy-agent tests cover IDs, answer-session IDs, and `has_answer`. |
| Can gold labels reach the oracle answer path? | No. The oracle projector deep-clones only answer sessions, then strips session IDs, answer-session IDs, and turn labels before the isolated reader exists. |
| Can arbitrary provider objects enter a native report as evidence? | No. Only `QuestionEvidenceEnvelope.AdditionalPropertiesKey` is read. Typed DTO, bounded JSON string, and bounded `JsonElement` are accepted; other objects are rejected to an owned code. |
| Is raw evidence enabled accidentally? | No. `EvidenceCaptureMode.None` is the default and returns before accessing `AdditionalProperties`. `Full` requires explicit programmatic configuration; CLI presets do not enable it. |
| Are sizes bounded before copying/serialization? | Yes. Reference/list/content bounds are checked before copies. JSON strings are length-checked before deserialization, and `JsonElement` now writes through a fixed-capacity buffer so an oversized element cannot force an unbounded duplicate allocation. |
| Are IDs normalized without changing equality semantics? | Yes. Evidence IDs receive outer whitespace trimming and control-character rejection only; comparisons remain ordinal with no case folding or hashing. Dataset question IDs are not rewritten. |
| Are embeddings, credentials, headers, and exception objects rejected? | Yes. Arbitrary objects are rejected; strict JSON disallows extra properties; credential-like IDs/content and unsafe controls are rejected; exception text is never persisted. |
| Are provider errors AgentEval-owned? | Yes. Results retain bounded codes such as `provider_error`, `timeout`, `content_filtered`, and `invalid_finish_reason`, never provider exception messages. |
| Are score-interpreting policies recorded? | Yes. Native results retain `Options`; safe report dimensions and canonical summary metrics record failure policy, retry bound, temperature configuration, output budget, judge evidence mode, evidence mode, and top-K. The shared manifest schema has no extensible metadata field, so it was not broadened for one benchmark. |
| Are normal/oracle state and counters isolated? | Yes. Normal completes before oracle projection/reader creation. Results, calls, evidence, and model declarations are separate; selected IDs are frozen once. |
| Are cancellation and concurrency semantics safe? | Yes. Cancellation propagates rather than becoming provider failure. Per-question state is cleared in `finally`; paired execution is deliberately sequential to enforce the oracle boundary. |
| Is migration clear? | Yes. The guide covers nullable 0–100 scores, denominators, failure/evidence modes, provider-compatible judge options, paired execution, privacy, and canonical inconclusive behavior. |
