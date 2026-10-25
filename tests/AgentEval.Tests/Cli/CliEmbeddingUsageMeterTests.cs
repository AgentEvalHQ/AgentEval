// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Cli.Infrastructure;
using AgentEval.Results.Writing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Cli;

public class CliEmbeddingUsageMeterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_MissingSdkCounts_DoesNotInventOutputOrCost(bool inputReported)
    {
        using var meter = CliUsageMeter.Begin();
        using var generator = CliUsageMeter.WrapEmbeddings(new Generator(_ => Response(
            inputReported ? new UsageDetails { InputTokenCount = 7 } : null)), "BAAI/bge-m3");
        using (CliUsageMeter.Attribute(AefUsageRole.Judge, "case-a"))
            await generator.GenerateAsync(["text"]);

        var usage = Assert.Single(CliUsageMeter.ForCase("case-a")!);
        Assert.Equal(AefUsageRole.Other, usage.Role);
        Assert.Equal("BAAI/bge-m3", usage.Model);
        Assert.Equal(inputReported ? 7L : (long?)null, usage.InputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.CostUsd);
    }

    [Fact]
    public async Task Snapshot_FailedSecondCall_KeepsCaseOwnershipAndMakesTotalUnknown()
    {
        using var meter = CliUsageMeter.Begin();
        using var generator = CliUsageMeter.WrapEmbeddings(new Generator(call => call == 1
            ? Response(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 0 })
            : throw new IOException("synthetic refusal")), "BAAI/bge-m3");
        using (CliUsageMeter.Attribute(AefUsageRole.Judge, "first"))
            await generator.GenerateAsync(["first"]);
        using (CliUsageMeter.Attribute(AefUsageRole.Judge, "second"))
            await Assert.ThrowsAsync<IOException>(() => generator.GenerateAsync(["second"]));

        Assert.Equal(10, Assert.Single(meter.Snapshot("first")!).InputTokens);
        var failed = Assert.Single(meter.Snapshot("second")!);
        Assert.Equal(AefUsageRole.Other, failed.Role);
        Assert.Null(failed.InputTokens);
        Assert.Null(failed.OutputTokens);
        var total = Assert.Single(meter.Snapshot()!);
        Assert.Null(total.InputTokens);
        Assert.Null(total.OutputTokens);
        Assert.Null(total.CostUsd);
    }

    [Fact]
    public async Task Snapshot_CancelledCall_IsRetainedAsUnknownAndCancellationPropagates()
    {
        using var meter = CliUsageMeter.Begin();
        using var generator = CliUsageMeter.WrapEmbeddings(new Generator(_ => Response(null)), "embedding");
        using var attribution = CliUsageMeter.Attribute(caseId: "cancelled");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            generator.GenerateAsync(["text"], cancellationToken: new CancellationToken(true)));

        var usage = Assert.Single(meter.Snapshot("cancelled")!);
        Assert.Equal("embedding", usage.Model);
        Assert.Null(usage.InputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.CostUsd);
    }

    [Fact]
    public async Task Snapshot_ReportedZeroesAndRequestModel_AreRetained()
    {
        using var meter = CliUsageMeter.Begin();
        var inner = new Generator(_ => Response(new UsageDetails { InputTokenCount = 0, OutputTokenCount = 0 }));
        using (var generator = CliUsageMeter.WrapEmbeddings(inner, "unused-default"))
            await generator.GenerateAsync(["text"], new EmbeddingGenerationOptions { ModelId = "BAAI/bge-m3" });

        var usage = Assert.Single(meter.Snapshot()!);
        Assert.Equal("BAAI/bge-m3", usage.Model);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
        Assert.Equal(0, usage.CostUsd);
        Assert.True(inner.Disposed);
    }

    [Fact]
    public void WrapEmbeddings_WithoutInvocationMeter_ReturnsOriginalGenerator()
    {
        using var generator = new Generator(_ => Response(null));
        Assert.Same(generator, CliUsageMeter.WrapEmbeddings(generator, "embedding"));
    }

    private static GeneratedEmbeddings<Embedding<float>> Response(UsageDetails? usage) =>
        new([new Embedding<float>(new float[] { 1, 0 })]) { Usage = usage };

    private sealed class Generator(Func<int, GeneratedEmbeddings<Embedding<float>>> respond)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _calls;
        public bool Disposed { get; private set; }
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(++_calls));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }
}
