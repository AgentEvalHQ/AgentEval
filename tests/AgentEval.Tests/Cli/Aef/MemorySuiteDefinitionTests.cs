using AgentEval.Benchmarks;
using AgentEval.Cli.Infrastructure;
using AgentEval.Memory.Models;

namespace AgentEval.Tests.Cli.Aef;

public sealed class MemorySuiteDefinitionTests
{
    [Theory]
    [InlineData("quick", 3, "Quick")]
    [InlineData("standard", 8, "Standard")]
    [InlineData("full", 12, "Full")]
    [InlineData("diagnostic", 12, "Full")]
    [InlineData("overflow", 8, "Full")]
    public void Capture_ShippedPreset_DeclaresRecipeAndResourcesWithoutFrozenDigest(string preset, int categories, string resolved)
    {
        var benchmark = preset switch
        {
            "quick" => MemoryBenchmark.Quick, "standard" => MemoryBenchmark.Standard,
            "full" => MemoryBenchmark.Full, "diagnostic" => MemoryBenchmark.Diagnostic, _ => MemoryBenchmark.Overflow,
        };
        var manifest = MemorySuiteDefinition.Create(preset, benchmark, "judge-a", "provider-a");
        Assert.False(manifest.Frozen);
        Assert.Null(manifest.SuiteContentDigest);
        Assert.Equal(new[] { "memory." + preset }, manifest.CaseIds);
        var json = manifest.ToJson();
        Assert.Equal(categories, json["definition"]!["categories"]!.AsArray().Count);
        Assert.Equal(resolved, (string?)json["definition"]!["resolvedPreset"]);
        Assert.Equal(12, json["inputs"]!.AsObject().Count(p => p.Key.Contains(".scenarios.", StringComparison.Ordinal)));
        Assert.Contains(json["inputs"]!.AsObject(), p => p.Key.Contains(".corpus.", StringComparison.Ordinal));
        Assert.Equal(manifest.Digest, MemorySuiteDefinition.Create(preset, benchmark, "judge-a", "provider-a").Digest);
    }

    [Theory]
    [InlineData("weight")]
    [InlineData("scenario")]
    [InlineData("context")]
    [InlineData("overflow")]
    [InlineData("resolution")]
    [InlineData("judge")]
    public void Capture_ChangedAppliedConfiguration_ChangesRecipe(string change)
    {
        var baseline = MemoryBenchmark.Quick;
        var categories = baseline.Categories.ToArray();
        if (change is "weight" or "scenario") categories[0] = new MemoryBenchmarkCategory
        {
            Name = categories[0].Name, Weight = change == "weight" ? 0.5 : categories[0].Weight,
            ScenarioType = change == "scenario" ? BenchmarkScenarioType.Abstention : categories[0].ScenarioType,
        };
        var modified = new MemoryBenchmark
        {
            Name = baseline.Name, Categories = categories,
            PresetResolutionKey = change == "resolution" ? "Full" : null,
            TargetTokensOverride = change == "context" ? 192_000 : null,
            OverflowCallsOverride = change == "overflow" ? 20 : null,
        };
        Assert.NotEqual(MemorySuiteDefinition.Create("quick", baseline, "judge-a", "provider").Digest,
            MemorySuiteDefinition.Create("quick", modified, change == "judge" ? "judge-b" : "judge-a", "provider").Digest);
    }
}
