# Swedish judge prompt example

This authored `sv-SE` pack covers five RAG metrics, seven additional safety/quality/task metrics, counterfactual bias, rubric-bound relevance, and a generic criteria scaffold. Load `prompts.json` with `JudgePromptPackLoader`; no custom metric class is needed. Each entry names its system, user, safety and retry Markdown files. The additional metric schemas preserve their machine enum values and safety caps; only instructions and narratives are translated.

The generic `judge.criteria` entry starts with an empty criterion roster. Add the exact stable IDs/text required by your fixed dataset before using it to judge criteria. For arbitrary per-case rosters, use library per-evaluation definitions instead.

A sixteenth entry, `gate.over-refusal`, is an AI-authored **draft** for the runtime over-refusal judge. Set `JudgeGateOptions.JudgePrompts` to the loaded pack; configured gating bypasses the English refusal prefilter. The sample calls `JudgeTextAsync` and demonstrates a quality flag, not an inline deployment. Use this axis under `WarnOnly`; a flag must not block a justified safety refusal. Its output-only view cannot independently verify the original request or the legitimacy of a cited reason. The draft explicitly retains uncertainty.

The accompanying [gate calibration JSON](calibration-gate-over-refusal.json) contains two AI-authored, unreviewed format examples. From the repository root, `agenteval prompts validate samples/AgentEval.Samples/PromptPacks/sv-SE/prompts.json` validates the mixed metric/runtime pack. With a configured provider, the following makes real model calls:

```bash
agenteval gatekeeper calibrate --gate judge:over-refusal \
  --judge-prompt-pack samples/AgentEval.Samples/PromptPacks/sv-SE/prompts.json --judge-locale sv-SE \
  --gold-set samples/AgentEval.Samples/PromptPacks/sv-SE/calibration-gate-over-refusal.json
```

This diagnostic exits 1: the two-case, unreviewed dataset cannot establish certificate eligibility. Keep the label/review metadata honest even when model judgments match. For offline inspection, supply a `GateDecisionV1` JSON reply via `gatekeeper inspect --model-reply <file>` with the same pack/locale and a `{"text":"..."}` payload on stdin. It records zero provider calls and unknown model provenance.

**Status: AI-authored, not human-reviewed or semantically calibrated.** The sample program uses scripted replies to verify transport and policy wiring. The draft cases and expected labels below were authored by an AI assistant. No customer dataset or human-reviewed labels have been supplied. These cases are proposed review material, not Andreas Adner's private dataset or evidence of real-model accuracy. Swedish reviewers should approve labels and expand coverage for the customer's domain before running repeatable calibration.

| Draft case | Question / context / answer | Expected review |
|---|---|---|
| Short supported answer | “När börjar mötet?” / “Mötet börjar klockan nio på måndag.” / “Klockan nio.” | Faithfulness should pass; omitted unrelated detail is not a defect. |
| Unsupported time | Same question/context / “Klockan tio.” | Faithfulness should fail. |
| Negation | “Är rabatten tillgänglig?” / “Rabatten gäller inte företagskunder.” / “Ja, för företagskunder.” | Contradiction should fail. |
| Correct abstention | “Vad kostar tjänsten?” / “Priset är ännu inte fastställt.” / “Priset framgår inte.” | Supported abstention should pass. |
| Empty retrieval | “När öppnar butiken?” / captured empty context / “Den öppnar åtta.” | Judge the unsupported claim; do not confuse empty with unavailable retrieval. |
| Missing retrieval | Required context is unavailable | No judge grade; record missing evidence. |
| Relevant but wrong | “Vad är Sveriges huvudstad?” / answer “Göteborg.” | Relevance can be high while correctness fails; do not conflate metrics. |
| Diacritics and mixed language | Swedish query and answer, English evidence naming Örebro | Meaning should determine support, not language matching or lost diacritics. |

See [the guide](../../../../docs/judge-prompt-packs.md) for CLI, override precedence, privacy, supported scope and calibration requirements. Thanks to [@adner](https://github.com/adner) (Andreas Adner) for the feedback motivating this work.

The accompanying [relevance calibration JSONL](calibration-relevance.jsonl) is a runnable four-case **AI-authored draft**, with no customer data or human review. With a configured judge provider, the following makes real model calls and records agreement with these draft labels:

```sh
agenteval bench agentic calibrate \
  --dataset samples/AgentEval.Samples/PromptPacks/sv-SE/calibration-relevance.jsonl \
  --judge-prompt-pack samples/AgentEval.Samples/PromptPacks/sv-SE/prompts.json \
  --judge-locale sv-SE --out ./relevance-sv-review.md --records ./relevance-sv-cases.jsonl
```

Use `--limit 1` for a single-call wiring check; it does not apply the agreement gate. Even the full four-case run is too small and unreviewed to establish accuracy. Keep disagreements, expand the cases, obtain independent label review, and reserve held-out cases before making a customer-domain claim. Case records include the dataset digest and actual instrument identity, and explicitly mark label review as unverified.
