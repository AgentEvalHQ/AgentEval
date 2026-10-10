using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Writing;

/// <summary>
/// A temporary folder for a run the writer writes, and the small pieces most tests need: a minimal header, a leaf
/// line, the metric <c>m</c>, a clock, a trust policy.
/// </summary>
internal sealed class WriterRun : IDisposable
{
    public const string RunId = "run-w";
    public const string Alice = "git:alice@example.com";
    public const string Bob = "spiffe://example.com/bob";

    public static readonly DateTimeOffset Start = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"aef-writer-{Guid.NewGuid():N}");

    /// <summary>The run folder (inside <see cref="Root"/>, not created yet).</summary>
    public string Dir => Path.Combine(Root, "run");

    public static AefRunHeader Header(AefContentCapture capture = AefContentCapture.On, string runId = RunId) => new()
    {
        RunId = runId,
        Producer = new AefProducer { Name = "test", Version = "1.0" },
        Subject = new AefSubject { Ref = "agent:t", Kind = AefSubjectKind.Agent, Version = "1" },
        StartedAt = Start,
        ContentCapture = capture,
        Execution = new AefExecution { TargetMode = AefTargetMode.Live },
    };

    public static AefMetric Metric(string id = "m", AefMetricKind kind = AefMetricKind.Score) =>
        new() { Id = id, Kind = kind, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1) };

    public static AefResult Leaf(string caseId, string path = "q", AefState state = AefState.Passed, double? m = 0.9, int? trial = null) => new()
    {
        CaseId = caseId,
        Path = path,
        Trial = trial,
        Evaluator = new AefEvaluator("code:q"),
        State = state,
        Scores = m is { } value ? [new AefScore { Metric = "m", Value = value }] : null,
        Reason = state is AefState.NotMeasured or AefState.NotApplicable or AefState.Skipped or AefState.Error or AefState.Pending ? "a reason" : null,
    };

    /// <summary>A writer with the metric <c>m</c> declared.</summary>
    public AefRunWriter Create(AefContentCapture capture = AefContentCapture.On)
    {
        var writer = AefRunWriter.Create(Dir, Header(capture));
        writer.SetMetrics([Metric()]);
        return writer;
    }

    /// <summary>A small closed run: two leaves, the metric <c>m</c>, one summary entry.</summary>
    public AefRunVerification WriteSmall()
    {
        var writer = Create();
        writer.AddResult(Leaf("k1", m: 0.4, state: AefState.Failed));
        writer.AddResult(Leaf("k2"));
        writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("main", [new AefSummaryEntry { Metric = "m", Path = "q" }])] });
        return writer.Close(AefRunStatus.Completed, Start.AddMinutes(5));
    }

    public static TrustPolicy Policy(IAefSigner signer, string identity = Alice, bool redact = false) =>
        new([new TrustedKey(identity, signer.PublicKey, redact ? [TrustPolicy.Redact] : null)]);

    public static AefSealOptions Options(IAefSigner? signer = null, AefSealedBy by = AefSealedBy.Producer, DateTimeOffset? at = null) =>
        new() { Signer = signer, SealedBy = by, TimeProvider = new Clock(at ?? Start.AddMinutes(10)) };

    public AefRunVerification Verify(TrustPolicy? policy = null) => AefRunVerifier.Verify(Dir, new AefVerifyOptions { Policy = policy });

    public IReadOnlyList<string> Problems(TrustPolicy? policy = null) => [.. Verify(policy).Problems.Select(p => $"{p.Path} {p.Code}")];

    public string Full(string path) => Path.Combine(Dir, path.Replace('/', Path.DirectorySeparatorChar));

    public byte[] Read(string path) => File.ReadAllBytes(Full(path));

    public JsonObject Json(string path) => JsonNode.Parse(Read(path))!.AsObject();

    public List<JsonObject> Lines(string path) =>
        [.. File.ReadAllLines(Full(path)).Select(l => JsonNode.Parse(l)!.AsObject())];

    /// <summary>Every file of the run, by path, with its bytes.</summary>
    public Dictionary<string, byte[]> Snapshot() =>
        AefRunFolder.Open(Dir).Files.ToDictionary(p => p, Read, StringComparer.Ordinal);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
