// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using AgentEval.Cli.Commands;
using AgentEval.Models;
using AgentEval.Providers;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Tests.Cli.Aef;

public class EvalEmbeddingDefinitionTests
{
    [Fact]
    public void AefOptions_EmbeddingInstrument_ChangesDefinitionAndRecordsOnlySafeIdentity()
    {
        var first = Options(Settings("embedding-a"));
        var second = Options(Settings("embedding-b"));
        var otherProvider = Options(Settings("embedding-a") with { Provider = InferenceProvider.Bitdeer, ProviderTag = "bitdeer" });

        Assert.NotEqual(first.Definition!.Digest, second.Definition!.Digest);
        Assert.NotEqual(first.Definition.Digest, otherProvider.Definition!.Digest);
        Assert.Equal("embedding-a", (string?)first.Config!.Settings!["embeddingModel"]);
        Assert.Equal("openai-compatible", (string?)first.Config.Settings["embeddingProvider"]);
        var retained = first.Definition.ToJson().ToJsonString() + first.Config.Settings.ToJsonString();
        Assert.DoesNotContain("private-key", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("path-secret", retained, StringComparison.Ordinal);
    }

    [Fact]
    public void AefOptions_StochasticUnusedMetrics_DoesNotClaimAnEmbeddingInstrument()
    {
        var options = Options(Settings("unused"), runs: 3);

        Assert.Null(options.Config!.Settings!["embeddingModel"]);
        Assert.Null(options.Definition!.ToJson()["definition"]!["embeddingModel"]);
    }

    private static EmbeddingProviderSettings Settings(string model) => new(InferenceProvider.OpenAICompatible,
        "openai-compatible", "Local", new Uri("https://example.test/path-secret"), "private-key", model,
        "OPENAI_COMPATIBLE_EMBEDDING_MODEL", false, null);

    private static AefEvalRunOptions Options(EmbeddingProviderSettings settings, int runs = 1) =>
        EvalCommand.AefOptions(new EvalOptions
        {
            Dataset = new FileInfo("embedding-fixture.json"), Model = "subject", Runs = runs, Format = "json",
            Metrics = "embed_answer_similarity,embed_response_context,embed_query_context"
        }, "subject", AefTargetMode.Mocked, null,
        [new DatasetTestCase { Id = "case", Input = "question", ExpectedOutput = "answer" }],
        Encoding.UTF8.GetBytes("same dataset bytes"), embeddingSettings: settings);
}
