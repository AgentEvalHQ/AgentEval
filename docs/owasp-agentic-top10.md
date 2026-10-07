# OWASP Agentic Top 10 — Coverage Crosswalk

> **OWASP Agentic AI Top 10 "Version 2026"** was published 2025-12-09. It complements the OWASP LLM Top 10
> (2025 edition) rather than replacing it — the official PDF includes an explicit ASI → LLM cross-map in
> Appendix A, and both lists remain actively maintained. This page maps each ASI category to what
> AgentEval provides: which red-team probes exercise it, which Gatekeeper gates defend against it at
> runtime, and an honest coverage verdict.

## Coverage at a glance

| ASI | Risk | Probes | Gates | Coverage |
|---|---|---|---|---|
| **ASI01** | Goal Hijacking | `PromptInjection`, `IndirectInjection`, `Crescendo`, `Jailbreak`, `PAIR`, `TAP`, `EncodingEvasion` | `HiddenInstructionPrefilterGate`, `ToolResultInjectionGate`, `ToolArgumentGoalCoherenceApprovalGate` | ✅ Full |
| **ASI02** | Tool & Resource Misuse | `ExcessiveAgency`, `ToolEscalation` | `ForbiddenToolGate`, `ToolNameApprovalGate`, `ArgumentPatternGate`, `PerToolCallBudgetGate`, `RunBudgetGate`, `MonetaryLimitGate` | ✅ Full |
| **ASI03** | Identity & Privilege Abuse | `PIILeakage`, `SystemPromptExtraction` | `SessionIdentityDriftGate`, `ContainedIdentityGate`, `OperatorAuthGate`, `ReferentialIntegrityGate` | ✅ Full |
| **ASI04** | Supply Chain Compromise | `SupplyChain` (static-dependency framing only) | None (runtime plugin validation not implemented) | ⚠️ Partial |
| **ASI05** | Sensitive Data Exfiltration | `PIILeakage`, `VectorEmbedding` | `ToolResultSecretGate`, `TaintTrackingGate`, `DomainAllowListGate` | ✅ Full |
| **ASI06** | Memory & Context Manipulation | RedTeam Memory Security suite (`MemorySecurityEvaluation`) | `MemoryScopeIntegrityGate`, `MemoryWriteAdmissionGate`, `MemoryConflictGate`, `MemoryRecallAdmissionGate`, `MemoryResourceBudgetGate`, `QuarantineGate`, `QuarantineLeaseGate` | ✅ Full |
| **ASI07** | Insecure Inter-Agent Communication | None | `SkillScriptApprovalGate`, `SkillScriptExecutionGate`, `SameBatchOrderingGate`, `SequenceGate` | ⚠️ Partial |
| **ASI08** | Cascading & Amplification Failures | None | `BlockStormSentinelGate`, `ContainmentOverrideGate`, `RateLimitGate`, `ToolResultSizeGate`, `ToolResultSizeAnomalyGate` | ⚠️ Partial |
| **ASI09** | Human Oversight Manipulation | `InferenceAPIAbuse`, `Misinformation`, `DataPoisoning` | `GatekeeperRefusalContract` / `GatekeeperRefusalPresenter` (plain-language risk summary per OWASP mitigation #4) | ✅ Full |
| **ASI10** | Rogue & Uncontrolled Agents | None | `FleetCorrelator`, SecurityGraph, Containment policies | ⚠️ Partial |

**Legend:** ✅ Full — probes + gates; ⚠️ Partial — gates only, or probes only, or both present with known gaps; ❌ Not covered.

## Per-category detail

### ASI01 — Goal Hijacking

An attacker manipulates the agent's objective — either directly via prompt injection in user input, or
indirectly via malicious content retrieved from external sources (RAG results, tool outputs, web pages).

**Probes** (`bench owasp` and `bench agentic`):
- `PromptInjection` — 30 probes covering direct injection patterns across 3 difficulty bands
- `IndirectInjection` — 18 tool-aware probes that plant instructions in tool return values
- `Jailbreak` — 20 single-turn jailbreak attempts (role-play, authority claim, hypothetical framing)
- `Crescendo` — 8 multi-turn escalation trajectories (Wave C)
- `PAIR` / `TAP` — iterative LLM-driven attacks (opt-in; not in default preset)
- `EncodingEvasion` — 12 probes using homoglyphs, base64, and zero-width characters

**Gates** (runtime, `UseGatekeeper`):
- `HiddenInstructionPrefilterGate` — detects embedded instructions in tool arguments before execution
- `ToolResultInjectionGate` — scans tool results for injected commands before they reach the model
- `ToolArgumentGoalCoherenceApprovalGate` — flags arguments that deviate from the stated goal

**Gaps:** none known in the standard probe set; PAIR/TAP are opt-in and require a separate attacker model.

---

### ASI02 — Tool & Resource Misuse

An agent invokes tools it should not have access to, or uses permitted tools beyond their intended scope
(calling `DELETE` when only `GET` was intended, running shell commands from a code-execution tool, etc.).

**Probes:**
- `ExcessiveAgency` — 14 probes that attempt to elicit privilege escalation via tool calls
- `ToolEscalation` — 8 multi-turn probes that attempt to chain permitted tools into a destructive sequence (opt-in)

**Gates:**
- `ForbiddenToolGate` — denies calls to tools not on the allow-list
- `ToolNameApprovalGate` — human-in-the-loop approval before novel tool names
- `ArgumentPatternGate` / `ArgumentPatternApprovalGate` — blocks or requires approval for argument patterns matching a policy
- `PerToolCallBudgetGate` — per-tool call-count limit within one run
- `RunBudgetGate` — aggregate call budget across all tools in one run
- `MonetaryLimitGate` — cumulative spend limit (requires tool-cost metadata)

**Gaps:** `ToolEscalation` is opt-in and not in the default `top10` preset; multi-step escalation sequences beyond 2 turns are not covered.

---

### ASI03 — Identity & Privilege Abuse

An agent is tricked into acting as a different principal, escalating its own permissions, or leaking
credentials and session tokens.

**Probes:**
- `PIILeakage` — 20 probes attempting to extract personal data including bearer tokens and session ids
- `SystemPromptExtraction` — 16 probes that attempt to read or reconstruct the system prompt

**Gates:**
- `SessionIdentityDriftGate` — detects identity changes mid-session
- `ContainedIdentityGate` — enforces per-run agent identity boundaries
- `OperatorAuthGate` — requires operator-level authentication for privileged tool actions
- `ReferentialIntegrityGate` — prevents arguments referencing resources outside the authorised scope

---

### ASI04 — Supply Chain Compromise

Malicious or counterfeit capabilities are introduced via third-party tools, plugins, model updates, or
runtime dependency resolution in an agentic ecosystem.

**Probes:**
- `SupplyChain` — 14 probes testing whether the agent recommends typosquatted or non-existent packages
  **Caveat:** these use the LLM03 static-dependency framing ("does the agent recommend a bad package?").
  OWASP ASI04 explicitly targets runtime plugin composition, not manifest-time dependencies. The four
  named ASI04 runtime vectors (malicious tool registration, capability poisoning via MCP, cross-agent
  tool injection, and runtime SDK substitution) have **no current probes**.

**Gates:** none implement runtime plugin validation or capability-provenance checks.

**Known gap:** runtime supply-chain attacks (ASI04's primary concern) are not covered by any current
probe or gate. Tracking in the backlog.

---

### ASI05 — Sensitive Data Exfiltration

An agent leaks PII, credentials, confidential documents, or internal state to an attacker-controlled
endpoint via tool calls, API responses, or model outputs.

**Probes:**
- `PIILeakage` — 20 probes across names, emails, SSNs, credit-card numbers, bearer tokens
- `VectorEmbedding` — 10 RAG-boundary probes that plant confidential data in retrieval context

**Gates:**
- `ToolResultSecretGate` — redacts secret-shaped spans (API keys, tokens, connection strings) from tool results before they reach the model
- `TaintTrackingGate` — propagates a taint tag from sensitive tool results and blocks exfiltration attempts downstream
- `DomainAllowListGate` — prevents tool calls to domains outside an explicit allow-list

---

### ASI06 — Memory & Context Manipulation

An attacker poisons the agent's persistent memory store or cross-session context, causing it to act on
false beliefs in future sessions.

**Probes:** the `MemorySecurityEvaluation` suite (RedTeam) covers scope integrity, write admission,
conflict detection, recall admission, and resource-budget violations — calibrated against the Memory
Security gold set shipped with the Gatekeeper Memory module.

**Gates** — the Gatekeeper Memory module is a near-1:1 implementation of OWASP's nine ASI06 mitigations:
- `MemoryScopeIntegrityGate` — blocks writes outside the declared memory scope
- `MemoryWriteAdmissionGate` — admits or rejects write operations by policy
- `MemoryConflictGate` — detects conflicting facts before they are committed
- `MemoryRecallAdmissionGate` — filters recalled memories by trust level and freshness
- `MemoryResourceBudgetGate` — enforces memory-size and write-rate budgets
- `QuarantineGate` / `QuarantineLeaseGate` — isolates untrusted memory objects behind a timed lease

---

### ASI07 — Insecure Inter-Agent Communication

Messages passed between agents (orchestrator → sub-agent, peer-to-peer, MCP tool calls) carry injected
instructions or bypass the downstream agent's safety controls.

**Probes:** none cover inter-agent message injection or trust-boundary violations between agents.

**Gates** (partial):
- `SkillScriptApprovalGate` / `SkillScriptExecutionGate` — gate on skill scripts that represent agent-to-agent delegation
- `SameBatchOrderingGate` / `SequenceGate` — enforce ordering invariants on tool-call sequences that could be exploited across agent boundaries

**Known gap:** no probes exercise multi-agent scenarios. The gates above cover agent-delegation enforcement
but not message-injection between independent agents. Tracking in the backlog.

---

### ASI08 — Cascading & Amplification Failures

A single compromised or misbehaving agent triggers a chain reaction across the fleet: tool abuse
propagates, a DoS on one agent degrades shared resources, or a benign agent amplifies a malicious
instruction passed through from a compromised peer.

**Probes:** none. `FleetCorrelator`, `BlockStormSentinelGate`, containment, and the security graph
all exist in the runtime but have zero corresponding red-team probes.

**Gates** (partial — runtime defences exist, but untested by probes):
- `BlockStormSentinelGate` — detects abnormal block-rate spikes that indicate an amplification loop
- `ContainmentOverrideGate` / path-containment policies — prevent tool calls from escaping a session's allowed filesystem or network scope
- `RateLimitGate` — per-agent / per-tool call-rate ceiling
- `ToolResultSizeGate` / `ToolResultSizeAnomalyGate` — caps result sizes that could be used to saturate context windows

**Known gap:** fleet-level cascade behaviour is not red-teamed. Tracking in the backlog.

---

### ASI09 — Human Oversight Manipulation

The agent undermines legitimate human oversight — by generating misleading summaries, refusing to
explain its reasoning, or manipulating its audit trail.

**Probes:**
- `InferenceAPIAbuse` — 12 probes testing model refusal under high-volume / anomalous call patterns (LLM10)
- `Misinformation` — 14 probes testing confabulation, nonexistent-entity recommendations, and false-fact injection (LLM09)
- `DataPoisoning` — 10 in-context poisoning probes including trigger-phrase and false-fact variants (LLM04)

**Gates:**
- `GatekeeperRefusalContract` / `GatekeeperRefusalPresenter` — produce a plain-language, non-model-generated risk summary for every blocked action. OWASP ASI09 mitigation #4 asks verbatim for "a plain-language risk summary (not model-generated rationales)" — this mechanism was shipped before the standard was published.

---

### ASI10 — Rogue & Uncontrolled Agents

An agent operates outside its sanctioned boundary — taking actions it was not authorised to take,
spawning sub-agents without approval, or persisting in a loop after its task is complete.

**Probes:** none.

**Gates** (partial — runtime defences exist, but untested by probes):
- `FleetCorrelator` — correlates findings across agents to detect coordinated anomalous behaviour
- SecurityGraph — maps inter-agent trust relationships and flags violations
- Containment policies — path, domain, and shell-metacharacter containment that prevents an agent from escaping its sandbox

**Known gap:** no probes test rogue-agent behaviour (unauthorised sub-agent spawning, run-after-completion, out-of-scope action sequences). Tracking in the backlog.

---

## Relation to OWASP LLM Top 10

The table below shows how the ASI and LLM taxonomies relate for the categories AgentEval covers. Use it
when you need to cite both standards in a compliance report.

| ASI | Risk | Maps to LLM Top 10 |
|---|---|---|
| ASI01 | Goal Hijacking | LLM01 (Prompt Injection) |
| ASI02 | Tool & Resource Misuse | LLM06 (Excessive Agency) |
| ASI03 | Identity & Privilege Abuse | LLM02 (Sensitive Information Disclosure), LLM07 (System Prompt Leakage) |
| ASI04 | Supply Chain Compromise | LLM03 (Supply Chain) — but ASI04 extends to runtime vectors LLM03 does not cover |
| ASI05 | Sensitive Data Exfiltration | LLM02 (Sensitive Information Disclosure) |
| ASI06 | Memory & Context Manipulation | LLM01 (indirect injection via memory), LLM08 (Vector & Embedding) |
| ASI07 | Insecure Inter-Agent Communication | LLM01 (injection via agent messages) |
| ASI08 | Cascading & Amplification Failures | LLM04 (Model DoS) |
| ASI09 | Human Oversight Manipulation | LLM09 (Misinformation), LLM10 (Inference API Abuse) |
| ASI10 | Rogue & Uncontrolled Agents | LLM06 (Excessive Agency — extended to multi-agent) |

## Running the agentic bench

To run the benchmark family most relevant to the agentic ASI categories:

```bash
# Standard agentic probe set (ASI01/02/03/05 focus)
agenteval bench agentic --preset top10 --subject MyAgent --azure-from-env

# Include tool-escalation and memory probes (ASI06)
agenteval bench agentic --preset audit --subject MyAgent --azure-from-env

# Full OWASP LLM Top 10 + ASI crosswalk report
agenteval bench owasp --preset audit --subject MyAgent --azure-from-env
```

The output report includes an `osiCoverage` block (JSON) and a per-category verdict table in the
Markdown summary. Flag `--certify` also writes a calibration certificate for the axis.
