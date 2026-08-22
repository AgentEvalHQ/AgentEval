// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.PartnerDeskDemo.Demo;
using AgentEval.PartnerDeskDemo.Tools;

namespace AgentEval.PartnerDeskDemo.Tests;

/// <summary>
/// Holds the demo's declared contract to the same shape the shipped Gatekeeper sample manifest enforces, so the
/// claim printed before the demo runs stays reviewed rather than decorative.
/// </summary>
public sealed class DemoManifestTests
{
    private static readonly string[] RequiredProperties =
    [
        "id",
        "name",
        "handler",
        "description",
        "executionMode",
        "complexity",
        "compositionMode",
        "compositionRationale",
        "launcherStatus",
        "source",
        "boundaries",
        "mechanisms",
        "threats",
        "externalEffects",
        "guarantee",
        "nonGuarantee",
        "benignControl",
        "passOracle",
    ];

    [Fact]
    public void Manifest_CarriesTheSameEighteenReviewedFieldsAsTheGatekeeperSampleManifest()
    {
        var manifestPath = Path.Combine(RepoRoot(), "samples", "AgentEval.PartnerDeskDemo", "demo-manifest.json");
        Assert.True(File.Exists(manifestPath), $"Missing demo manifest at {manifestPath}.");

        using var document = JsonDocument.Parse(
            File.ReadAllText(manifestPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });

        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
            ["samples", "schemaVersion"],
            root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());

        var samples = root.GetProperty("samples");
        Assert.Equal(JsonValueKind.Array, samples.ValueKind);
        var sample = Assert.Single(samples.EnumerateArray());

        Assert.Equal(
            RequiredProperties.Order().ToArray(),
            sample.EnumerateObject().Select(property => property.Name).Order().ToArray());

        Assert.Contains(
            RequiredString(sample, "executionMode"),
            new[] { "offline", "live-model", "live-boundary", "hybrid" });
        Assert.Contains(
            RequiredString(sample, "complexity"),
            new[] { "introductory", "intermediate", "advanced" });
        Assert.Contains(
            RequiredString(sample, "compositionMode"),
            new[] { "supported-composite", "intentional-low-level" });
        Assert.Contains(RequiredString(sample, "launcherStatus"), new[] { "menu", "direct" });

        foreach (var arrayProperty in new[] { "boundaries", "mechanisms", "threats", "externalEffects" })
        {
            var value = sample.GetProperty(arrayProperty);
            Assert.Equal(JsonValueKind.Array, value.ValueKind);
            Assert.InRange(value.GetArrayLength(), 1, 32);
            foreach (var item in value.EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(item.GetString()));
            }
        }

        var source = RequiredString(sample, "source");
        Assert.True(
            File.Exists(Path.Combine(RepoRoot(), source.Replace('/', Path.DirectorySeparatorChar))),
            $"Manifest source does not exist: {source}.");

        // A supported-composite claim must be true of the code.
        if (RequiredString(sample, "compositionMode") == "supported-composite")
        {
            var agentSource = File.ReadAllText(Path.Combine(
                RepoRoot(), "samples", "AgentEval.PartnerDeskDemo", "Agent", "PartnerDeskAgent.cs"));
            Assert.Contains(".UseGatekeeper(", agentSource, StringComparison.Ordinal);
            Assert.DoesNotContain(".UseAgentEvalToolGate(", agentSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EmbeddedManifest_IsReadableAndMatchesTheDemoItDescribes()
    {
        var contract = DemoContractRenderer.Current;

        Assert.Equal("PD", contract.Id);
        Assert.Equal("The Trusted Supplier", contract.Name);
        Assert.Contains(contract.Mechanisms, mechanism =>
            mechanism.Contains("HiddenInstructionPrefilterGate", StringComparison.Ordinal));
        Assert.Contains(contract.Mechanisms, mechanism =>
            mechanism.Contains("ToolUsageContractGate", StringComparison.Ordinal));
    }

    [Fact]
    public void SyntheticRegister_HasEnoughRowsForTheExportToLookLikeACustomerList()
    {
        var register = PartnerRegister.Load();

        Assert.True(register.Count >= 120, $"expected at least 120 records, found {register.Count}");
        Assert.Equal("Alpina Logistik AG", register.Records[0].Company);
        Assert.All(register.Records, record =>
        {
            Assert.EndsWith(".example", record.Email, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(record.Contact));
            Assert.True(record.AnnualContractValueChf > 0);
        });
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        var text = value.GetString();
        Assert.False(string.IsNullOrWhiteSpace(text), $"Property '{propertyName}' must be non-empty.");
        Assert.InRange(text!.Length, 1, 1024);
        return text;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentEval.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test binary.");
    }
}
