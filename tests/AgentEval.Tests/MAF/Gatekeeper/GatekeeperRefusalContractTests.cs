// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Phase 4, P4-1/P4-2 — the versioned model-facing refusal envelope and its disposition. The envelope is
/// namespaced under <c>_gatekeeper</c> so a consumer can tell a gate refusal from a tool's own error, carries a
/// coarse disposition derived from the gate class, and leaks no policy name or reason.
/// </summary>
public class GatekeeperRefusalContractTests
{
    [Fact]
    public void Build_ProducesVersionedNamespacedEnvelope()
    {
        var body = GatekeeperRefusalContract.Build("gk_abc", RefusalDisposition.Quota, attempts: 3);

        using var doc = JsonDocument.Parse(body);
        var env = doc.RootElement.GetProperty("_gatekeeper");
        Assert.Equal("gatekeeper.refusal/1", env.GetProperty("schema").GetString());
        Assert.True(env.GetProperty("block").GetBoolean());
        Assert.Equal("gk_abc", env.GetProperty("referenceId").GetString());
        Assert.Equal("quota", env.GetProperty("disposition").GetString());
        Assert.Equal(3, env.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void Build_OmitsAttempts_WhenNull()
    {
        using var doc = JsonDocument.Parse(GatekeeperRefusalContract.Build("gk_x"));
        Assert.False(doc.RootElement.GetProperty("_gatekeeper").TryGetProperty("attempts", out _));
    }

    [Fact]
    public void TryParse_RoundTrips_AndDistinguishesFromAToolError()
    {
        var body = GatekeeperRefusalContract.Build("gk_1", RefusalDisposition.Escalate, attempts: 2);
        Assert.True(GatekeeperRefusalContract.TryParse(body, out var refId, out var disp, out var attempts));
        Assert.Equal("gk_1", refId);
        Assert.Equal(RefusalDisposition.Escalate, disp);
        Assert.Equal(2, attempts);

        // A tool's OWN error shape must NOT be mistaken for a gate refusal.
        Assert.False(GatekeeperRefusalContract.TryParse("""{"error":"file not found"}""", out _, out _, out _));
        Assert.False(GatekeeperRefusalContract.TryParse("not json", out _, out _, out _));
        Assert.False(GatekeeperRefusalContract.TryParse(null, out _, out _, out _));
    }

    [Theory]
    [InlineData("RunBudgetGate", RefusalDisposition.Quota)]
    [InlineData("MonetaryLimitGate", RefusalDisposition.Quota)]
    [InlineData("PerToolCallBudgetGate", RefusalDisposition.Quota)]
    [InlineData("CanaryToolGate", RefusalDisposition.Escalate)]
    [InlineData("QuarantineLeaseGate", RefusalDisposition.Escalate)]
    [InlineData("ForbiddenToolGate", RefusalDisposition.Denied)]
    public void Classify_MapsGateClassToDisposition(string policy, RefusalDisposition expected)
        => Assert.Equal(expected, RefusalDispositionClassifier.Classify(policy));

    [Fact]
    public void Classify_FailedClosedOnThrow_IsTransient()
        => Assert.Equal(RefusalDisposition.Transient, RefusalDispositionClassifier.Classify("AnyGate", failedClosedOnThrow: true));

    [Fact]
    public async Task Integration_BudgetBlock_SurfacesQuotaDisposition_ToTheModel()
    {
        var refund = AIFunctionFactory.Create((int amount) => "ok", "refund");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "refund", new Dictionary<string, object?> { ["amount"] = 1 })
            .AddToolCall("c2", "refund", new Dictionary<string, object?> { ["amount"] = 1 })
            .AddText("done");
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [refund] } });

        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, g => g.Add(new RunBudgetGate(maxToolCalls: 1)))
            .Build();

        await gated.RunAsync("refund twice");

        // The 2nd (budget-blocked) call's result is the refusal envelope with disposition="quota".
        var refusal = model.ReceivedMessages.SelectMany(l => l).SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result?.ToString())
            .First(t => t is not null && GatekeeperRefusalContract.TryParse(t, out _, out _, out _));

        Assert.True(GatekeeperRefusalContract.TryParse(refusal, out _, out var disposition, out _));
        Assert.Equal(RefusalDisposition.Quota, disposition);
    }
}
