// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper.MemorySecurity;
using AgentEval.RedTeam.MemorySecurity;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class MemorySecurityMockComponentsTests
{
    private static readonly MockMemoryScope UserA = new("tenant-a", "user-a");
    private static readonly MockMemoryScope UserB = new("tenant-a", "user-b");

    [Fact]
    public void SqlStore_DefaultPartitions_IsolateUsers()
    {
        var store = new MockMemorySqlStore();
        store.Write(UserA, "private blue preference", "source-a");

        Assert.Empty(store.Recall(UserB, "blue"));
        Assert.Single(store.Recall(UserA, "blue"));
    }

    [Fact]
    public void SqlStore_DeliberateSharedPartitionBug_LeaksAcrossUsers()
    {
        var store = new MockMemorySqlStore(deliberateSharedPartitionBug: true);
        store.Write(UserA, "private blue preference", "source-a");

        var leaked = Assert.Single(store.Recall(UserB, "blue"));

        Assert.Equal(UserA, leaked.Scope);
    }

    [Fact]
    public void SqlStore_Restart_PreservesCrossSessionMemory()
    {
        var store = new MockMemorySqlStore();
        var written = store.Write(UserA, "durable marker", "source-a");

        var recalled = Assert.Single(store.Restart().Recall(UserA, "marker"));

        Assert.Equal(written.RecordId, recalled.RecordId);
    }

    [Fact]
    public void SqlStore_Tamper_LeavesDigestInvalid()
    {
        var store = new MockMemorySqlStore();
        var written = store.Write(UserA, "trusted value", "source-a");

        Assert.True(store.Tamper(UserA, written.RecordId, "modified value"));
        var tampered = Assert.Single(store.Recall(UserA, "modified"));
        Assert.False(store.HasValidIntegrity(tampered));
    }

    [Theory]
    [InlineData(MemoryAttackDeliverySurface.BrowserDocument)]
    [InlineData(MemoryAttackDeliverySurface.Email)]
    [InlineData(MemoryAttackDeliverySurface.CloudTool)]
    public async Task ExternalSource_ReturnsHermeticContent(MemoryAttackDeliverySurface surface)
    {
        var source = new MockMemoryInjectionSource(surface, "source", "candidate memory");

        var content = await source.FetchAsync();

        Assert.Equal("candidate memory", content);
        Assert.Equal(1, source.FetchCount);
    }

    [Fact]
    public async Task ExternalSource_Cancellation_Propagates()
    {
        var source = new MockMemoryInjectionSource(
            MemoryAttackDeliverySurface.BrowserDocument,
            "source",
            "candidate memory");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await source.FetchAsync(cancellation.Token));
    }

    [Fact]
    public void McpEndpoint_ServerAdmissionDenial_DoesNotWrite()
    {
        var store = new MockMemorySqlStore();
        var endpoint = new MockMemoryMcpEndpoint("memory", "1", false, store, _ => false);
        var scenario = MemorySecurityAttackCorpus.Default.Scenarios.Single(item => item.Id == "MS-DIRECT-001");

        var result = endpoint.Plant(UserA, scenario);

        Assert.Null(result);
        Assert.Equal(0, store.WriteCount);
        Assert.Equal(1, endpoint.ServerAdmissionCount);
    }

    [Fact]
    public void McpEndpoint_ServerRecallDenial_DoesNotReadStore()
    {
        var store = new MockMemorySqlStore();
        store.Write(UserA, "private blue preference", "source-a");
        var endpoint = new MockMemoryMcpEndpoint(
            "memory",
            "1",
            false,
            store,
            serverRecallAdmission: _ => false);

        var result = endpoint.Recall(UserA, "blue");

        Assert.Empty(result);
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(1, endpoint.ServerRecallAdmissionCount);
    }

    [Fact]
    public void HostedMcpSimulator_ExposesHostedIdentity()
    {
        var endpoint = new MockMemoryMcpEndpoint("hosted-memory", "2.1", true, new MockMemorySqlStore());

        Assert.True(endpoint.Hosted);
        Assert.Equal("hosted-memory", endpoint.ServerName);
        Assert.Equal("2.1", endpoint.ServerVersion);
    }

    [Fact]
    public void Quarantine_Rollback_RemovesRecord()
    {
        var store = new MockMemorySqlStore();
        var quarantine = new MockMemoryQuarantineStore();
        var record = store.Write(UserA, "candidate", "source");
        quarantine.Quarantine(record);

        Assert.Single(quarantine.Records);
        Assert.True(quarantine.Rollback(record.RecordId));
        Assert.Empty(quarantine.Records);
    }

    [Fact]
    public void AuditStore_PersistsOnlyContentFreeIdentifiersAndDigest()
    {
        var audit = new MockMemoryAuditStore();
        audit.Record(new MockMemoryAuditEvent("MS-DIRECT-001", "source", "quarantine", new string('a', 64)));

        var recorded = Assert.Single(audit.Events);
        Assert.Equal("quarantine", recorded.Decision);
        Assert.Equal(64, recorded.ContentDigest.Length);
    }

    [Fact]
    public void AuditEvent_RawOrInvalidIdentifiers_AreRejected()
    {
        Assert.Throws<ArgumentException>(
            () => new MockMemoryAuditEvent("scenario with raw content", "source", "allow", new string('a', 64)));
        Assert.Throws<ArgumentException>(
            () => new MockMemoryAuditEvent("scenario", "source", "allow", "not-a-digest"));
    }

    [Fact]
    public async Task ContextProvider_Recall_UsesRealMafLifecycle()
    {
        var store = new MockMemorySqlStore();
        store.Write(UserA, "blue preference", "source");
        var provider = new MockMemoryAIContextProvider(store, UserA, "blue");

#pragma warning disable MAAI001 // Test exercises the current MAF AIContextProvider lifecycle.
        var result = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(Agent(), session: null, new AIContext()));
#pragma warning restore MAAI001

        var message = Assert.Single(result.Messages!);
        Assert.Equal("blue preference", message.Text);
    }

    [Fact]
    public async Task ContextProvider_Store_UsesRealMafLifecycle()
    {
        var store = new MockMemorySqlStore();
        var provider = new MockMemoryAIContextProvider(store, UserA, "response", providerNativeCandidateHook: true);

#pragma warning disable MAAI001 // Test exercises the current MAF AIContextProvider lifecycle.
        await provider.InvokedAsync(
            new AIContextProvider.InvokedContext(
                Agent(),
                session: null,
                [new ChatMessage(ChatRole.User, "request memory")],
                [new ChatMessage(ChatRole.Assistant, "response memory")]));
#pragma warning restore MAAI001

        Assert.Equal(2, provider.CandidateWriteCount);
        Assert.Single(store.Recall(UserA, "response"));
        Assert.True(provider.ProviderNativeCandidateHook);
    }

    private static ChatClientAgent Agent()
        => new(
            new ScriptedChatClient().AddText("ok"),
            new ChatClientAgentOptions { Name = "memory-security-mock-agent" });
}
