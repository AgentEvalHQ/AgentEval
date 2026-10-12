// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.DataLoaders;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Results;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Json;
using AgentEval.Results.Runner;
using AgentEval.Results.Schemas;
using AgentEval.Results.Writing;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Runner;

/// <summary>Resolves dataset snapshots and executes text-only endpoint evaluations through bounded clients.</summary>
internal sealed class AefDatasetJobTarget : IAefJobTarget
{
    private Role? _subject, _judge;
    private AefJobCredentials? _credentials;
    private bool _captureContent;
    private long _caseSeconds = 60, _preparationSeconds = 30;
    public AefTargetMode TargetMode => AefTargetMode.Live;
    public string Isolation => "process";
    public string PriceTable { get; private set; } = "agenteval-model-pricing";
    public string Fingerprint { get; private set; } = "unresolved-dataset";
    public long CloseSeconds { get; private set; } = 5;
    public string? RefusalReason { get; private set; }
    public IReadOnlyList<IAefJobSuite> Suites { get; private set; } = [];
    public IReadOnlyCollection<string> CredentialPurposes => _judge is null ? ["subject"] : ["subject", "judge"];
    public void SetCredentials(AefJobCredentials credentials) => _credentials = credentials;

    internal JsonObject DescribeEstimate(AefJobEstimate estimate)
    {
        long remaining = estimate.Cases, runs = 0, seconds = 0;
        foreach (var suite in Suites)
        {
            var count = Math.Min(remaining, suite.Cases.Count);
            if (count == 0) break;
            runs++;
            seconds = checked(seconds + suite.PreparationSecondsBound + CloseSeconds + count * _caseSeconds);
            remaining -= count;
        }
        return new JsonObject
        {
            ["cases"] = estimate.Cases,
            ["calls"] = new JsonObject { ["target"] = checked(runs + estimate.Cases * _subject!.MaxCalls),
                ["judge"] = _judge is null ? 0 : checked(runs + estimate.Cases * _judge.MaxCalls) },
            ["models"] = new JsonArray(Roles().Select(r => r.Model).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
            ["costRange"] = new JsonObject { ["low"] = estimate.UsdLow, ["high"] = estimate.UsdHigh,
                ["currency"] = "USD", ["priceTable"] = estimate.PriceTable },
            ["durationRange"] = new JsonObject { ["low"] = 0, ["high"] = seconds, ["unit"] = "seconds" },
            ["warnings"] = new JsonArray("Calls are upper bounds and include readiness and judge retries.",
                "Cost and duration lower bounds are unknown and reported as zero; missing usage is charged at its reserved bound.",
                "The price table identifies a captured software price snapshot, not a provider quote or a pricing date."),
        };
    }

    /// <summary>Reads files only. No client, credential delivery, temporary file or provider request is created.</summary>
    internal static async Task<AefDatasetJobTarget> LoadAsync(byte[] planBytes, string planDirectory, CancellationToken ct = default)
    {
        var target = new AefDatasetJobTarget();
        JsonObject plan;
        try { plan = AefJsonReader.ParseDocument(planBytes); }
        catch (AefReadException) { throw new AefInputException("The plan is not a readable I-JSON document."); }
        // The shared engine owns schema refusal, including readable planId requirements.
        if (!AefSchemas.Reader.IsValid("run-plan", plan)) return target;
        try { await target.ResolveAsync(plan, planBytes, planDirectory, ct).ConfigureAwait(false); }
        catch (AefInputException ex)
        {
            target.RefusalReason = ex.Message;
            target.Suites = [];
        }
        return target;
    }

    private async Task ResolveAsync(JsonObject plan, byte[] planBytes, string planDirectory, CancellationToken ct)
    {
        _captureContent = (string?)plan["contentCapture"] == "on";
        var config = plan["ext"]?["agenteval.run"] as JsonObject
            ?? throw Refuse("The live dataset runner requires ext.agenteval.run endpoint and budget settings.");
        Only(config, "model", "azure", "deployment", "inputCap", "outputCap", "maxCalls", "caseSeconds", "preparationSeconds", "closeSeconds", "judge");
        var subject = plan["subject"]!.AsObject();
        var subjectRef = subject["ref"]!.GetValue<string>();
        if (!subjectRef.StartsWith("model:", StringComparison.Ordinal) && !subjectRef.StartsWith("endpoint:", StringComparison.Ordinal))
            throw Refuse("S1 can bound model and endpoint subjects only; agent services and tool targets are unsupported.");
        var model = Text(config, "model") ?? (subjectRef.StartsWith("model:", StringComparison.Ordinal) ? Uri.UnescapeDataString(subjectRef[6..]) : null);
        _subject = ReadRole(config, model, Text(subject, "endpoint"), "subject", 1);
        _caseSeconds = Positive(config, "caseSeconds", 60, 4_294_967);
        _preparationSeconds = Positive(config, "preparationSeconds", 30, 4_294_967);
        CloseSeconds = Positive(config, "closeSeconds", 5, 4_294_967);
        if (plan["judges"] is JsonArray { Count: > 0 } judges)
        {
            var judge = judges[0]!.AsObject();
            if (config["judge"] is not null and not JsonObject)
                throw Refuse("Runner judge settings must be an object.");
            var settings = config["judge"] as JsonObject ?? new JsonObject();
            Only(settings, "endpoint", "azure", "deployment", "inputCap", "outputCap", "maxCalls");
            _judge = ReadRole(settings, Text(judge, "model"), Text(settings, "endpoint") ?? _subject.Endpoint, "judge", 2);
            if (Text(judge, "provider") is { } provider && provider != _judge.Provider)
                throw Refuse("The requested judge provider does not match the configured endpoint adapter.");
            if (judge["rubricDigest"] is not null)
                throw Refuse("S1 uses the default harness judge; a requested rubric digest cannot be resolved by this target.");
        }
        else if (config["judge"] is not null) throw Refuse("Judge endpoint settings require a judge in the plan.");
        foreach (var role in Roles())
        {
            var references = (plan["credentialRefs"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(r => Text(r, "purpose") == role.Purpose).ToArray();
            if (references.Length > 1)
                throw Refuse("Each runner role accepts at most one API-key credential reference.");
            if (role.Azure && references.Length == 0)
                throw Refuse("Azure runner roles require an explicit purpose-scoped credential reference.");
        }
        var suites = new List<IAefJobSuite>();
        foreach (var item in plan["suites"]!.AsArray())
        {
            ct.ThrowIfCancellationRequested();
            var descriptor = item!.AsObject();
            var reference = descriptor["ref"]!.GetValue<string>();
            if (!reference.StartsWith("dataset:", StringComparison.Ordinal))
                throw Refuse("S1 resolves dataset:<relative-path> suites only.");
            var relative = Uri.UnescapeDataString(reference[8..]);
            if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') || string.IsNullOrWhiteSpace(relative))
                throw Refuse("Dataset references must use a relative path with forward slashes.");
            byte[] bytes;
            IReadOnlyList<DatasetTestCase> cases;
            try
            {
                var path = Path.GetFullPath(relative, Path.GetFullPath(planDirectory));
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                cases = await DatasetLoaderFactory.LoadSnapshotAsync(bytes, Path.GetExtension(path), true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Parser/file errors can quote content or absolute paths; neither belongs in a job's refusal.
                throw Refuse("A dataset snapshot could not be read or parsed with explicit unique IDs.");
            }
            if (cases.Count == 0) throw Refuse("A dataset suite must contain at least one case.");
            foreach (var value in cases)
            {
                if (!AefSchemas.Reader.IsValid("result#/properties/caseId", JsonValue.Create(value.Id)))
                    throw Refuse("A dataset ID is not a valid AEF case ID; IDs are never rewritten.");
                if (string.IsNullOrWhiteSpace(value.Input) || value.PassingScore is < 0 or > 100)
                    throw Refuse("Dataset cases require non-empty input and a passing score between 0 and 100.");
                if (value.ExpectedTools is { Count: > 0 } || value.GroundTruth is not null)
                    throw Refuse("The text-only dataset target cannot enforce tool expectations.");
                if (value.EvaluationCriteria is { Count: > 0 } && _judge is null)
                    throw Refuse("Dataset evaluation criteria require an explicit plan judge.");
            }
            suites.Add(new Suite(this, reference, descriptor["version"]!.GetValue<string>(), AefEvalWriter.DatasetDigest(bytes), cases));
        }
        Suites = suites;
        var prices = JsonSerializer.SerializeToUtf8Bytes(Roles().Select(r => new { r.Model, r.InputPrice, r.OutputPrice }));
        var priceDigest = Hash(prices);
        PriceTable = "ae-prices-" + priceDigest[..40]; // Event price-table names are at most 64 characters.
        Fingerprint = Hash(Encoding.UTF8.GetBytes(Hash(planBytes) + priceDigest + string.Join("|", suites.Select(s => s.Digest))));
    }

    private IEnumerable<Role> Roles()
    {
        yield return _subject!;
        if (_judge is not null) yield return _judge;
    }

    private static Role ReadRole(JsonObject options, string? model, string? endpoint, string purpose, int calls)
    {
        if (string.IsNullOrWhiteSpace(model) || !ModelPricing.KnownModels.Contains(model, StringComparer.OrdinalIgnoreCase)
            || ModelPricing.GetPricing(model) is not { } price || price.InputPricePerMillion < 0 || price.OutputPricePerMillion < 0)
            throw Refuse("A configured model has no exact known non-negative price; its case cost cannot be bounded.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw Refuse("Runner endpoints must be HTTP(S) URIs without user info, query or fragment.");
        var azure = options["azure"] is null ? false : options["azure"] is JsonValue v && v.TryGetValue<bool>(out var flag)
            ? flag : throw Refuse("Runner azure must be a boolean.");
        var deployment = Text(options, "deployment");
        if (deployment is not null && !azure) throw Refuse("Deployment is supported only for Azure endpoints.");
        var role = new Role(purpose, model, endpoint!, azure, deployment ?? model,
            Positive(options, "inputCap", 32768, 16_777_216), Positive(options, "outputCap", 1024, 1_000_000),
            Positive(options, "maxCalls", calls, 1000), price.InputPricePerMillion, price.OutputPricePerMillion);
        try { _ = role.Bound; }
        catch (OverflowException) { throw Refuse("A configured role cost bound cannot be represented."); }
        return role;
    }

    private BudgetedChatClient Open(Role role, bool readiness)
    {
        if (_credentials is null) throw new InvalidOperationException("Runner credentials must be delivered before opening a client.");
        var key = _credentials.ForPurpose(role.Purpose).Values.SingleOrDefault();
        var endpoint = EndpointFactory.CreateForRunner(role.Endpoint, role.Deployment, key, role.Azure);
        try
        {
            return new BudgetedChatClient(CliChatClientDiagnostics.Wrap(endpoint, role.Purpose), role.Deployment,
                role.InputCap, readiness ? Math.Min(16, role.OutputCap) : role.OutputCap,
                readiness ? 1 : role.MaxCalls, role.InputPrice, role.OutputPrice);
        }
        catch { endpoint.Dispose(); throw; }
    }

    private sealed record Role(string Purpose, string Model, string Endpoint, bool Azure, string Deployment,
        int InputCap, int OutputCap, int MaxCalls, decimal InputPrice, decimal OutputPrice)
    {
        public string Provider => Azure ? "azure" : "openai-compatible";
        public decimal Bound => checked(MaxCalls * (InputCap * InputPrice + OutputCap * OutputPrice) / 1_000_000m);
        public decimal ReadinessBound => (InputCap * InputPrice + Math.Min(16, OutputCap) * OutputPrice) / 1_000_000m;
    }

    private sealed class Suite : IAefJobSuite
    {
        private readonly AefDatasetJobTarget _owner;
        private readonly List<(Role Role, BudgetedChatClient Client, bool Readiness)> _clients = [];
        private bool _prepared, _closed;
        public Suite(AefDatasetJobTarget owner, string reference, string version, string digest, IReadOnlyList<DatasetTestCase> cases)
        {
            _owner = owner; Ref = reference; Version = version; Digest = digest;
            Cases = cases.Select(c => (IAefJobCase)new Case(this, c)).ToArray();
        }
        public string Ref { get; }
        public string Version { get; }
        public string Digest { get; }
        public string CasePath => AefEvalWriter.CasePath;
        public IReadOnlyList<IAefJobCase> Cases { get; }
        public IReadOnlyList<AefMetric> Metrics =>
        [
            new() { Id = AefEvalWriter.PassMetric, Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1) },
            new() { Id = AefEvalWriter.ScoreMetric, Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 100) },
        ];
        public IReadOnlyList<AefSummaryEntry> SummaryEntries =>
        [new() { Metric = AefEvalWriter.PassMetric, Path = CasePath }, new() { Metric = AefEvalWriter.ScoreMetric, Path = CasePath }];
        public IReadOnlyList<AefUsage>? SummaryUsage => Usage(_clients.Select(c => (c.Role, c.Client)));
        public double PreparationUsdBound => (double)_owner.Roles().Sum(r => r.ReadinessBound);
        public long PreparationSecondsBound => _owner._preparationSeconds;
        public double PreparationIncurredUsd => (double)_clients.Where(c => c.Readiness).Sum(c => c.Client.IncurredUsd);

        public async ValueTask<AefJobPreparationResult> PrepareAsync(CancellationToken ct)
        {
            if (_prepared || _closed) throw new InvalidOperationException("A dataset suite executes only once.");
            _prepared = true;
            foreach (var role in _owner.Roles())
            {
                var client = Create(role, true);
                var response = await client.GetResponseAsync("Reply with OK.", cancellationToken: ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response.Text)) return new(false, PreparationIncurredUsd, 0);
            }
            return new(true, PreparationIncurredUsd, 0);
        }

        private BudgetedChatClient Create(Role role, bool readiness)
        {
            var client = _owner.Open(role, readiness);
            _clients.Add((role, client, readiness));
            return client;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            if (_closed) return ValueTask.CompletedTask;
            _closed = true;
            List<Exception>? errors = null;
            foreach (var item in _clients)
                try { item.Client.Dispose(); }
                catch (Exception ex) { (errors ??= []).Add(ex); }
            if (errors is not null) throw new AggregateException("Runner clients could not all be released.", errors);
            return ValueTask.CompletedTask;
        }

        private sealed class Case(Suite suite, DatasetTestCase data) : IAefJobCase
        {
            private readonly List<(Role Role, BudgetedChatClient Client)> _clients = [];
            private bool _started;
            public string CaseId => data.Id;
            public double UsdLow => 0;
            public double UsdBound => (double)suite._owner.Roles().Sum(r => r.Bound);
            public long SecondsBound => suite._owner._caseSeconds;
            public double IncurredUsd => (double)_clients.Sum(c => c.Client.IncurredUsd);
            public async ValueTask<AefJobCaseResult> RunAsync(CancellationToken ct)
            {
                if (_started || !suite._prepared || suite._closed) throw new InvalidOperationException("Dataset cases execute once after readiness.");
                _started = true;
                BudgetedChatClient OpenCase(Role role)
                {
                    var client = suite.Create(role, false);
                    _clients.Add((role, client));
                    return client;
                }
                var owner = suite._owner;
                var agent = new ChatClientAgentAdapter(OpenCase(owner._subject!), name: "runner-subject");
                var judge = owner._judge is { } role ? OpenCase(role) : null;
                var harness = judge is null ? new MAFEvaluationHarness(verbose: false) : new MAFEvaluationHarness(judge, verbose: false);
                var testCase = data.ToTestCase();
                var result = await harness.RunEvaluationAsync(agent, testCase,
                    new EvaluationOptions { Verbose = false, EvaluateResponse = judge is not null, ModelName = owner._subject!.Model }, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (_clients.Any(c => c.Client.BoundExceeded)) throw new IOException("A provider exceeded its declared token bound.");
                var mapped = AefEvalWriter.MapCase(result, CaseId, testCase.PassingScore, owner._judge?.Model);
                return new AefJobCaseResult(mapped.Root with { Usage = Usage(_clients) }, IncurredUsd, 0,
                    mapped.Children.Select((child, index) => new AefJobCaseNode(child)
                    {
                        // MapCase records criteria first, in their original order.
                        Content = owner._captureContent && !result.JudgeFailed && result.CriteriaResults is { } criteria
                            && index < criteria.Count
                            ? [new(AefEvidenceKind.JudgeReasoning, criteria[index].Explanation)] : null,
                    }).ToArray(),
                    owner._judge is { } graded && !result.JudgeFailed && result.CriteriaResults is not null
                        ? [new AefJudge { Model = graded.Model, Provider = graded.Provider, RubricDigest = result.JudgeRubricDigest, Mode = AefJudgeMode.Single }] : null)
                {
                    Content = owner._captureContent ?
                    [
                        new(AefEvidenceKind.Input, testCase.Input),
                        new(AefEvidenceKind.Output, result.ActualOutput),
                        new(AefEvidenceKind.Expected, data.ExpectedOutput),
                        new(AefEvidenceKind.JudgeReasoning, !result.JudgeFailed && result.CriteriaResults is not null
                            && result.Details.Length > 0 ? result.Details : null),
                    ] : null,
                };
            }
        }
    }

    private static IReadOnlyList<AefUsage>? Usage(IEnumerable<(Role Role, BudgetedChatClient Client)> clients)
    {
        var groups = clients.Where(c => c.Client.Calls > 0).GroupBy(c => (c.Role.Purpose, c.Role.Model));
        var result = groups.Select(g => new AefUsage
        {
            Role = g.Key.Purpose == "subject" ? AefUsageRole.Agent : AefUsageRole.Judge, Model = g.Key.Model,
            InputTokens = g.All(c => c.Client.InputTokens is not null) ? g.Sum(c => c.Client.InputTokens!.Value) : null,
            OutputTokens = g.All(c => c.Client.OutputTokens is not null) ? g.Sum(c => c.Client.OutputTokens!.Value) : null,
            CostUsd = (double)g.Sum(c => c.Client.IncurredUsd),
            CostSource = "AgentEval runner price snapshot; unreported or interrupted usage charged at declared bounds",
        }).ToArray();
        return result.Length == 0 ? null : result;
    }

    private static AefInputException Refuse(string message) => new(message);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string? Text(JsonObject value, string name) => value[name] is null ? null
        : value[name] is JsonValue text && text.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result)
            ? result : throw Refuse("A runner text setting is empty or has the wrong type.");
    private static int Positive(JsonObject value, string name, int fallback, int maximum) => value[name] is null ? fallback
        : value[name] is JsonValue number && number.TryGetValue<int>(out var result) && result > 0 && result <= maximum
            ? result : throw Refuse("Runner bounds must be positive integers within the supported range.");
    private static void Only(JsonObject value, params string[] names)
    {
        if (value.Any(p => !names.Contains(p.Key, StringComparer.Ordinal)))
            throw Refuse("The runner settings contain an unsupported field.");
    }
}
