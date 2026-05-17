// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core.Benchmarks;
using AgentEval.Memory.External.LongMemEval;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Integration tests that <see cref="BenchmarkFamilyRegistry"/> auto-registers the
/// Memory + LongMemEval families on assembly load (Phase 8 / v0.10.0-beta).
/// </summary>
public class BenchmarkFamilyRegistryIntegrationTests
{
    [Fact]
    public void MemoryFamily_AutoRegistersOnAssemblyLoad()
    {
        // Touch the MemoryBenchmark type to force AgentEval.Memory assembly load.
        _ = typeof(MemoryBenchmark).Assembly;

        var family = BenchmarkFamilyRegistry.TryGet("memory");
        Assert.NotNull(family);
        Assert.Equal("memory", family!.Name);
        Assert.Contains(family.Presets, p => p.Name == "quick");
        Assert.Contains(family.Presets, p => p.Name == "standard");
        Assert.Contains(family.Presets, p => p.Name == "full");
        Assert.Equal(typeof(MemoryBenchmark), family.RunnerType);
    }

    [Fact]
    public void LongMemEvalFamily_AutoRegistersOnAssemblyLoad()
    {
        _ = typeof(LongMemEvalBenchmark).Assembly;

        var family = BenchmarkFamilyRegistry.TryGet("longmemeval");
        Assert.NotNull(family);
        Assert.Equal("longmemeval", family!.Name);
        Assert.Contains(family.Presets, p => p.Name == "subset");
        Assert.Contains(family.Presets, p => p.Name == "full");
        Assert.Equal(typeof(LongMemEvalBenchmarkRunner), family.RunnerType);
    }

    [Fact]
    public void MemoryFamily_RunnerFactory_ResolvesPresetByName()
    {
        _ = typeof(MemoryBenchmark).Assembly;
        var family = BenchmarkFamilyRegistry.TryGet("memory");
        Assert.NotNull(family);

        var runner = family!.RunnerFactory!("quick");
        Assert.NotNull(runner);
        Assert.IsType<MemoryBenchmark>(runner);
        Assert.Equal("Quick", ((MemoryBenchmark)runner).Name);
    }

    [Fact]
    public void LongMemEvalFamily_RunnerFactory_RequiresHostingContext()
    {
        _ = typeof(LongMemEvalBenchmark).Assembly;
        var family = BenchmarkFamilyRegistry.TryGet("longmemeval");
        Assert.NotNull(family);

        // Without a hosting context, factory should throw.
        Assert.Throws<InvalidOperationException>(() => family!.RunnerFactory!("subset"));
    }

    [Fact]
    public void LongMemEvalFamily_RunnerFactory_WithHostingContext_ReturnsRunner()
    {
        _ = typeof(LongMemEvalBenchmark).Assembly;
        var family = BenchmarkFamilyRegistry.TryGet("longmemeval");
        Assert.NotNull(family);

        using (new LongMemEvalRunnerHostingContext(new StubChatClient()))
        {
            var runner = family!.RunnerFactory!("subset");
            Assert.NotNull(runner);
            Assert.IsType<LongMemEvalBenchmarkRunner>(runner);
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("stub", new Uri("http://localhost"));
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "stub")]));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
