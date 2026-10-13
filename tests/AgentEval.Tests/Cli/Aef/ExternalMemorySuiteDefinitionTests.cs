using System.Text.Json;
using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Cli.Aef;

public sealed class ExternalMemorySuiteDefinitionTests
{
    [Theory]
    [InlineData("answer")]
    [InlineData("history")]
    [InlineData("date")]
    [InlineData("roster")]
    public void Capture_ChangedSelectedOracleOrContext_ChangesIdentity(string change)
    {
        var entry = Entry("q1");
        var before = new ExternalBenchmarkPlan(new(), [entry]);
        switch (change)
        {
            case "answer": entry.AnswerRaw = JsonSerializer.SerializeToElement("different gold"); break;
            case "history": entry.HaystackSessions![0][0].Content = "different memory"; break;
            case "date": entry.HaystackDates![0] = "2026/08/01"; break;
            case "roster": entry.QuestionId = "q2"; break;
        }
        Assert.NotEqual(before.SelectedInputsDigest, new ExternalBenchmarkPlan(new(), [entry]).SelectedInputsDigest);
    }

    [Fact]
    public void Capture_SelectedContentOptionsAndTypedExtensions_AreImmutableAndAffectIdentity()
    {
        var entry = Entry("q1");
        var options = new ExternalBenchmarkOptions { DatasetPath = "private-local-path" };
        var plan = new ExternalBenchmarkPlan(options, [entry], "typed gold data");
        var original = ExternalMemorySuiteDefinition.Create("typedmemeval", "episodic", plan, "judge-a", "provider");
        plan.Configuration["MaxQuestions"] = 999;
        entry.Question = "different question";
        Assert.Equal(original.Digest, ExternalMemorySuiteDefinition.Create("typedmemeval", "episodic", plan, "judge-a", "provider").Digest);
        Assert.NotEqual(original.Digest, ExternalMemorySuiteDefinition.Create("typedmemeval", "episodic",
            new ExternalBenchmarkPlan(options, [entry], "typed gold data"), "judge-a", "provider").Digest);
        entry = Entry("q1");
        Assert.NotEqual(original.Digest, ExternalMemorySuiteDefinition.Create("typedmemeval", "episodic",
            new ExternalBenchmarkPlan(options, [entry], "changed gold extension"), "judge-a", "provider").Digest);
        Assert.NotEqual(original.Digest, ExternalMemorySuiteDefinition.Create("typedmemeval", "episodic",
            new ExternalBenchmarkPlan(new() { MaxJudgeRetries = 2 }, [entry], "typed gold data"), "judge-a", "provider").Digest);
        Assert.NotEqual(original.Digest, ExternalMemorySuiteDefinition.Create("typedmemeval", "episodic", plan, "judge-b", "provider").Digest);
        Assert.Equal(original.Digest, ExternalMemorySuiteDefinition.Create("typedmemeval", "episodic",
            new ExternalBenchmarkPlan(new() { DatasetPath = "another-path" }, [entry], "typed gold data"), "judge-a", "provider").Digest);
        Assert.DoesNotContain("private-local-path", original.ToJson().ToJsonString());
        Assert.DoesNotContain("private question", original.ToJson().ToJsonString());
        Assert.DoesNotContain("typed gold data", original.ToJson().ToJsonString());
        Assert.True(original.Frozen);
    }

    [Fact]
    public async Task TypedCapture_AllVerticals_UsesResolvedOptionsBeforeProviderCalls()
    {
        using var client = new TestClient();
        foreach (var descriptor in TypedMemEvalVerticals.All)
        {
            ExternalBenchmarkPlan? plan = null;
            await Assert.ThrowsAsync<Captured>(() => new TypedMemEvalRunner(client).RunWithProgressAsync(
                client.AsEvaluableAgent("subject"), descriptor.Vertical,
                new() { MaxQuestions = 2, RandomSeed = 42 }, null,
                onPrepared: p => { plan = p; throw new Captured(); }));
            Assert.Equal(0, client.Calls);
            Assert.Equal(2, plan!.QuestionIds.Count);
            Assert.Equal((int)descriptor.RequiredGrounding, (int)plan.Configuration["TemporalGrounding"]!);
            Assert.NotNull(plan.CorpusDigest);
            var manifest = ExternalMemorySuiteDefinition.Create("typedmemeval", descriptor.Slug, plan, "judge", "provider");
            Assert.Equal(plan.QuestionIds.OrderBy(id => id, StringComparer.Ordinal), manifest.CaseIds);
        }
    }

    [Fact]
    public async Task LongMemEval_PreparedSelection_ExecutesSameQuestionsAfterFileChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "selected-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { Entry("one"), Entry("two"), Entry("three") }));
            using var client = new TestClient();
            ExternalBenchmarkPlan? plan = null;
            var observed = new List<string>();
            var result = await new LongMemEvalBenchmarkRunner(client).RunWithProgressAsync(
                client.AsEvaluableAgent("subject"), new AgentBenchmarkConfig { AgentName = "subject" },
                new() { DatasetPath = path, MaxQuestions = 2, RandomSeed = null },
                question => observed.Add(question.QuestionId), onPrepared: p =>
                {
                    Assert.Equal(0, client.Calls);
                    plan = p;
                    File.WriteAllText(path, "[]");
                });
            Assert.Equal(2, observed.Count);
            Assert.Equal(plan!.QuestionIds, observed);
            Assert.Equal(2, result.QuestionResults.Count);
            Assert.True(ExternalMemorySuiteDefinition.Create("longmemeval", "subset", plan, "judge", "provider").Frozen);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("longmemeval", "subset")]
    [InlineData("typedmemeval", "episodic")]
    public void Capture_EmptySelection_DeclaresWriterRoot(string family, string preset)
        => Assert.Equal(new[] { family + "." + preset }, ExternalMemorySuiteDefinition.Create(
            family, preset, new ExternalBenchmarkPlan(new(), []), null, "override").CaseIds);

    private static LongMemEvalEntry Entry(string id) => new()
    {
        QuestionId = id, QuestionType = "single-session-user", Question = "private question",
        AnswerRaw = JsonSerializer.SerializeToElement("gold answer"),
        HaystackSessions = [[new() { Role = "user", Content = "history", HasAnswer = true }]],
        HaystackDates = ["2026/07/01"], HaystackSessionIds = ["s1"], AnswerSessionIds = ["s1"],
    };

    private sealed class Captured : Exception;
    private sealed class TestClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
