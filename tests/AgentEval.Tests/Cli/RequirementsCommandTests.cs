using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Results.Requirements;

namespace AgentEval.Tests.Cli;

public sealed class RequirementsCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-requirements-" + Guid.NewGuid().ToString("N"));
    private const string Yaml = """
        schemaVersion: "1.0"
        requirements:
          - id: team:quality
            selector: {lane: quality, metric: accuracy, path: accuracy}
            acceptance: {kind: threshold, operator: ">=", value: 0.8}
            aggregation: {kind: all, missing: not_measured}
            freshnessSeconds: 86400
        """;

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string Write(string value, string name = "catalogue.yaml")
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, name);
        File.WriteAllText(file, value, new UTF8Encoding(false));
        return file;
    }

    [Fact]
    public void RunValidate_ValidYaml_ReportsExactSourceDigestWithoutWriting()
    {
        var file = Write(Yaml);
        var output = new StringWriter();
        Assert.Equal(ExitCodes.Success, RequirementsCommand.RunValidate(file, true, output, TextWriter.Null));
        var json = JsonNode.Parse(output.ToString())!;
        Assert.True((bool)json["valid"]!);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(), (string?)json["catalogueDigest"]);
        Assert.Equal("team:quality", (string?)json["requirements"]![0]);
        Assert.Single(Directory.GetFiles(_root));
        Assert.Equal(Yaml, File.ReadAllText(file));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("anchor")]
    [InlineData("alias")]
    [InlineData("tag")]
    [InlineData("documents")]
    [InlineData("syntax")]
    [InlineData("merge")]
    [InlineData("depth")]
    public void RunValidate_UnsafeOrAmbiguousYaml_ReportsJsonUsageError(string issue)
    {
        var value = issue switch
        {
            "duplicate" => Yaml + "\nschemaVersion: \"1.0\"",
            "anchor" => "schemaVersion: &version \"1.0\"",
            "alias" => "schemaVersion: *version",
            "tag" => "schemaVersion: !!str 1.0",
            "documents" => Yaml + "\n---\n" + Yaml,
            "syntax" => "requirements: [",
            "merge" => "<<: {}",
            "depth" => "x: " + new string('[', 40) + "0" + new string(']', 40),
            _ => throw new InvalidOperationException(),
        };
        var output = new StringWriter();
        Assert.Equal(ExitCodes.UsageError, RequirementsCommand.RunValidate(Write(value), true, output, TextWriter.Null));
        var json = JsonNode.Parse(output.ToString())!;
        Assert.False((bool)json["valid"]!);
        Assert.NotEmpty(json["diagnostics"]!.AsArray());
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extension")]
    [InlineData("oversized")]
    [InlineData("encoding")]
    public void RunValidate_InvalidFile_RefusesWithoutCreatingOutput(string kind)
    {
        var path = Write(kind == "oversized" ? new string('x', RequirementCatalogueReader.MaxBytes + 1) : Yaml,
            kind == "extension" ? "catalogue.txt" : "catalogue.yaml");
        if (kind == "missing") File.Delete(path);
        if (kind == "encoding") File.WriteAllBytes(path, [0xFF, 0xFE, 0x00]);
        var error = new StringWriter();
        Assert.Equal(ExitCodes.UsageError, RequirementsCommand.RunValidate(path, false, TextWriter.Null, error));
        Assert.Contains("failed", error.ToString());
    }

    [Fact]
    public void Create_ValidateInvocation_RequiresCatalogue()
    {
        Assert.NotEmpty(RequirementsCommand.Create().Parse(["validate"]).Errors);
        Assert.Empty(RequirementsCommand.Create().Parse(["validate", "catalogue.yaml", "--json"]).Errors);
    }

    [Fact]
    public void RunStatus_NoSelectedEvidence_ReportsNotMeasuredWithExitOne()
    {
        var output = new StringWriter();
        Assert.Equal(ExitCodes.TestFailure, RequirementsCommand.RunStatus(Write(Yaml), "agent:t", "v1",
            "2026-10-01T11:00:00Z", [], null, true, output, TextWriter.Null));
        var report = JsonNode.Parse(output.ToString())!;
        Assert.Equal("not_measured", (string?)report["requirements"]![0]!["status"]);
        Assert.Empty(report["selectedRunHashes"]!.AsArray());
        Assert.Equal("v1", (string?)report["subject"]!["version"]);
        Assert.Equal(64, report["policyDigest"]!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData("time")]
    [InlineData("selection")]
    [InlineData("limit")]
    [InlineData("policy")]
    [InlineData("subject")]
    public void RunStatus_InvalidInputs_ReportStructuredError(string kind)
    {
        var file = Write(Yaml);
        var output = new StringWriter();
        var selections = kind == "selection" ? new[] { "not-a-hash=folder" }
            : kind == "limit" ? Enumerable.Repeat("invalid", 257).ToArray() : [];
        var policy = kind == "policy" ? Write("{}", "trust.json") : null;
        Assert.Equal(ExitCodes.UsageError, RequirementsCommand.RunStatus(file, kind == "subject" ? "" : "agent:t", "1",
            kind == "time" ? "2026-10-01" : "2026-10-01T11:00:00Z", selections, policy, true, output, TextWriter.Null));
        Assert.True(JsonNode.Parse(output.ToString())!["error"]!.GetValue<bool>());
    }

    [Fact]
    public void Create_StatusInvocation_RequiresExplicitBindingAndTime()
    {
        Assert.NotEmpty(RequirementsCommand.Create().Parse(["status", "catalogue.yaml"]).Errors);
        Assert.Empty(RequirementsCommand.Create().Parse(["status", "catalogue.yaml", "--subject", "agent:t",
            "--version", "1", "--at", "2026-10-01T11:00:00Z", "--evidence", new string('a', 64) + "=folder", "--json"]).Errors);
    }
}
