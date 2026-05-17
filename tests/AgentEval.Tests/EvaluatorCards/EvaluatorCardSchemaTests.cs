// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using Json.Schema;
using Xunit;

namespace AgentEval.Tests.EvaluatorCards;

/// <summary>
/// Locks down the MC1.5 contract: every <c>EvaluatorCards/&lt;key&gt;.json</c> embedded
/// resource validates against <c>evaluator-card.schema.json</c> v1.0, deserialises into
/// an <see cref="EvaluatorCard"/>, and references a key registered in
/// <see cref="EvaluatorCostMap"/>. Plan-08 MC1.5.0 + MC1.5.1.
/// </summary>
public class EvaluatorCardSchemaTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static JsonSchema LoadSchema()
    {
        // The schema lives in DataLoaders; load via embedded resource.
        var asm = typeof(AgentEval.Output.FileSystemOutputStore).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("evaluator-card.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static IReadOnlyList<(string ResourceName, string Json)> LoadAllShippedCards()
    {
        var asm = typeof(AgentEval.Benchmarks.AgenticBenchmark).Assembly;
        var cardResources = asm.GetManifestResourceNames()
            .Where(n => n.Contains("EvaluatorCards.") && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return cardResources.Select(rn =>
        {
            using var s = asm.GetManifestResourceStream(rn)!;
            using var r = new StreamReader(s);
            return (rn, r.ReadToEnd());
        }).ToList();
    }

    [Fact]
    public void SchemaIsEmbedded_AndLoads()
    {
        var schema = LoadSchema();
        Assert.NotNull(schema);
    }

    [Fact]
    public void AtLeastOneCard_IsEmbedded()
    {
        var cards = LoadAllShippedCards();
        Assert.NotEmpty(cards);
    }

    [Fact]
    public void EveryShippedCard_ValidatesAgainstSchema()
    {
        var schema = LoadSchema();
        var cards = LoadAllShippedCards();

        foreach (var (resourceName, json) in cards)
        {
            using var doc = JsonDocument.Parse(json);
            var result = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!result.IsValid)
            {
                var errors = string.Join("\n", (result.Details ?? Array.Empty<EvaluationResults>())
                    .SelectMany(d => d.Errors?.Select(e => $"  {d.EvaluationPath} {e.Key}={e.Value}")
                        ?? Array.Empty<string>()));
                Assert.Fail($"EvaluatorCard '{resourceName}' failed schema validation:\n{errors}");
            }
        }
    }

    [Fact]
    public void EveryShippedCard_DeserialisesToRecord()
    {
        var cards = LoadAllShippedCards();

        foreach (var (resourceName, json) in cards)
        {
            EvaluatorCard? card = JsonSerializer.Deserialize<EvaluatorCard>(json, JsonOpts);
            Assert.NotNull(card);
            Assert.False(string.IsNullOrWhiteSpace(card!.Key), $"{resourceName}: Key required");
            Assert.False(string.IsNullOrWhiteSpace(card.Name), $"{resourceName}: Name required");
            Assert.False(string.IsNullOrWhiteSpace(card.Category), $"{resourceName}: Category required");
            Assert.False(string.IsNullOrWhiteSpace(card.Description), $"{resourceName}: Description required");
            Assert.NotNull(card.ExpectedInputs);
            Assert.NotNull(card.CompatibleWith);
        }
    }

    [Fact]
    public void EveryShippedCardKey_ResolvesInEvaluatorCostMap_AndTiersMatch()
    {
        var cards = LoadAllShippedCards();

        foreach (var (resourceName, json) in cards)
        {
            var card = JsonSerializer.Deserialize<EvaluatorCard>(json, JsonOpts)!;
            var registeredTier = EvaluatorCostMap.GetTier(card.Key);
            // Note: GetTier returns Medium for unknown keys per the cost map's design. We want
            // the card's tier to match what the map says — if the card claims Trivial but the
            // map says Medium, the registry view will be inconsistent.
            Assert.Equal(registeredTier, card.CostTier);
        }
    }

    [Fact]
    public void EveryCardLinkSource_PointsAtAnExistingFile()
    {
        // Catch authoring typos like "ToolCallAccuracyAggregator.cs" when the actual
        // file is "ToolCallAccuracyAggregateEval.cs". links.source is repo-relative.
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var cards = LoadAllShippedCards();
        foreach (var (resourceName, json) in cards)
        {
            var card = JsonSerializer.Deserialize<EvaluatorCard>(json, JsonOpts)!;
            var sourcePath = card.Links?.Source;
            if (string.IsNullOrWhiteSpace(sourcePath)) continue;
            // Skip URLs — only verify repo-relative file paths.
            if (sourcePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                sourcePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

            var absolute = Path.Combine(repoRoot!, sourcePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(absolute),
                $"EvaluatorCard '{card.Key}' links.source='{sourcePath}' does not resolve to an existing file (looked for '{absolute}').");
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AgentEval.sln"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) return null;
            dir = parent.FullName;
        }
        return null;
    }

    [Fact]
    public void EveryRegisteredCostMapKey_HasAShippedCard()
    {
        // Inverse-direction lockdown: catches the case where someone registers
        // an evaluator in EvaluatorCostMap but forgets to author a card. The
        // SPA's evaluator-registry page would then silently omit a budget-
        // tier-eligible evaluator.
        //
        // Reflective lookup of the cost map's `_tiers` private field via a
        // snapshot through GetTier won't tell us the registered key set —
        // we need IsRegistered + a known evaluator-key list. The cleanest
        // path is to enumerate the cost-map source via reflection over
        // EvaluatorCostMap's internal dictionary, but that requires access
        // we don't expose. Instead we hardcode the known cost-map keys
        // here; if EvaluatorCostMap grows, this test surfaces the gap.
        var cards = LoadAllShippedCards();
        var cardKeys = cards
            .Select(c => JsonSerializer.Deserialize<EvaluatorCard>(c.Json, JsonOpts)!.Key)
            .ToHashSet(StringComparer.Ordinal);

        // Pull every key registered in the cost map by reflecting over the
        // (internal) dictionary backing EvaluatorCostMap.IsRegistered. This
        // makes the test self-updating as new evaluators land.
        var costMapType = typeof(EvaluatorCostMap);
        var tiersField = costMapType.GetField("s_tiers",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(tiersField);
        var tiers = (System.Collections.IDictionary)tiersField!.GetValue(null)!;

        var missingCards = new List<string>();
        foreach (var key in tiers.Keys)
        {
            var k = (string)key!;
            if (!cardKeys.Contains(k))
                missingCards.Add(k);
        }

        Assert.True(
            missingCards.Count == 0,
            $"EvaluatorCostMap registers keys without an EvaluatorCard JSON file: {string.Join(", ", missingCards)}. " +
            "Either author <key>.json under src/AgentEval.Evals.Agentic/EvaluatorCards/ or remove the key from EvaluatorCostMap.");
    }

    [Fact]
    public void NoDuplicateCardKeys()
    {
        var cards = LoadAllShippedCards();
        var keys = cards.Select(c =>
            JsonSerializer.Deserialize<EvaluatorCard>(c.Json, JsonOpts)!.Key).ToList();

        var duplicates = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Theory]
    [InlineData("")]                                           // missing schemaVersion
    [InlineData("{ \"schemaVersion\": \"1.0\" }")]              // only schemaVersion — missing required fields
    public void MalformedCard_FailsSchemaValidation(string json)
    {
        if (string.IsNullOrEmpty(json))
            json = "{}";

        var schema = LoadSchema();
        using var doc = JsonDocument.Parse(json);
        var result = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void MalformedCard_InvalidCostTier_FailsSchemaValidation()
    {
        const string json = """
        {
          "schemaVersion": "1.0",
          "key": "fake_eval",
          "name": "Fake",
          "category": "system",
          "version": "1.0.0",
          "description": "fake",
          "costTier": "GIGANTIC",
          "higherIsBetter": true,
          "expectedInputs": [],
          "compatibleWith": []
        }
        """;
        var schema = LoadSchema();
        using var doc = JsonDocument.Parse(json);
        var result = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(result.IsValid);
    }
}
