# Context Corpus Generation Guide

**Purpose:** Generate large conversation corpora for memory benchmark context pressure testing.
**Location:** Place generated files in `src/AgentEval.Memory/Data/corpus/`
**Format:** JSON files matching the schema defined below.

---

## What Are These Files?

Context corpus files are **pre-built synthetic conversations** that get injected into an agent's chat history BEFORE the benchmark plants facts and asks questions. They simulate the effect of a long prior conversation — pushing the agent's context window closer to its limits.

The agent never "had" these conversations — they're injected silently via `IHistoryInjectableAgent.InjectConversationHistory()`. The benchmark then plants real facts on top of this noisy history and tests whether the agent can still recall them.

**Why this matters:** A modern LLM with a 128K token context window can trivially recall 5 facts from a short conversation. But if 50K tokens of prior conversation are already in the context, attention degrades, facts get buried, and recall drops significantly. Research (LongMemEval, ICLR 2025) shows 30-60% accuracy drops at 115K-token histories.

### File Inventory

| File | Turns | Tokens | Preset | Status |
|------|-------|--------|--------|--------|
| `context-small.json` | 15 | ~8K | Quick | ✅ Done |
| `context-medium.json` | 41 | ~20K | Standard | ✅ Done |
| `context-large.json` | 100 | ~50K | Full/Diagnostic | ❌ Needs generation |
| `context-stress.json` | 250+ | ~120K | Stress test | ❌ Needs generation |

---

## Output Format (CRITICAL — Must Match Exactly)

```json
{
  "schema_version": "1.0",
  "name": "context-large",
  "description": "Large context pressure — 100 turns of diverse conversation, ~50K tokens. Used by Full and Diagnostic presets.",
  "token_estimate": 50000,
  "turns": [
    {
      "user": "The user's question or statement goes here. Should be 20-80 tokens, natural and conversational.",
      "assistant": "The assistant's detailed response goes here. Should be 150-400 tokens, informative, natural, and substantive. Include specific details, facts, numbers, and explanations that make the response feel real and fill context meaningfully."
    },
    {
      "user": "Next question on a DIFFERENT topic...",
      "assistant": "Next detailed response..."
    }
  ]
}
```

### Field Requirements

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `schema_version` | string | Yes | Always `"1.0"` |
| `name` | string | Yes | `"context-large"` or `"context-stress"` |
| `description` | string | Yes | Human-readable description with turn count and token estimate |
| `token_estimate` | number | Yes | Approximate total tokens (all turns combined) |
| `turns` | array | Yes | Array of turn objects |
| `turns[].user` | string | Yes | User message (20-80 tokens) |
| `turns[].assistant` | string | Yes | Assistant response (150-400 tokens) |

### Example Turn (Copy This Style)

```json
{
  "user": "Why does the Moon appear larger when it's near the horizon compared to when it's high in the sky?",
  "assistant": "This is called the Moon Illusion, and it's been debated since at least Aristotle's time. The Moon's actual angular size doesn't change — it's about 0.5 degrees whether it's on the horizon or overhead. The leading explanation is the Ponzo illusion: when the Moon is near the horizon, your brain compares it to familiar objects like trees, buildings, and mountains. These reference objects make your brain interpret the Moon as being farther away, and since it still subtends the same visual angle, your brain 'corrects' by perceiving it as larger. Another contributing factor is that the horizon provides a sense of depth and distance that the empty sky lacks. Interestingly, if you bend over and look at the horizon Moon between your legs (removing normal spatial cues), the illusion largely disappears. Photographers know this effect well — Moon photos near the horizon always look disappointing because the camera captures the actual angular size, not your brain's inflated perception."
}
```

---

## PROMPT 1: Generate context-large.json (100 turns, ~50K tokens)

### Instructions for the LLM

Copy and paste this entire prompt into Claude, GPT-4, or any capable LLM:

---

**SYSTEM PROMPT:**

You are a JSON content generator creating a synthetic conversation corpus for an AI memory benchmark system called AgentEval. Your output must be a valid JSON file that exactly matches the specified schema.

**TASK:**

Generate a JSON file containing exactly **100 conversational turns** between a curious user and a knowledgeable assistant. Each turn consists of a user question/statement and a detailed assistant response.

**OUTPUT FORMAT:**

Your response must be a single, valid JSON object with this exact structure:

```json
{
  "schema_version": "1.0",
  "name": "context-large",
  "description": "Large context pressure — 100 turns of diverse conversation, ~50K tokens. Used by Full and Diagnostic benchmark presets.",
  "token_estimate": 50000,
  "turns": [
    {"user": "...", "assistant": "..."},
    {"user": "...", "assistant": "..."}
  ]
}
```

**CONTENT REQUIREMENTS:**

1. **Exactly 100 turns.** Each turn = one `{"user": "...", "assistant": "..."}` object.

2. **Diverse topics.** Cover ALL of these categories, roughly 5-7 turns each:
   - Physics & astronomy (black holes, quantum mechanics, relativity, stars, planets)
   - Biology & medicine (evolution, genetics, immune system, brain science, viruses)
   - History (ancient civilizations, wars, revolutions, cultural shifts, discoveries)
   - Technology & computing (AI, cryptography, networking, databases, programming paradigms)
   - Economics & finance (markets, inflation, trade, behavioral economics, cryptocurrency)
   - Philosophy & ethics (consciousness, free will, trolley problems, epistemology)
   - Earth science & climate (geology, weather, plate tectonics, ocean currents, climate change)
   - Mathematics & logic (probability, game theory, infinity, cryptography, statistics)
   - Psychology & neuroscience (memory, decision-making, cognitive biases, sleep, emotions)
   - Sociology & anthropology (cultures, languages, religions, urbanization, demographics)
   - Arts & literature (music theory, art movements, literary analysis, film, architecture)
   - Cooking & food science (chemistry of cooking, fermentation, nutrition, food history)
   - Engineering & materials (bridges, aerospace, materials science, energy, manufacturing)
   - Law & governance (legal systems, constitutions, international law, democracy, rights)
   - Sports & games (strategy, physics of sports, game theory, history of games)

3. **User messages:** 20-80 tokens each. Natural, curious, sometimes follow-up questions. Mix of:
   - Open questions ("Why does X happen?")
   - Specific questions ("What's the difference between X and Y?")
   - Opinion-seeking ("Do you think X is overhyped?")
   - Follow-ups ("That's interesting — what about Z?")

4. **Assistant responses:** 150-400 tokens each (this is CRITICAL for filling context). Responses should be:
   - Substantive and detailed — include specific facts, numbers, dates, names
   - Well-structured — use clear explanations with examples
   - Informative but conversational — not dry encyclopedia entries
   - Varied in length — some 150 tokens, some 350 tokens

5. **ABSOLUTELY FORBIDDEN content** (the benchmark plants these as facts to test recall — if they appear in the corpus, it contaminates results):
   - ❌ Any personal names (no "José", "Sarah", "Ana", "Max" as a person's name)
   - ❌ Any mention of peanut allergies, EpiPens, or food allergies
   - ❌ Any mention of golden retrievers or dogs named "Max"
   - ❌ Any mention of Barcelona, Copenhagen, or moving between cities
   - ❌ Any mention of WiFi passwords, parking lot spots, or dentist appointments
   - ❌ Any mention of Honda Civic, Tesla, or car purchases
   - ❌ Any mention of favorite colors being blue or green
   - ❌ Any mention of vitamin D supplements
   - ❌ Any mention of Contoso, Microsoft, or Google as employers
   - ❌ Any mention of sisters who are doctors in Madrid
   - ❌ Any mention of meetings with managers at 3pm
   - ❌ The word "Max" used as a name (OK as "maximum" or "max out")

6. **Quality checks:**
   - No two turns should discuss the exact same topic
   - No turn should reference a previous turn (each is independent)
   - All facts in responses should be accurate (or clearly presented as theoretical)
   - No markdown formatting — plain text only
   - Proper JSON escaping for quotes and special characters
   - Use straight quotes `"`, not curly quotes `""` `''`
   - No trailing commas in JSON arrays

**EXAMPLE TURNS (use this style but DO NOT copy these exact turns):**

```json
{"user": "Why does the Moon appear larger near the horizon?", "assistant": "This is called the Moon Illusion, and it's been debated since Aristotle. The Moon's actual angular size doesn't change — it's about 0.5 degrees whether on the horizon or overhead. The leading explanation is the Ponzo illusion: near the horizon, your brain compares it to trees, buildings, and mountains, making it seem farther away. Since it subtends the same angle, your brain 'corrects' by perceiving it as larger. If you bend over and look between your legs, the illusion largely disappears — removing normal spatial cues. Photographers know this well: horizon Moon photos always look disappointing because cameras capture actual angular size, not your brain's inflated perception."},

{"user": "What actually happens inside a neutron star?", "assistant": "A neutron star is the collapsed core of a massive star (8-25 solar masses) after a supernova. The density is almost incomprehensible — a sugar-cube-sized sample would weigh about 6 billion tonnes on Earth. At the surface, gravity is about 2 billion times Earth's. The crust is a crystalline lattice of iron nuclei embedded in a sea of electrons, incredibly rigid — 'mountains' on neutron stars are estimated at most 1-2 centimeters tall. Deeper, the nuclei dissolve into a superfluid of neutrons that flows with zero viscosity. At the very core (which we honestly don't fully understand), matter may form exotic states like quark-gluon plasma or strange matter. Neutron stars also have the strongest magnetic fields in the universe — magnetars can reach 10^15 Gauss, strong enough to distort atomic orbitals from light-years away."}
```

**OUTPUT ONLY THE JSON.** No explanations before or after. Start with `{` and end with `}`.

---

## PROMPT 2: Generate context-stress.json (250 turns, ~120K tokens)

### Instructions for the LLM

This file is much larger. You may need to generate it in batches (e.g., 50 turns at a time) and concatenate.

Copy and paste this prompt:

---

**SYSTEM PROMPT:**

You are a JSON content generator creating a very large synthetic conversation corpus for stress-testing AI memory benchmarks. Your output must fill approximately 120,000 tokens of context window.

**TASK:**

Generate a JSON file containing exactly **250 conversational turns** between a curious user and a knowledgeable assistant.

**OUTPUT FORMAT:**

```json
{
  "schema_version": "1.0",
  "name": "context-stress",
  "description": "Stress test context pressure — 250 turns of diverse conversation, ~120K tokens. Pushes context windows to their limits.",
  "token_estimate": 120000,
  "turns": [
    {"user": "...", "assistant": "..."},
    ...
  ]
}
```

**CONTENT REQUIREMENTS:**

All requirements from the context-large.json prompt apply, plus:

1. **Exactly 250 turns.** If generating in batches, concatenate into a single `turns` array.

2. **Longer assistant responses:** Average 350-500 tokens per response (to reach ~120K total). Include:
   - Multi-paragraph explanations
   - Historical context and modern applications
   - Specific numbers, dates, and named references (but NOT forbidden names)
   - Comparisons and analogies
   - "Interesting aside" tangents within responses

3. **Even more diverse topics.** In addition to the 15 categories from context-large, add:
   - Linguistics (language families, etymology, writing systems, translation)
   - Agriculture (farming techniques, soil science, crop rotation, GMOs)
   - Space exploration (missions, telescopes, colonization, space agencies)
   - Urban planning (city design, transportation, zoning, smart cities)
   - Cryptography & security (ciphers, RSA, zero-knowledge proofs, blockchain)

4. **Conversation patterns to include:**
   - Some turns where the user disagrees or pushes back ("But isn't that oversimplified?")
   - Some turns with follow-up chains (2-3 turns on the same broad topic, different angles)
   - Some turns where the user shares an anecdote before asking ("I saw a documentary about X, and they claimed Y. Is that accurate?")
   - Some turns with practical questions ("How would I actually implement X?")

5. **SAME FORBIDDEN CONTENT as context-large.json** (see list above). This is critical — any overlap with benchmark facts contaminates evaluation results.

6. **If generating in batches:** Each batch should be a valid JSON array fragment. Concatenation format:
   - Batch 1: turns 1-50
   - Batch 2: turns 51-100
   - Batch 3: turns 101-150
   - Batch 4: turns 151-200
   - Batch 5: turns 201-250

   When concatenating, ensure:
   - Single `turns` array (no nested arrays)
   - No duplicate turn content across batches
   - Topic distribution is even across batches (don't put all physics in batch 1)

**OUTPUT ONLY THE JSON.** No explanations. Start with `{` and end with `}`.

---

## VALIDATION PROMPT

After generating each file, use this prompt to validate it:

---

**SYSTEM PROMPT:**

You are a JSON quality validator for AI memory benchmark corpus files. You must check the provided JSON for correctness, completeness, and compliance with strict content rules.

**TASK:**

Validate the following JSON corpus file. Check ALL of the following and report each as PASS or FAIL with details:

**STRUCTURAL CHECKS:**
1. Is the JSON valid (parseable without errors)?
2. Does it have `schema_version`, `name`, `description`, `token_estimate`, and `turns` fields?
3. Is `turns` an array?
4. Does each turn have exactly `user` and `assistant` string fields?
5. Are there any trailing commas, mismatched brackets, or encoding issues?
6. Count the exact number of turns. Report: "Turn count: N" (expected: 100 for large, 250 for stress)

**CONTENT QUALITY CHECKS:**
7. Are assistant responses substantive? Check 5 random turns — each response should be 150+ tokens.
8. Are topics diverse? List the first topic word from each of 10 evenly-spaced turns (turns 1, N/10, 2N/10, ...).
9. Are there any duplicate or near-duplicate turns?
10. Do any turns reference other turns ("as I mentioned earlier")?

**FORBIDDEN CONTENT CHECKS (CRITICAL):**
11. Search the ENTIRE file for these exact strings (case-insensitive). Report EACH as FOUND or NOT FOUND:
    - "José" or "Jose"
    - "peanut allergy" or "peanut allergies" or "EpiPen"
    - "golden retriever"
    - "Barcelona" (as a city someone is from, NOT in a general context like FC Barcelona or Barcelona architecture)
    - "Copenhagen" (as a place someone lives, NOT in general European geography)
    - "WiFi password" or "BlueOcean"
    - "parking lot" or "spot 247" or "lot B"
    - "dentist appointment"
    - "Honda Civic" or "bought a Tesla"
    - "favorite color is blue" or "favorite color is green"
    - "vitamin D supplement"
    - "Contoso"
    - "sister.*doctor.*Madrid" (sister who is a doctor in Madrid)
    - "meeting.*3pm" or "meeting.*Sarah" (meeting with a manager)
    - "Max" used as a name (not as "maximum" or "max out" — look for "named Max", "my dog Max", "Max is")

12. For each FOUND item, report the turn number and exact context.

**TOKEN ESTIMATE CHECK:**
13. Estimate total tokens: count total characters, divide by 4. Report: "Estimated tokens: ~N"
14. Compare to `token_estimate` field. PASS if within 20% of stated estimate.

**REPORT FORMAT:**

```
=== CORPUS VALIDATION REPORT ===
File: [name]
Turns: [count] (expected: [N])

STRUCTURAL: [PASS/FAIL]
  - JSON valid: [PASS/FAIL]
  - Schema fields: [PASS/FAIL]
  - Turn format: [PASS/FAIL]

CONTENT QUALITY: [PASS/FAIL]
  - Response length: [PASS/FAIL] (sampled turns: [list lengths])
  - Topic diversity: [PASS/FAIL] (topics: [list])
  - No duplicates: [PASS/FAIL]
  - No cross-references: [PASS/FAIL]

FORBIDDEN CONTENT: [PASS/FAIL]
  - [list each check with PASS/FAIL]
  - VIOLATIONS: [list any found with turn number and context]

TOKEN ESTIMATE: [PASS/FAIL]
  - Estimated: ~[N] tokens
  - Stated: [N] tokens
  - Within 20%: [YES/NO]

OVERALL: [PASS/FAIL]
```

**INPUT:** [paste the JSON file content here]

---

## File Placement

After generating and validating, place the files here:

```
src/AgentEval.Memory/
└── Data/
    └── corpus/
        ├── context-small.json        ← ✅ Already exists (15 turns, ~8K tokens)
        ├── context-medium.json       ← ✅ Already exists (41 turns, ~20K tokens)
        ├── context-large.json        ← 🔴 Place here after generation (100 turns, ~50K tokens)
        └── context-stress.json       ← 🔴 Place here after generation (250 turns, ~120K tokens)
```

The `.csproj` already has `<EmbeddedResource Include="Data\corpus\*.json" />` — new files are automatically embedded.

After placing the files:
1. `dotnet build` — verify no errors
2. Run the validation test: `dotnet test --filter "CorpusLoaderTests"` — new files should load correctly
3. Run a benchmark with the Diagnostic preset to verify context pressure works

---

## How Context Pressure Scales

| Preset | Corpus File | Turns Injected | Token Pressure | Expected Vanilla Score |
|--------|-------------|---------------|----------------|----------------------|
| Quick | context-small.json | 15 | ~8K tokens | 50-65% |
| Standard | context-medium.json | 30 of 41 | ~15K tokens | 40-55% |
| Full | context-medium.json | 40 of 41 | ~20K tokens | 30-50% |
| Diagnostic | context-large.json | 100 | ~50K tokens | 20-40% |
| Stress | context-stress.json | 250 | ~120K tokens | 10-30% |

The more context pressure, the harder it is for the agent to recall planted facts — exactly mimicking real-world conditions where an agent accumulates hours of conversation history.
