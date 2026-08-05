# Migrate an existing memory integration to Gatekeeper

Migrate one operation surface at a time and keep the original application-owned memory boundary in place
until the `MemoryProtectionReport` proves the required coverage. A migration is incomplete while an
unregistered write, recall, promotion, reconciliation, or action-influence path remains reachable.

## Common migration sequence

1. Inventory every memory operation and surface, including background reconciliation/import and direct
   provider SDK calls.
2. Assign a stable `MemoryOperationContract` with kind, surface, content fields, trusted scope fields,
   category, side-effect behavior, and sensitive-result behavior.
3. Resolve identity from authenticated host state and implement the narrow context adapter for that surface.
4. Start with a versioned `MemorySecurityPolicy` in `Observe`, compare decisions with expected outcomes, and
   resolve all `Unsupported` coverage.
5. Test candidate injection, cross-tenant/user access, stale/conflicting recall, quarantine, approval replay,
   result-to-action influence, and restart persistence.
6. Switch the matching Gatekeeper enforcement and memory policy profile to `Enforce`; require the intended
   minimum coverage in startup configuration.
7. Remove the old path only after startup fails closed on configuration drift and rollback is rehearsed.

## From a custom `IToolGate`

Keep non-memory policy in the existing gate. Move memory semantics into `MemoryToolOperationRegistry`, an
`IMemoryToolContextAdapter`, and `MemoryGatePipeline`; then configure them through the single
`ProtectMemory` path. Do not register the generated memory call/result/influence gates manually, because
that can split policy provenance and duplicate enforcement.

Map any previous allow/deny result to explicit `MemoryGateAction` dispositions. Preserve a stable reason
code, but replace content-bearing logs with receipts and digests.

## From Mem0 or AgentMemory

Treat SDK/API calls as tool or MCP operations unless the provider exposes trustworthy candidate-write and
recalled-item hooks. Register search/recall, add/update, delete, and promotion/reconciliation separately.
Provider metadata is untrusted input; tenant/user scope must come from the host.

If only the external API boundary is visible, claim at most `Boundary`. Use `MemoryProviderNativeGate` and
claim `FullLifecycle` only when both candidate and recalled-item hooks execute for every relevant path and
their policy fingerprint matches the composite pipeline. Provider-side security features complement these
gates but do not replace application scope, influence, and coverage checks.

## From a custom `AIContextProvider`

Wrap it with `GatedAIContextProvider` to gate the before/after context boundary. This yields `Boundary`
coverage in enforce mode (`ObserveOnly` in observe mode). Ensure direct calls to the inner provider cannot
bypass the wrapper.

For stronger coverage, add native candidate-write and recalled-item hooks inside the provider and expose
them through `MemoryProviderNativeGate`. Keep the generic wrapper until all call sites use the native path;
the report will show both integrations and their exact coverage.

## From local MCP

Create client operation contracts and bindings from the actual discovered tool/schema, not copied names.
For an owned server, add `MemoryMcpServerGate<TRequest,TResult>` and pass its immutable `CoverageEvidence` to
the composite options. Full-lifecycle credit requires exact client/server identity, version, transport,
schema fingerprint, operation semantics, and policy provenance.

If the server is not owned, model it as hosted MCP. Do not fabricate server evidence. Use an explicit hosted
contract for the callback/approval capabilities you can actually verify, and lower the coverage floor or
refuse the integration when it cannot meet policy.

## Cutover checklist

- All operations and bypass paths are inventoried and registered.
- Host identity cannot be overridden by arguments, memory, or provider metadata.
- Observe and enforce profiles align with Gatekeeper enforcement.
- The startup report has no coverage below the configured minimum.
- Configuration and report JSON validate against schema version 1.
- Quarantine, approval expiry/replay, rollback, and restart isolation are tested.
- Telemetry contains no memory content, secrets, embeddings, or raw payloads.
- A fingerprint or coverage change is reviewed as a deployment change.
