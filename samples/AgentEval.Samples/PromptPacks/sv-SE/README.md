# Swedish judge prompt example

This authored `sv-SE` pack covers five RAG metrics, rubric-bound relevance, and a generic criteria scaffold. Load `prompts.json` with `JudgePromptPackLoader`; no custom metric class is needed. Each entry names its system, user, safety and retry Markdown files.

The generic `judge.criteria` entry starts with an empty criterion roster. Add the exact stable IDs/text required by your fixed dataset before using it to judge criteria. For arbitrary per-case rosters, use library per-evaluation definitions instead.

**Status: authored, not semantically calibrated.** The sample program uses scripted replies to verify transport and policy wiring. The draft cases below are proposed review material, not Andreas Adner's private dataset or evidence of real-model accuracy. Swedish reviewers should approve labels and expand coverage for the customer's domain before running repeatable calibration.

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
