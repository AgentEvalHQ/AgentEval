# Memory-security samples

Run all eight offline release fixtures from the repository root:

```powershell
dotnet run --project samples/AgentEval.Gatekeeper.Validation -- memory-security
```

The executable source is
[`MemorySecurityReleaseValidation.cs`](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Gatekeeper.Validation/MemorySecurityReleaseValidation.cs).
It uses only mock/local collaborators and prints content-free configuration fingerprint prefixes.

## Scenario map

| # | Scenario | What it proves |
|---|---|---|
| 1 | Local memory tool with strict scope | The scope resolver uses host tenant/user identity and the composite path reports full tool lifecycle coverage. |
| 2 | Local MCP plus owned server | Matching client binding and server evidence elevate the operation to `FullLifecycle`. |
| 3 | Generic `GatedAIContextProvider` | A custom provider wrapped at the context boundary reports `Boundary`, not provider-native coverage. |
| 4 | Provider-native hooks | Candidate-write plus recalled-item hooks bound to the same policy report `FullLifecycle`. |
| 5 | Cross-session browser/email fixture | Mocked untrusted sources persist only within the intended tenant/user scope after restart. |
| 6 | Observe-to-enforce rollout | Observe mode produces a coverage/configuration record without claiming enforcement. |
| 7 | Quarantine and rollback | A candidate remains reviewable outside active memory and can be rolled back by record ID. |
| 8 | Opaque hosted MCP | Startup intentionally fails when hosted capabilities cannot meet the `Boundary` floor. |

These fixtures validate composition, coverage, and safe failure behavior. They do not prove that a real
browser, email provider, SQL engine, remote MCP server, or memory vendor is secure. Replace each mock with an
adapter that derives identity from authenticated host state, then repeat the attack and restart tests against
the actual deployment.

## Minimal local-tool shape

```csharp
var policy = new MemorySecurityPolicy(
    "memory-production", "1", MemorySecurityProfile.Enforce,
    MemoryGateAction.Quarantine, MemoryCoverageLevel.Boundary);

var pipeline = new MemoryGatePipeline(
    [new MemoryScopeIntegrityGate(), new MemoryWriteAdmissionGate()],
    new MemoryGateCapabilities(scopeResolver: hostScopeResolver),
    policy);

builder.UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
{
    options.KnownTools = tools;
    options.ProtectMemory(new MemoryProtectionOptions(
        pipeline, operationRegistry, contextAdapter));
});
```

The host scope resolver and context adapter are application-specific security boundaries. The sample's
minimal adapters are deliberately non-production fixtures.
