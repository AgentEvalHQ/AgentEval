using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Requirements;

namespace AgentEval.Results.Tests.Requirements;

public class RequirementCatalogueTests
{
    private const string Valid = """
        {"schemaVersion":"1.0","requirements":[{
          "id":"team:quality","selector":{"lane":"quality","metric":"accuracy","path":"accuracy"},
          "acceptance":{"kind":"threshold","operator":">=","value":0.8},
          "aggregation":{"kind":"all","missing":"not_measured"},"freshnessSeconds":86400
        }]}
        """;

    [Fact]
    public void Read_ValidCatalogue_PreservesTypedRulesAndExactSourceIdentity()
    {
        var bytes = Encoding.UTF8.GetBytes(Valid);
        var result = new RequirementCatalogueReader().Read(bytes);
        var requirement = Assert.Single(result.Requirements);
        Assert.Equal("team:quality", requirement.Id);
        Assert.Equal("accuracy", requirement.Selector.Path);
        Assert.Equal(0.8, requirement.Acceptance.Value);
        Assert.Equal(86400, requirement.FreshnessSeconds);
        Assert.False(requirement.AllowEffectiveOverrides);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Digest);
        Assert.NotEqual(result.Digest, new RequirementCatalogueReader().Read(Encoding.UTF8.GetBytes(Valid + "\n")).Digest);
        Assert.Throws<NotSupportedException>(() => ((IList<RequirementDefinition>)result.Requirements).Clear());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-id")]
    [InlineData("unqualified-id")]
    [InlineData("blank-selector")]
    [InlineData("padded-selector")]
    [InlineData("missing-aggregation")]
    [InlineData("missing-policy")]
    [InlineData("missing-path")]
    [InlineData("zero-freshness")]
    [InlineData("fractional-freshness")]
    [InlineData("unknown-operator")]
    [InlineData("digest-without-suite")]
    [InlineData("numeric-override")]
    [InlineData("zero-fraction")]
    [InlineData("unmeasured-acceptance")]
    public void Read_InvalidRules_RefusesWithActionableDiagnostic(string issue)
    {
        var root = JsonNode.Parse(Valid)!;
        var rule = root["requirements"]![0]!;
        switch (issue)
        {
            case "version": root["schemaVersion"] = "2.0"; break;
            case "unknown-field": rule["implicitAverage"] = true; break;
            case "duplicate-id": root["requirements"]!.AsArray().Add(rule.DeepClone()); break;
            case "unqualified-id": rule["id"] = "quality"; break;
            case "blank-selector": rule["selector"]!["lane"] = " "; break;
            case "padded-selector": rule["selector"]!["metric"] = " accuracy"; break;
            case "missing-aggregation": rule.AsObject().Remove("aggregation"); break;
            case "missing-policy": rule["aggregation"]!.AsObject().Remove("missing"); break;
            case "missing-path": rule["selector"]!.AsObject().Remove("path"); break;
            case "zero-freshness": rule["freshnessSeconds"] = 0; break;
            case "fractional-freshness": rule["freshnessSeconds"] = 1.5; break;
            case "unknown-operator": rule["acceptance"]!["operator"] = "approximately"; break;
            case "digest-without-suite": rule["selector"]!["suiteDigest"] = "sha256:" + new string('a', 64); break;
            case "numeric-override": rule["allowEffectiveOverrides"] = true; break;
            case "zero-fraction": rule["aggregation"] = JsonNode.Parse("""{"kind":"fraction","minimum":0,"missing":"not_measured"}"""); break;
            case "unmeasured-acceptance": rule["acceptance"] = JsonNode.Parse("""{"kind":"state","states":["not_measured"]}"""); break;
        }
        var error = Assert.Throws<RequirementCatalogueException>(() => new RequirementCatalogueReader().Read(Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.Contains("Expected", error.Message);
        Assert.Contains("Actual", error.Message);
        Assert.Contains("Suggestions", error.Message);
        Assert.Contains("Because", error.Message);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":\"1.0\",\"schemaVersion\":\"1.0\"}")]
    [InlineData("{\"value\":1e400}")]
    [InlineData("{} garbage")]
    public void Read_AmbiguousOrInvalidJson_RefusesBeforeEvaluation(string json) =>
        Assert.Throws<RequirementCatalogueException>(() => new RequirementCatalogueReader().Read(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Read_OversizedDocument_RefusesBeforeParsing() =>
        Assert.Throws<RequirementCatalogueException>(() => new RequirementCatalogueReader().Read(new byte[RequirementCatalogueReader.MaxBytes + 1]));

    [Theory]
    [InlineData("team/quality")]
    [InlineData("team:quality:ambiguous")]
    public void Read_IdNotCompatibleWithScopedAefIdentifiers_Refuses(string id) =>
        Assert.Throws<RequirementCatalogueException>(() => new RequirementCatalogueReader().Read(
            Encoding.UTF8.GetBytes(Valid.Replace("team:quality", id))));

    [Fact]
    public void Read_StateRuleWithFractionAndOverride_PreservesExplicitChoices()
    {
        var root = JsonNode.Parse(Valid)!;
        var rule = root["requirements"]![0]!;
        rule["acceptance"] = JsonNode.Parse("""{"kind":"state","states":["passed","warn"]}""");
        rule["aggregation"] = JsonNode.Parse("""{"kind":"fraction","minimum":0.75,"missing":"not_measured"}""");
        rule["allowEffectiveOverrides"] = true;
        var parsed = Assert.Single(new RequirementCatalogueReader().Read(Encoding.UTF8.GetBytes(root.ToJsonString())).Requirements);
        Assert.Equal("accuracy", parsed.Selector.Path);
        Assert.Equal(new[] { "passed", "warn" }, parsed.Acceptance.States);
        Assert.Equal(0.75, parsed.Aggregation.Minimum);
        Assert.True(parsed.AllowEffectiveOverrides);
    }
}
