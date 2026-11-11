# Localize and replace judge prompts

Built-in RAG judges can load their instructions from Markdown plus JSON or YAML metadata. A Swedish rubric does not need a custom metric class. This configures the **judge**, independently of the target agent's system prompt or the report language.

Thanks to [@adner](https://github.com/adner) (Andreas Adner) for identifying the private English RAG prompts and the rubric-binding limitations.

## Start with files

```sh
agenteval prompts export ./my-prompts --evaluator llm_faithfulness
agenteval prompts validate ./my-prompts/prompts.json
agenteval prompts inspect ./my-prompts/prompts.json --json
```

Export creates a new directory and refuses to overwrite an existing one. Its messages are English drafts. Translate the system, user, retry and safety Markdown files, translate criterion text, and set `locale` and an instrument `id`/`version` in the manifest. Keep machine-readable JSON field names, evaluator keys, criterion IDs and template slots unchanged. Validation checks structure and compatibility; it does not certify the translation's meaning or the judge's accuracy.

An authored Swedish example is in [the sample pack](../samples/AgentEval.Samples/PromptPacks/sv-SE/README.md). Run the offline walkthrough:

```sh
dotnet run --project samples/AgentEval.Samples -- --sample MultilingualPromptPacks
```

The sample uses scripted replies and makes no model calls. Its scores are examples, not calibration results.

## Use ordinary built-ins

```csharp
using AgentEval.Prompts;
using AgentEval.Metrics.RAG;

var pack = await new JudgePromptPackLoader().LoadAsync("my-prompts/prompts.json");
var prompts = new JudgePromptOptions { Pack = pack, Locale = "sv-SE" };
var faithfulness = new FaithfulnessMetric(chatClient, prompts);
```

Rubric-bound text evaluation uses the same options:

```csharp
var judge = new ChatClientEvaluator(chatClient).WithPrompts(prompts);
var relevance = new RelevanceEval(judge);
```

Register these configured instances using the host's existing DI registrations. `AddAgentEval()` registers `IJudgePromptResolver`; `AddAgentEvalDataLoaders()` registers `IJudgePromptPackLoader`. Loading creates an immutable snapshot. Editing a file does not alter running evaluations: reload it explicitly for the next run.

`EvaluationContext.JudgePrompts`, `EvaluationRequest.JudgePrompts`, `EvalInput.JudgePrompts` and `EvaluationOptions.JudgePrompts` select an instrument for one invocation. Per-invocation options replace constructor options as a complete selection. Inside that selection, a complete `Override` wins over an exact pack entry, then a matching built-in locale. An absent locale fails unless `FallbackLocale` was explicitly set. There is no process-culture detection, automatic translation, implicit parent-locale fallback or shared customer registry.

The legacy `ChatClientEvaluator(chatClient, systemPrompt)` path also preserves customer system text when a built-in binds its rubric. This is a **text-only override**: the bound rubric's numeric scale and reader policy still apply. Use a full prompt definition to change thresholds, scale or retry/user framing together. Unconfigured default English evaluators retain their legacy prompt path.

Exporting a `ChatClientEvaluator`-backed leaf retains the system instructions that judge carries, including family fallback or customer text. Merely selecting English without supplying a replacement also preserves those instructions. An explicit pack entry or complete override can replace them deliberately.

## CLI execution

```sh
agenteval eval --dataset cases.jsonl --endpoint http://localhost:1234/v1 --model my-model \
  --metrics llm_faithfulness --judge-prompt-pack ./my-prompts/prompts.json --judge-locale sv-SE
```

Use `--judge` and `--judge-model` to select a separate judge endpoint/model. `--system-prompt` remains the target's instruction. An explicit fallback is `--judge-fallback-locale sv` or `en`; provenance records both requested and effective locale.

If the dataset also has `evaluation_criteria` and `--judge` is supplied, include a matching `judge.criteria` definition. Export that key and populate its initially empty criteria with IDs `criterion-1`, `criterion-2`, etc., matching the case criteria in order. Supply the same fixed criterion roster as the cases being judged. The CLI rejects differing rosters even if they have the same number of criteria; split them into separate packs/runs or use library per-evaluation overrides. For localized criteria-fraction scoring in the library, declare `TestCase.JudgeCriteria` with stable IDs; translated prose is not a join key.

Named CLI metrics currently execute only with `--runs 1`. Combining their prompt packs with `--runs > 1` is rejected rather than ignored. The harness's criteria judge supports localized concurrent stochastic runs through the library and CLI criteria path. The CLI preflights selected instruments before invoking the target and includes their digests in the declared suite identity.

## Metadata and safety

Each definition declares `evaluatorKey`, `id`, semantic `version`, `locale`, `responseContract`, `scale`, `passAt`, optional `reviewAt`/`severityBands`, stable `criteria`, `dataSlots`, translation `status`, and four relative Markdown file paths: `systemFile`, `userFile`, `retryFile`, `safetyFile`. Thresholds are normalized to `[0,1]`; `scale` is `unit` or `percent`. `CriteriaV1` uses percent. Machine contracts are selected from known versions, not loaded executable code. The [v1 metadata schema](schemas/judge-prompt-pack.schema.json) supports editor validation; always run `prompts validate` too, which checks files, templates and evaluator compatibility.

Only `{{input}}`, `{{output}}`, `{{context}}`, `{{reference}}`, `{{criteria}}`, `{{dimension}}` and `{{safety}}` are recognized. The safety slot must precede data. Every case-data substitution is fenced by the implementation, including marker neutralization; substitution happens once. A translation cannot remove required data slots, change stable criterion IDs or select another response shape. System text cannot contain case-data slots. These controls reduce injection risk; an LLM can still disregard instructions.

Configured responses undergo bounded, implementation-owned JSON validation, including numeric scales, required arrays/booleans and duplicate/unknown criterion IDs. One corrective retry uses the selected localized retry text and original evidence. An invalid reply remains unmeasured. Prompt metadata determines rubric thresholds and score bands rather than merely describing different instructions while the reader uses an old policy.

Manifests are UTF-8 JSON/YAML, at most 1 MiB and 256 definitions. Individual message files are at most 512 KiB, with a 16 MiB aggregate read bound and a 128 Ki-character message bound. Loading rejects duplicate keys, YAML anchors/aliases/tags/merge keys, excess nesting, unknown fields, absolute/traversing paths and symlinks/reparse points. Files must reside under the manifest directory. Treat pack directories as trusted local configuration, not concurrently mutable untrusted uploads.

## Identity, privacy and calibration

Results record the actual key, ID, version, requested/effective locale, resolution origin, fallback and SHA-256 digest. The digest covers all message templates and reading metadata, including criteria and thresholds; changing text without changing a filename or version still changes identity. File paths and case data are excluded. Different configured instruments cannot be pooled into one stochastic statistic or model ranking.

`CaptureContent = true` additionally retains the resolved system/user/retry content in process; this can contain customer instructions and case data. Native AEF writing independently requires content capture on to persist it. Content-off AEF retains only identity metadata, even if the caller captured text in memory. CLI full content follows its existing explicit AEF content-capture setting. Serialized ordinary results with captured content should be handled accordingly.

An authored translation needs a separately reviewed calibration set. Include short supported answers, unsupported facts, contradictions, missing retrieval, empty retrieval, explicit abstention, Swedish negation, diacritics and mixed-language input. Record the prompt digest, model/provider/version, settings, labeled cases, repeat counts and disagreements. Changing locale, text, thresholds or a prefilter invalidates previous calibration claims. The shipped Swedish fixtures and draft labels are AI-authored; no customer dataset or human label review has been supplied. Repeated agreement with those labels verifies repeatability against the draft, not customer accuracy. Andreas's private 12-case result has not been reproduced here.

## Supported scope

| Surface | Current support |
|---|---|
| `llm_faithfulness`, `llm_context_recall`, `llm_answer_correctness` | Complete configurable RAG prompts, contracts and retry path |
| `llm_context_precision`, `llm_relevance` | Same configurable RAG path |
| `ChatClientEvaluator` criteria transport | Invocation-owned selection and stable criterion IDs |
| `RelevanceEval` / `AtomicLlmEval` | Rubric binding, localized criteria/framing and metadata-driven scoring |
| Tool/system-policy/evaluator-note atomic framing | Fixed, fenced slots selected by the leaf; required slots cannot be omitted by a replacement |
| Agentic/compliance atomic leaves | Library export and binding through the declared evaluation graph; broader CLI evaluation/calibration integration remains in progress |
| Red-team, runtime gates/prefilters, memory and upstream MEAI paths | Follow-up coverage; do not assume a global pack localizes them |

The supported RAG paths have no English keyword prefilter. Reference availability uses language-independent text checks. Unsupported library LLM metrics and upstream `MicrosoftEvaluatorAdapter` reject explicit `JudgePrompts` before evaluating, including both adapter contracts. The CLI rejects these combinations before invoking the target. Code-only `code_toxicity` can share English options for other metrics, but rejects non-English selection or a definition targeting its keyword rules, even in English. Targeted adapters for the remaining surfaces remain separate work.

## Custom evaluators and migration

For a custom `AtomicLlmEval`, call `CapturePromptDefinition()` after setting `JudgeSeesData` to obtain its stable key, criteria, evidence slots and reader policy. Export that definition with `JudgePromptPackLoader`, then bind the edited pack through `ChatClientEvaluator.WithPrompts()` or `EvalInput.JudgePrompts`. `BuiltInJudgePrompts.ForEval(benchmark)` collects the declared atomic instruments in composites and standard wrappers without executing them. It rejects cycles and conflicting definitions that share a key. Custom evaluators must expose their dependencies through `IEvalInstrumentSource`; an unknown evaluator cannot silently be assumed to be code-only.

The additional data slots are `toolCalls`, `toolDefinitions`, `systemMessage`, `expectedActions`, `evaluatorNotes` and `evaluatorPolicy`. Translate the surrounding Markdown labels. The implementation keeps tool facts, bounded serialization, truncation diagnostics, fencing and response validation; a pack cannot turn evidence into system instructions. Missing required evidence still skips the judge, while a recorded empty tool list is evaluated. Notes or policy supplied to a localized custom leaf must be explicitly selected by its `JudgeSeesData` flags.

Configured `JailbreakResistanceEval` uses one invocation of the selected judge, bypassing the legacy language-dependent pattern shortlist. This prevents an English pattern miss from becoming a Swedish pass and preserves the selected threshold and prompt identity. Its unconfigured per-pattern behavior remains unchanged. Recalibrate this new instrument; no localized pattern-accuracy claim is implied.

The CLI authoring catalogue currently exposes the first supported built-in starting points; custom and additional composite keys can use the library loader and binding API directly. A new machine response shape requires an `IEvaluator` implementation with its own parser. Implement `IJudgePromptBindable` and `IResolvedJudgePromptSource` if that evaluator also accepts these definitions; unsupported contracts must fail before a provider call. Executable parsers are never loaded from a pack.

Opting into a pack creates a new instrument digest, including when selecting the exported English draft. Re-establish baselines for that instrument instead of treating earlier English scores as calibrated translations. Existing context-free default calls retain their legacy path. Explicit customer system text now survives rubric binding; the CLI's embedded family fallback still yields to a leaf's own rubric. If customer text relied on being discarded, remove that override explicitly. A text-only system override does not change the bound reader's numeric scale or thresholds.
