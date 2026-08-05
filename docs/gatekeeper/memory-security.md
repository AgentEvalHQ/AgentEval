# Memory security with Gatekeeper

AgentEval can place deterministic policy gates around memory tools, local and hosted MCP operations,
`AIContextProvider` implementations, and provider-native candidate/recall hooks. The integration is a
defense-in-depth boundary: it reduces memory-poisoning, scope-confusion, unsafe recall, and resource-abuse
risk, but it does **not** prove that poisoning or data leakage is impossible.

```text
authenticated host identity
        ↓
one immutable memory-protection configuration
        ├─ local memory tool / MCP boundary
        ├─ GatedAIContextProvider boundary
        └─ provider-native candidate + recall hooks
        ↓
MemoryGatePipeline: recall → write → promotion → lifecycle → action influence
        ↓
allow / reject / quarantine / explicit approval
        ↓
content-free coverage report, receipts, and operational evidence
```

Every real path must pass through one of the declared adapters. A stronger adapter on one operation never upgrades
an opaque or bypassing operation. with one composite configuration

Configure memory protection once, before the agent is built:

```csharp
agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
{
    options.KnownTools = tools;
    options.ProtectMemory(new MemoryProtectionOptions(
        pipeline,
        operationRegistry,
        hostContextAdapter)
    {
        LocalMcpRegistry = localMcpRegistry,
        LocalMcpBindings = localMcpBindings,
        OwnedMcpServer = serverGate.CoverageEvidence,
        ContextProviders = gatedContextProviders,
        ProviderNativeGates = nativeProviderGates,
    });
});
```

`ProtectMemory` is deliberately call-once. It performs all profile, tool, MCP, provider, and deployment
checks before `UseGatekeeper` mutates the agent builder. Do not also register `MemoryToolCallGate`,
`MemoryInfluenceGate`, or `MemoryToolResultGate` manually.

The resulting `GatekeeperOptions.MemoryProtectionReport` is the authoritative, content-free deployment
record. Persist its schema, policy/adapter/configuration fingerprints, coverage entries, and notes with the
release evidence.

## Identity is host authority

Build `MemorySecurityScope` only from authenticated host state: established tenant, user, application,
agent, and session identifiers. Never accept these values from model-generated tool arguments, recalled
memory, MCP payloads, prompt text, or provider metadata. An adapter may compare an untrusted claimed scope
to host scope, but it must not promote the claim to authority.

Fail closed when required identity is absent or ambiguous. Keep tenant and user isolation checks active
across process restarts, imports, reconciliation, promotion, and rollback—not only at write time.

## Profiles and dispositions

- `Observe` records the decision that enforcing policy would make without adding the memory influence gate.
  Use it to calibrate policy and inventory coverage, not as a security boundary.
- `Enforce` composes call, influence, and result gates. Depending on policy, an unsafe or ambiguous operation
  can be rejected, quarantined, or held for explicit approval.
- Quarantine is not approval. Quarantined candidates must remain outside active recall and action paths until
  an authenticated reviewer promotes them.
- Rejection is preferable when ownership or coverage cannot be established. Approval is appropriate only
  when the reviewer receives bounded, content-safe evidence and the approval is bound to the exact operation.

Roll out from `Observe` to `Enforce` only after the report proves the intended minimum coverage and operators
have tested quarantine, approval expiry, incident attribution, and rollback.

## Coverage levels

| Level | Meaning | Suitable enforcement claim |
|---|---|---|
| `Unsupported` | No trustworthy interception point | None; refuse the deployment |
| `ObserveOnly` | Decision can be measured but not enforced | Calibration only |
| `ActionOnly` | Action boundary is gated but memory lifecycle is opaque | Narrow action control |
| `Boundary` | Reads/writes are intercepted at the integration boundary | Boundary enforcement |
| `FullLifecycle` | Candidate, storage/promotion, recall, and action influence are covered | Strongest available claim |

Coverage is per operation and surface. A tool, provider, or server with one strong operation does not upgrade
an unrelated operation. Local MCP reaches `FullLifecycle` only when matching client bindings and owned-server
coverage evidence agree on server identity, transport, schema, operation semantics, and policy fingerprint.
An opaque hosted MCP endpoint should normally remain `ActionOnly` or `Unsupported`; the composite integration
refuses it when the configured minimum is stronger.

## What each integration controls

- Memory tools: pre-call admission, scope and candidate checks, post-result recall checks, and taint-based
  influence controls for downstream sensitive tools.
- Local MCP: client boundary checks; an owned server gate adds pre-storage or pre-read enforcement and can
  prove full-lifecycle coverage.
- Hosted MCP: only capabilities stated by a bounded, versioned contract count. Marketing claims and tool
  names are not evidence.
- `GatedAIContextProvider`: generic before/after context boundary coverage. It cannot see provider-internal
  candidate ranking or storage unless the provider exposes native hooks.
- Provider-native hooks: candidate-write and recalled-item hooks can establish full-lifecycle coverage when
  both capabilities are present and policy fingerprints match.

## Deployment configuration and DI

`MemoryProtectionConfiguration.ParseJson` and `ReadFileOnce` accept only
`gatekeeper.memory-protection/1`. The JSON schema is embedded in `AgentEval.MAF` and published at
`Gatekeeper/Schema/memory-protection-config-v1.schema.json`. Unknown/duplicate properties, invalid enum
names, oversized documents, fingerprint mismatches, and a different minimum coverage fail closed.

Register an immutable options instance or factory with `AddAgentEvalMemoryProtection`. Do not bind arbitrary
types from JSON or permit runtime assembly/type activation. Read configuration once during startup, validate
the expected policy fingerprint, and treat changes as a new deployment.

## Safe operational telemetry

Record policy IDs/versions, fingerprints, operation IDs, surface, stage, disposition, reason codes, coverage,
and content digests. Do not record memory content, prompts, recalled documents, credentials, authorization
headers, cookies, embeddings, raw tool arguments/results, judge evidence, or exception payloads. Restrict
access to quarantine and approval evidence and apply an explicit retention policy.

Fingerprint changes are deployment changes. Alert when a known operation disappears, coverage drops, an
adapter fingerprint changes unexpectedly, or `Unsupported` appears. The report JSON schema is
`gatekeeper.memory-protection-report/1`.

## Incident and rollback runbook

1. Stop promotion and disable affected recall/action influence paths; preserve content-free receipts.
2. Identify the exact tenant, user, operation, policy/configuration fingerprint, provenance digest, and time
   window without exporting memory content to general logs.
3. Quarantine suspected records and descendants. Do not delete first: preserve a restricted forensic copy
   under the incident retention policy.
4. Rotate compromised credentials and revoke sessions if cross-scope access is plausible.
5. Roll back by immutable record/version IDs, re-run scope and conflict checks, and rebuild derived indexes.
6. Validate isolation after a restart and replay the relevant attack fixtures before restoring recall.
7. Deploy a versioned policy/configuration change, verify the coverage report, and monitor in `Observe` only
   if doing so does not re-expose the unsafe path.

For runnable scenarios, see [Memory-security samples](memory-security-samples.md). For existing systems, see
the [migration guide](memory-security-migration.md).
