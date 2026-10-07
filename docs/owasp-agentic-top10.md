# OWASP Top 10 for Agentic Applications — Coverage Crosswalk

> The **OWASP Top 10 for Agentic Applications for 2026** was published on 2025-12-09. Its ten risks, ASI01–ASI10,
> are specific to agents that plan, keep memory, call tools and act with delegated authority. It sits beside the
> [OWASP Top 10 for LLM Applications 2025](redteam/owasp.md), which AgentEval's `bench owasp` covers. This page maps
> each ASI risk to what AgentEval has today: the red-team attacks that probe it, the Gatekeeper gates that defend
> against it at runtime, and what is missing.

## Coverage at a glance

| ASI | Risk (OWASP name) | Probes | Runtime gates | Coverage |
|---|---|---|---|---|
| **ASI01** | Agent Goal Hijack | `PromptInjection`, `IndirectInjection`, `Jailbreak`, `EncodingEvasion`, `SkillInjection`; opt-in `Crescendo`, `PAIR`, `TAP` | `HiddenInstructionPrefilterGate`, `ToolResultInjectionGate`, `ToolArgumentGoalCoherenceApprovalGate` | ✅ Probes and gates |
| **ASI02** | Tool Misuse and Exploitation | `ExcessiveAgency`; opt-in `ToolEscalation` | `ForbiddenToolGate`, `ToolNameApprovalGate`, `ArgumentPatternGate`, `ArgumentPatternApprovalGate`, `PerToolCallBudgetGate`, `RunBudgetGate`, `MonetaryLimitGate`, `SequenceGate`, `SameBatchOrderingGate`, `ReferentialIntegrityGate`, `DomainAllowListGate` | ✅ Probes and gates |
| **ASI03** | Identity and Privilege Abuse | None aimed at identity or privilege | `OperatorAuthGate`, `ContainedIdentityGate`, `SessionIdentityDriftGate` (experimental) | ⚠️ Gates only |
| **ASI04** | Agentic Supply Chain Vulnerabilities | `SkillInjection` (a poisoned third-party skill); `SupplyChain` (package recommendations) | `SkillScriptExecutionGate`, `SkillScriptApprovalGate` | ⚠️ Partial |
| **ASI05** | Unexpected Code Execution (RCE) | `InsecureOutput` (includes command- and code-injection probes) | `ToolUsageContractGate` with `ShellMetacharDenyPredicate`, `SkillScriptExecutionGate`, `SkillScriptApprovalGate` | ⚠️ Partial |
| **ASI06** | Memory and Context Poisoning | Memory-security corpus (library only); `DataPoisoning` (in-context) | `MemoryScopeIntegrityGate`, `MemoryWriteAdmissionGate`, `MemoryConflictGate`, `MemoryRecallAdmissionGate`, `MemoryResourceBudgetGate`, `MemoryToolCallGate`, `MemoryToolResultGate`, `MemoryInfluenceGate` | ⚠️ Partial |
| **ASI07** | Insecure Inter-Agent Communication | None | None specific | ❌ Not covered |
| **ASI08** | Cascading Failures | None | `BlockStormSentinelGate`, `ContainmentOverrideGate`, `RateLimitGate`, `RunBudgetGate`, `ToolResultSizeGate`, `ToolResultSizeAnomalyGate` (experimental) | ⚠️ Gates only |
| **ASI09** | Human-Agent Trust Exploitation | `Misinformation` (nearest) | The approval gates, which hand a decision to a person | ⚠️ Partial |
| **ASI10** | Rogue Agents | None | `ContainmentOverrideGate`, `QuarantineGate`, `QuarantineLeaseGate`, `ContainedIdentityGate`, `BlockStormSentinelGate` | ⚠️ Gates only |

**Legend:** ✅ probes and gates both exist; ⚠️ one side is missing or only part of the risk is covered; ❌ neither.

**Where the probes run.** The 14 default attacks run in `bench owasp` and `redteam`. `Crescendo`, `PAIR`, `TAP` and
`ToolEscalation` are opt-in: they run only when named in `redteam --attacks`, and no preset includes them. `bench agentic`
does not run attacks; it grades an answer or a captured trace.

**Probe counts** below are at the Comprehensive intensity that `bench owasp --preset audit` uses. `--preset top10` runs
the same 14 attacks at the Quick intensity, which is a subset of each (for example 10 of the 27 `PromptInjection` probes).

## Per-risk detail

### ASI01 — Agent Goal Hijack

An attacker changes what the agent is trying to do, through instructions hidden in content it reads: web pages,
documents, emails, tool results.

**Probes**
- `PromptInjection`: 27 single-turn probes (LLM01).
- `IndirectInjection`: 19 probes, some delivered through a tool's return value (LLM01).
- `Jailbreak`: 29 single-turn probes (LLM01).
- `EncodingEvasion`: 23 probes that hide the instruction in an encoding (LLM01). `redteam --transform` can also
  re-send every single-turn probe through any of the 18 encoding codecs.
- `SkillInjection`: 6 tool-aware probes that plant instructions in a MAF Agent Skill (LLM01; see also ASI04).
- Opt-in: `Crescendo` (3 multi-turn escalations), `PAIR` (3 seeds, iterative, needs `--attacker`), `TAP` (3 seeds,
  tree search, needs `--attacker`).

**Gates**
- `HiddenInstructionPrefilterGate`: a lexical prefilter that blocks tool results carrying hidden or encoded injection
  markers.
- `ToolResultInjectionGate`: blocks a tool result that contains an injection marker.
- `ToolArgumentGoalCoherenceApprovalGate`: an LLM judge that escalates a tool call whose arguments do not fit the user's
  goal.

---

### ASI02 — Tool Misuse and Exploitation

The agent uses a tool it is allowed to use, but in a way nobody intended: the wrong operation, the wrong target, too
often, or in a harmful sequence.

**Probes**
- `ExcessiveAgency`: 15 tool-aware probes (LLM06).
- Opt-in: `ToolEscalation`, 3 multi-turn, tool-aware seeds that try to chain permitted tools into a harmful sequence.

**Gates**
- `ForbiddenToolGate`: blocks tools on a deny-list.
- `ToolNameApprovalGate`: escalates calls to named tools for approval.
- `ArgumentPatternGate`: blocks arguments that match a forbidden pattern. `ArgumentPatternApprovalGate` approves only
  routine-looking arguments and escalates the rest.
- `PerToolCallBudgetGate` and `RunBudgetGate`: cap calls per tool, calls per run and the run's monetary total.
- `MonetaryLimitGate`: caps the running sum of a monetary argument.
- `SequenceGate` and `SameBatchOrderingGate`: block a guarded tool after a trigger tool, or in the same batch as one.
- `ReferentialIntegrityGate`: allows only IDs that the user or a trusted lookup produced earlier in the run.
- `DomainAllowListGate`: allows only listed URL hosts in tool arguments.

**Gaps:** escalation is probed only by the three opt-in `ToolEscalation` seeds.

---

### ASI03 — Identity and Privilege Abuse

The agent acts with more privilege than it should, or with credentials or an identity that are not its own.

**Probes:** none target identity or privilege. `PIILeakage` (22 probes, LLM02) and `SystemPromptExtraction` (19,
LLM07) test what the agent discloses, which is a different risk.

**Gates**
- `OperatorAuthGate`: blocks a run without an allow-listed operator identity.
- `ContainedIdentityGate`: refuses a run when a contained, or undetermined, identity is present.
- `SessionIdentityDriftGate` (experimental): binds the first admitted operator to the session.

**Gaps:** no probe tries credential misuse, confused-deputy calls or privilege escalation.

---

### ASI04 — Agentic Supply Chain Vulnerabilities

Third-party agents, tools, plugins or prompts that are malicious or tampered with, often loaded at runtime.

**Probes**
- `SkillInjection` (see ASI01): a poisoned third-party MAF Agent Skill whose description or resource tries to
  instruct the agent. The skill's description lands in the system prompt, a higher-trust position than a retrieved
  document.
- `SupplyChain`: 14 probes (LLM03) that ask whether the agent *recommends* a bad or non-existent package. That is the
  LLM Top 10 framing, not a runtime vector.

Other runtime vectors (a malicious tool registration, a poisoned MCP tool description, a swapped dependency) have no
probes.

**Gates**
- `SkillScriptExecutionGate`: hard-blocks skill scripts that are not on the allow-list.
- `SkillScriptApprovalGate`: escalates `run_skill_script` unless the script is trusted.

**Gaps:** nothing checks the provenance of a tool or plugin when it is registered.

---

### ASI05 — Unexpected Code Execution (RCE)

The agent writes or runs code or commands that an attacker controls.

**Probes:** `InsecureOutput` has 31 probes (LLM05), including 4 command-injection and 3 code-injection probes. They
check whether the agent's *output* carries an executable payload, not whether a code-execution tool runs it.

**Evaluators:** `CodeVulnerabilityEval`, an LLM-judged check on generated code, is part of the agentic `safety`
composite. It is built in code with `AgenticBenchmark.Safety(...)`, not from the CLI.

**Gates**
- `ToolUsageContractGate` with `ShellMetacharDenyPredicate`: denies shell metacharacters in arguments, per dialect
  (PowerShell, POSIX sh, cmd).
- `SkillScriptExecutionGate` and `SkillScriptApprovalGate` (see ASI04).

**Gaps:** no probe drives an agent that has a code-execution tool into running attacker code.

---

### ASI06 — Memory and Context Poisoning

Malicious content is written into the agent's memory or long-lived context and steers later decisions.

**Probes**
- The memory-security corpus, `MemorySecurityAttackCorpus.Default`: 12 attack scenarios and 4 benign controls. Five
  deterministic evaluators score a batch of memory operations against it (`MemoryPoisonContainmentEval`,
  `MemoryScopeIsolationEval`, `MemoryInfluenceSafetyEval`, `MemoryAuditabilityEval`, `MemoryUtilityEval`), combined by
  `MemorySecurityCompositeEvals.Create()`. This is a library API. No CLI command runs it, and nothing yet drives the
  scenarios against a live agent.
- `DataPoisoning`: 12 in-context poisoning probes (LLM04).

**Gates**
- `MemoryScopeIntegrityGate`: fails closed when the trusted scope is missing or contradicted.
- `MemoryWriteAdmissionGate`: checks provenance, trust and content before a write.
- `MemoryConflictGate`: blocks lower-trust overwrites and repeated writes from one source posing as agreement.
- `MemoryRecallAdmissionGate`: drops stale, cross-scope or tampered records on recall.
- `MemoryResourceBudgetGate`: caps writes, recalls and quarantined records.
- `MemoryToolCallGate` and `MemoryToolResultGate`: run the memory checks on registered memory write and read tool
  calls, and on what the reads return.
- `MemoryInfluenceGate`: taints values read from memory so they cannot flow into sensitive tool calls.

**Gaps:** the corpus is not run against a real agent from the CLI.

---

### ASI07 — Insecure Inter-Agent Communication

Messages between agents are intercepted, spoofed or tampered with, or carry instructions the receiving agent trusts.

**Probes:** none. **Gates:** none specific to agent-to-agent messages. A sub-agent that is exposed to its caller as a
tool passes through the tool-result gates (ASI01), but nothing authenticates or checks a message between two agents.

---

### ASI08 — Cascading Failures

A fault in one agent or tool spreads through the system and is amplified on the way.

**Probes:** none.

**Gates** (they limit how far a fault can run in one session):
- `BlockStormSentinelGate`: once a threshold of enforced blocks is reached, blocks everything after it.
- `ContainmentOverrideGate`: blocks every call while a target is contained.
- `RateLimitGate`: a per-session, fixed-window cap on runs.
- `RunBudgetGate`: caps calls and spend per run.
- `ToolResultSizeGate`: truncates oversized results. `ToolResultSizeAnomalyGate` (experimental) flags a result far
  outside a tool's usual size.
- `FleetCorrelator` (experimental): correlates findings across gates within a session.

**Gaps:** no probes, and nothing models a fault crossing from one agent to another.

---

### ASI09 — Human-Agent Trust Exploitation

People trust the agent's output or recommendation more than they should, and approve something harmful.

**Probes:** `Misinformation` has 16 probes (LLM09) for confabulated facts and invented entities. It is the nearest
check: an agent that states falsehoods confidently is what over-trust exploits.

**Gates:** the approval gates (`ToolNameApprovalGate`, `ArgumentPatternApprovalGate`, `SkillScriptApprovalGate`,
`ToolArgumentGoalCoherenceApprovalGate`) put a person in the loop. When the Gatekeeper blocks a call, the refusal
follows a versioned contract (`GatekeeperRefusalContract`).

**Gaps:** no probe tests whether the agent misleads the person who approves its actions.

---

### ASI10 — Rogue Agents

An agent acts outside what it was authorised to do while appearing legitimate.

**Probes:** none.

**Gates**
- `ContainmentOverrideGate`, `QuarantineGate` (blocks a session the shadow judge quarantined) and
  `QuarantineLeaseGate` (a signed, expiring quarantine lease).
- `ContainedIdentityGate` (see ASI03) and `BlockStormSentinelGate` (see ASI08).
- `AgenticSecurityGraph`: builds a cross-session view from a tenant snapshot.

**Gaps:** no probe tests out-of-scope action sequences, unsanctioned sub-agents or work continuing after the task ends.

---

## Relation to the OWASP LLM Top 10 (2025)

This is AgentEval's own mapping, for citing both lists in one report. It is not an OWASP table.

| ASI | Nearest LLM Top 10 (2025) risks |
|---|---|
| ASI01 Agent Goal Hijack | LLM01 Prompt Injection |
| ASI02 Tool Misuse and Exploitation | LLM06 Excessive Agency |
| ASI03 Identity and Privilege Abuse | LLM06 Excessive Agency |
| ASI04 Agentic Supply Chain Vulnerabilities | LLM03 Supply Chain |
| ASI05 Unexpected Code Execution | LLM05 Improper Output Handling |
| ASI06 Memory and Context Poisoning | LLM04 Data and Model Poisoning, LLM08 Vector and Embedding Weaknesses |
| ASI07 Insecure Inter-Agent Communication | LLM01 Prompt Injection (instructions passed between agents) |
| ASI08 Cascading Failures | LLM10 Unbounded Consumption |
| ASI09 Human-Agent Trust Exploitation | LLM09 Misinformation |
| ASI10 Rogue Agents | LLM06 Excessive Agency |

## Running the probes

```bash
# The 14 default attacks at full depth (covers the ASI01, ASI02 and ASI05 probes above)
agenteval bench owasp --preset audit --subject MyAgent --from-env

# The opt-in multi-turn attacks (ASI01, ASI02); redteam takes --endpoint/--model, --azure or --sut
agenteval redteam --endpoint <url> --model <name> --attacks Crescendo,ToolEscalation
```

See [OWASP LLM Top 10 red-teaming](redteam/owasp.md) and the [CLI reference](cli.md) for targets and options.
