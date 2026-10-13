using System.Text.Json.Nodes;
using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

public sealed class PairedMemoryAefSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-pair-export-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string Dataset()
    {
        Directory.CreateDirectory(_root);
        var rows = new JsonArray();
        for (var i = 0; i < 2; i++) rows.Add(JsonNode.Parse($$"""
            {"question_id":"q{{i}}","question_type":"single-session-user","question":"private question",
             "answer":"private gold","question_date":"2026/07/29 (Wed) 00:00",
             "haystack_sessions":[[{"role":"user","content":"private history","has_answer":true}]],
             "haystack_dates":["2026/07/01"],"haystack_session_ids":["s1"],"answer_session_ids":["s1"]}
            """));
        var path = Path.Combine(_root, "dataset.json");
        File.WriteAllText(path, rows.ToJsonString());
        return path;
    }
    private static AgentBenchmarkConfig Config(string name) => new() { AgentName = name, ModelId = "same-model" };
    private PairedMemoryAefSession Session(bool seal = true, string? root = null) => new(root ?? Path.Combine(_root, "aef"), "subset",
        new AefCompositeRunOptions
        {
            SubjectRef = "agent:memory", TargetMode = AefTargetMode.Mocked, SuiteRef = "unused:prepared", Lane = "longmemeval",
            Seal = seal,
        }, "fixture");
    private static ExternalBenchmarkOptions Options(string path) => new()
    {
        DatasetPath = path, HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory, MaxJudgeRetries = 0, RandomSeed = 42,
    };
    private static JsonNode Pair(PairedMemoryArtifact artifact) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(artifact.Directory, "summary.json")))!["ext"]!["agenteval.memoryPair"]!;
    private static ScriptedChatClient Judge() => new ScriptedChatClient().AddText("no").AddText("no").AddText("yes").AddText("yes");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_CompletedPair_PreservesSeparateScoresAndBackwardLink(bool seal)
    {
        var path = Dataset();
        using var judge = Judge(); using var oracle = new ScriptedChatClient().AddText("private oracle answer").AddText("private oracle answer");
        var session = Session(seal);
        var result = await session.RunAsync(LongMemEvalBenchmarkRunner.Create(judge, path), new NormalAgent(), Config("normal"),
            oracle, Config("oracle"), Options(path), CancellationToken.None);
        Assert.Equal(2, session.Artifacts.Count);
        var normal = session.Artifacts[0]; var ceiling = session.Artifacts[1];
        Assert.Equal(LongMemEvalArm.Normal, normal.Arm);
        Assert.Equal(LongMemEvalArm.Oracle, ceiling.Arm);
        Assert.Equal(result.Plan!.PairId, (string?)Pair(normal)["pairId"]);
        Assert.Equal((string?)Pair(normal)["pairId"], (string?)Pair(ceiling)["pairId"]);
        Assert.Equal(normal.RunHash, (string?)Pair(ceiling)["normalRunHash"]);
        Assert.Equal(0, (double)Pair(normal)["accuracyPercent"]!);
        Assert.Equal(100, (double)Pair(ceiling)["accuracyPercent"]!);
        Assert.Equal(100, (double)Pair(ceiling)["oracleGapPercentagePoints"]!);
        Assert.Equal(4, (int)Pair(normal)["totalLlmCalls"]!);
        Assert.Equal(4, (int)Pair(ceiling)["totalLlmCalls"]!);
        Assert.True(File.Exists(Path.Combine(normal.Directory, "gates.ndjson")));
        Assert.False(File.Exists(Path.Combine(ceiling.Directory, "gates.ndjson")));
        foreach (var artifact in session.Artifacts)
        {
            Assert.Equal(seal ? AefOutcome.Intact : AefOutcome.Unsealed, AefRunVerifier.Verify(artifact.Directory).Outcome);
            var all = string.Concat(Directory.GetFiles(artifact.Directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.DoesNotContain("private", all);
        }
        if (seal)
        {
            var reader = new SuiteIdentityReader();
            Assert.False(new SuiteCompatibilityEvaluator().Compare(reader.Read(normal.Directory), reader.Read(ceiling.Directory)).Compatible);
        }
        else Assert.Null(normal.RunHash);
    }

    [Theory]
    [InlineData(LongMemEvalArm.Normal, false)]
    [InlineData(LongMemEvalArm.Normal, true)]
    [InlineData(LongMemEvalArm.Oracle, false)]
    [InlineData(LongMemEvalArm.Oracle, true)]
    public async Task RunAsync_InterruptedArm_SealsOnlyObservedQuestionsAndRetainsEarlierArm(LongMemEvalArm stopArm, bool cancel)
    {
        var path = Dataset();
        using var judge = Judge(); using var oracle = new ScriptedChatClient().AddText("oracle answer").AddText("oracle answer");
        using var cancellation = new CancellationTokenSource();
        var session = Session();
        var failure = await Record.ExceptionAsync(() => session.RunAsync(LongMemEvalBenchmarkRunner.Create(judge, path), new NormalAgent(),
            Config("normal"), oracle, Config("oracle"), Options(path), cancellation.Token, onQuestion: (arm, _) =>
            {
                if (arm != stopArm) return;
                if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                throw new InvalidOperationException("interrupted fixture");
            }));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(stopArm == LongMemEvalArm.Normal ? 1 : 2, session.Artifacts.Count);
        var aborted = session.Artifacts[^1];
        Assert.Equal("aborted", aborted.Status);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(aborted.Directory).Outcome);
        Assert.Single(File.ReadAllLines(Path.Combine(aborted.Directory, "results.ndjson")));
        Assert.Equal(2, (int)Pair(aborted)["plannedQuestions"]!);
        Assert.Equal(1, (int)Pair(aborted)["observedQuestions"]!);
        Assert.False((bool)Pair(aborted)["complete"]!);
        Assert.Null(Pair(aborted)["accuracyPercent"]);
        Assert.Null(Pair(aborted)["oracleGapPercentagePoints"]);
        Assert.False(File.Exists(Path.Combine(aborted.Directory, "gates.ndjson")));
        if (stopArm == LongMemEvalArm.Oracle)
        {
            Assert.Equal("completed", session.Artifacts[0].Status);
            Assert.Equal(session.Artifacts[0].RunHash, (string?)Pair(aborted)["normalRunHash"]);
        }
        Assert.Equal(2, new SuiteIdentityReader().Read(aborted.Directory).Definition!.CaseIds.Count);
    }

    [Fact]
    public async Task RunAsync_CancelledBeforeFirstCall_WritesEmptyNormalWithoutInventingOracle()
    {
        var path = Dataset(); using var judge = Judge(); using var oracle = new ScriptedChatClient();
        var session = Session();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(LongMemEvalBenchmarkRunner.Create(judge, path),
            new NormalAgent(), Config("normal"), oracle, Config("oracle"), Options(path), new CancellationToken(true)));
        var artifact = Assert.Single(session.Artifacts);
        Assert.Equal(LongMemEvalArm.Normal, artifact.Arm);
        Assert.Empty(File.ReadAllLines(Path.Combine(artifact.Directory, "results.ndjson")));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(artifact.Directory).Outcome);
        Assert.Equal(0, (int)Pair(artifact)["observedQuestions"]!);
    }

    [Fact]
    public async Task RunAsync_CancelBetweenArms_RetainsOnlyCompletedNormal()
    {
        var path = Dataset(); using var judge = Judge(); using var oracle = new ScriptedChatClient();
        using var cancel = new CancellationTokenSource();
        var session = Session();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(LongMemEvalBenchmarkRunner.Create(judge, path),
            new NormalAgent(), Config("normal"), oracle, Config("oracle"), Options(path), cancel.Token,
            onSaved: _ => cancel.Cancel()));
        var artifact = Assert.Single(session.Artifacts);
        Assert.Equal("completed", artifact.Status);
        Assert.Equal(LongMemEvalArm.Normal, artifact.Arm);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(artifact.Directory).Outcome);
    }

    [Fact]
    public async Task RunAsync_CancelAtFirstOracleCall_RetainsEmptyPreparedOracleAndNormalHash()
    {
        var path = Dataset(); using var judge = Judge(); using var cancel = new CancellationTokenSource();
        using var oracle = new CancellingClient(cancel);
        var session = Session();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(LongMemEvalBenchmarkRunner.Create(judge, path),
            new NormalAgent(), Config("normal"), oracle, Config("oracle"), Options(path), cancel.Token));
        Assert.Equal(2, session.Artifacts.Count);
        var artifact = session.Artifacts[1];
        Assert.Equal("aborted", artifact.Status);
        Assert.Empty(File.ReadAllLines(Path.Combine(artifact.Directory, "results.ndjson")));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(artifact.Directory).Outcome);
        Assert.Equal(session.Artifacts[0].RunHash, (string?)Pair(artifact)["normalRunHash"]);
        Assert.Null(Pair(artifact)["oracleGapPercentagePoints"]);
        Assert.Equal(2, new SuiteIdentityReader().Read(artifact.Directory).Definition!.CaseIds.Count);
    }

    [Fact]
    public async Task RunAsync_InvalidDestination_RefusesBeforeAgentCalls()
    {
        var path = Dataset(); var root = Path.Combine(_root, "file"); File.WriteAllText(root, "occupied");
        using var judge = Judge(); using var oracle = new ScriptedChatClient(); var agent = new NormalAgent();
        await Assert.ThrowsAsync<PairedMemoryEvidenceException>(() => Session(root: root).RunAsync(LongMemEvalBenchmarkRunner.Create(judge, path),
            agent, Config("normal"), oracle, Config("oracle"), Options(path), CancellationToken.None));
        Assert.Equal(0, agent.Calls);
    }

    private sealed class CancellingClient(CancellationTokenSource cancel) : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        { cancel.Cancel(); cancellationToken.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }
        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NormalAgent : IEvaluableAgent, IHistoryInjectableAgent
    {
        public string Name => "normal";
        internal int Calls { get; private set; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new AgentResponse { Text = "private normal answer" }); }
        public void InjectConversationHistory(IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns) { }
    }
}
