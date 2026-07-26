// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Guardrails.Gates;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Json.Schema;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 2, Task 2.2d — adversarial host-local lexical path containment.</summary>
public class PathContainmentPredicateTests
{
    private static readonly string Root = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "agenteval-path-contract", "allowed"));
    private static readonly string Sibling = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "agenteval-path-contract", "allowed-sibling"));

    [Fact]
    public void Constructor_NormalizesDeduplicatesDefensivelyCopiesAndBounds()
    {
        var roots = new[] { Root + Path.DirectorySeparatorChar, Path.Combine(Root, ".") };
        var predicate = new PathContainmentPredicate("path", roots, Root + Path.DirectorySeparatorChar);
        roots[0] = Sibling;

        Assert.Single(predicate.AllowedRoots);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Root), predicate.AllowedRoots[0]);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Root), predicate.BasePath);
        Assert.Throws<ArgumentException>(() => new PathContainmentPredicate("path", []));
        Assert.Throws<ArgumentException>(() => new PathContainmentPredicate("path", ["relative"]));
        Assert.Throws<ArgumentException>(() => new PathContainmentPredicate("path", [Root], "relative"));
        Assert.Throws<ArgumentException>(() =>
            new PathContainmentPredicate(
                "path",
                Enumerable.Range(0, 257).Select(index => Path.Combine(Root, index.ToString()))));
        Assert.Throws<ArgumentException>(() =>
            new PathContainmentPredicate("path", [new string('x', 32_769)]));
    }

    [Fact]
    public async Task ExactRootAndDescendantsAllow_PrefixSiblingAndOutsideBlock()
    {
        var gate = Gate();

        Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, Root)).Action);
        Assert.Equal(
            ToolGateAction.Allow,
            (await Inspect(gate, Path.Combine(Root, "folder", "file.txt"))).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, Sibling)).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await Inspect(gate, Path.GetFullPath(Path.Combine(Root, "..", "outside.txt")))).Action);
    }

    [Fact]
    public async Task DotSegmentsResolveBeforeDirectoryBoundaryComparison()
    {
        var gate = Gate();

        Assert.Equal(
            ToolGateAction.Allow,
            (await Inspect(gate, Path.Combine(Root, "folder", "..", "file.txt"))).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await Inspect(gate, Path.Combine(Root, "..", "outside", "file.txt"))).Action);
    }

    [Fact]
    public async Task RelativePathRequiresBase_AndCannotEscapeItIntoOutsideRoot()
    {
        var noBase = Gate();
        var withBase = Gate(Root);

        Assert.Equal(ToolGateAction.Block, (await Inspect(noBase, "folder/file.txt")).Action);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(withBase, "folder/file.txt")).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(withBase, "../outside/file.txt")).Action);
    }

    [Fact]
    public async Task StringAndJsonStringAreAccepted_OtherShapesFailClosed()
    {
        var gate = Gate();

        Assert.Equal(
            ToolGateAction.Allow,
            (await Inspect(gate, JsonDocument.Parse(JsonSerializer.Serialize(Path.Combine(Root, "file"))).RootElement.Clone())).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await Inspect(gate, JsonDocument.Parse("""{"path":"inside"}""").RootElement.Clone())).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, 7)).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, new[] { Root })).Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\0path")]
    [InlineData("bad\npath")]
    public async Task EmptyControlAndNulTextFailClosed(string path)
    {
        Assert.Equal(ToolGateAction.Block, (await Inspect(Gate(Root), path)).Action);
    }

    [Fact]
    public async Task OversizedAndInvalidUnicodeFailClosed()
    {
        var invalidSurrogate = new string([(char)0xD800]);
        var gate = Gate(Root);

        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, new string('x', 32_769))).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, invalidSurrogate)).Action);
    }

    [Fact]
    public async Task PlatformCaseSemanticsAreConservative()
    {
        var gate = Gate();
        var toggled = ToggleAsciiCase(Path.Combine(Root, "File.txt"));
        var verdict = await Inspect(gate, toggled);

        Assert.Equal(
            OperatingSystem.IsWindows() ? ToolGateAction.Allow : ToolGateAction.Block,
            verdict.Action);
    }

    [Fact]
    public async Task WindowsDriveRelativeAndForeignDrivePathsFailClosed()
    {
        var gate = Gate(Root);

        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, "C:relative.txt")).Action);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(ToolGateAction.Block, (await Inspect(gate, @"C:\absolute\file.txt")).Action);
        }
    }

    [Fact]
    public async Task WindowsDeviceAdsReservedAndAmbiguousNamesFailClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var gate = Gate(Root);

        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, @"\\?\C:\allowed\file.txt")).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, @"\\.\C:\allowed\file.txt")).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, Path.Combine(Root, "file.txt:secret"))).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, Path.Combine(Root, "CON.txt"))).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, Path.Combine(Root, "COM¹.txt"))).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, Path.Combine(Root, "file. "))).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, @"\rooted-only")).Action);
        Assert.Throws<ArgumentException>(() =>
            new PathContainmentPredicate("path", [@"\\server"]));
        Assert.Throws<ArgumentException>(() =>
            new PathContainmentPredicate("path", [@"\\server\..\allowed"]));
    }

    [Fact]
    public async Task WindowsSeparatorMixingStillResolvesTraversal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var gate = Gate();
        var mixedEscape = Root + "/folder\\..\\..\\outside.txt";

        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, mixedEscape)).Action);
    }

    [Fact]
    public async Task UncCandidateRequiresAContainingRootOnTheSameShare()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<ArgumentException>(() =>
                new PathContainmentPredicate("path", [@"\\server\share\allowed"]));
            return;
        }

        var gate = GateForRoot(@"\\server\share\allowed");

        Assert.Equal(
            ToolGateAction.Allow,
            (await Inspect(gate, @"\\server\share\allowed\file.txt")).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await Inspect(gate, @"\\server\share2\allowed\file.txt")).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await Inspect(gate, @"\\other\share\allowed\file.txt")).Action);
    }

    [Fact]
    public async Task UnixColonIsTreatedAsAHostLocalFilenameCharacter()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var gate = Gate(Root);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, "folder:name/file.txt")).Action);
    }

    [Fact]
    public async Task FuzzStyleTraversalDepthsNeverEscapeRoot()
    {
        var gate = Gate(Root);
        for (var depth = 1; depth <= 128; depth++)
        {
            var traversal = string.Join(
                Path.DirectorySeparatorChar,
                Enumerable.Repeat("..", depth).Append("outside.txt"));
            var verdict = await Inspect(gate, traversal);

            Assert.Equal(ToolGateAction.Block, verdict.Action);
        }
    }

    [Fact]
    public async Task BlockReasonDoesNotEchoPathOrConfiguredRoot()
    {
        var gate = Gate();
        var secretPath = Path.Combine(Sibling, "SUPER-SECRET-PATH");

        var verdict = await Inspect(gate, secretPath);

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Contains("pathContainment", verdict.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(secretPath, verdict.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(Root, verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataAndFingerprintAreStableAndSecretFree()
    {
        var first = GateForRoot(Path.Combine(Root, "."));
        var equivalent = GateForRoot(Root + Path.DirectorySeparatorChar);
        var different = GateForRoot(Sibling);

        Assert.Equal(GateCost.PureCode, first.Cost);
        Assert.Equal(GateRequirements.None, first.Requirements);
        Assert.Equal(first.ConfigurationFingerprint, equivalent.ConfigurationFingerprint);
        Assert.NotEqual(first.ConfigurationFingerprint, different.ConfigurationFingerprint);
        Assert.DoesNotContain(Root, first.ConfigurationFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FluentBuilderProducesNormalizedPredicate()
    {
        GatekeeperOptions? captured = null;
        NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            options.Contract("write", builder => builder.PathContainment("path", [Root], Root));
            captured = options;
        });

        var gate = Assert.IsType<ToolUsageContractGate>(captured!.ToolGates[0]);
        var predicate = Assert.IsType<PathContainmentPredicate>(gate.Contracts[0].Predicates.Single());
        Assert.Equal(Path.TrimEndingDirectorySeparator(Root), predicate.AllowedRoots.Single());
    }

    [Fact]
    public async Task JsonConfigurationHasFluentParityAndIsOperational()
    {
        var json = Document(
            $$"""{"kind":"pathContainment","argument":"path","allowedRoots":[{{JsonSerializer.Serialize(Root)}}],"basePath":{{JsonSerializer.Serialize(Root)}}}""");
        var parsed = ResolveJson(json);
        var fluent = Gate(Root);

        Assert.Equal(fluent.ConfigurationFingerprint, parsed.ConfigurationFingerprint);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(parsed, "child/file.txt")).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(parsed, "../outside.txt")).Action);
    }

    [Theory]
    [InlineData("""{"kind":"pathContainment","argument":"path","allowedRoots":[]}""", "root_count_limit")]
    [InlineData("""{"kind":"pathContainment","argument":"path","allowedRoots":[7]}""", "root_type")]
    [InlineData("""{"kind":"pathContainment","argument":"path","allowedRoots":[" "]}""", "empty_root")]
    [InlineData("""{"kind":"pathContainment","argument":"path","allowedRoots":["relative"]}""", "invalid_path_configuration")]
    [InlineData("""{"kind":"pathContainment","argument":"path","allowedRoots":["/"],"basePath":7}""", "base_path_type")]
    [InlineData("""{"kind":"pathContainment","argument":"path","allowedRoots":["/"],"basePath":" "}""", "empty_base_path")]
    public void JsonConfigurationRejectsInvalidShapesWithSanitizedErrors(string predicate, string code)
    {
        var exception = Assert.Throws<GatekeeperContractConfigurationException>(() =>
            new GatekeeperOptions().LoadContractsFromJson(Document(predicate)));

        Assert.Equal(code, exception.ErrorCode);
        Assert.DoesNotContain("relative", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLimitsRejectTooManyAndOverlongRoots()
    {
        var roots = string.Join(
            ",",
            Enumerable.Range(0, 257).Select(index => JsonSerializer.Serialize(Path.Combine(Root, index.ToString()))));
        var overlong = JsonSerializer.Serialize(new string('x', 32_769));

        Assert.Equal(
            "root_count_limit",
            Assert.Throws<GatekeeperContractConfigurationException>(() =>
                new GatekeeperOptions().LoadContractsFromJson(
                    Document($$"""{"kind":"pathContainment","argument":"path","allowedRoots":[{{roots}}]}"""))).ErrorCode);
        Assert.Equal(
            "root_length_limit",
            Assert.Throws<GatekeeperContractConfigurationException>(() =>
                new GatekeeperOptions().LoadContractsFromJson(
                    Document($$"""{"kind":"pathContainment","argument":"path","allowedRoots":[{{overlong}}]}"""))).ErrorCode);
    }

    [Fact]
    public void EmbeddedSchemaAcceptsPathShapeAndRejectsUnknownProperty()
    {
        var schema = LoadSchema();
        var valid = JsonNode.Parse(Document(
            $$"""{"kind":"pathContainment","argument":"path","allowedRoots":[{{JsonSerializer.Serialize(Root)}}]}"""));
        var invalid = JsonNode.Parse(Document(
            $$"""{"kind":"pathContainment","argument":"path","allowedRoots":[{{JsonSerializer.Serialize(Root)}}],"extra":true}"""));

        Assert.True(schema.Evaluate(valid, new EvaluationOptions()).IsValid);
        Assert.False(schema.Evaluate(invalid, new EvaluationOptions()).IsValid);
    }

    private static ToolUsageContractGate Gate(string? basePath = null)
        => new([new ToolContract("write", [new PathContainmentPredicate("path", [Root], basePath)])]);

    private static ToolUsageContractGate GateForRoot(string root)
        => new([new ToolContract("write", [new PathContainmentPredicate("path", [root])])]);

    private static async Task<ToolGateVerdict> Inspect(ToolUsageContractGate gate, object value)
        => await gate.InspectAsync(new GatedToolCall(
            "write",
            new Dictionary<string, object?> { ["path"] = value },
            "agent", 0, 0, 1, false, null));

    private static string ToggleAsciiCase(string value)
        => new(value.Select(character =>
            char.IsAsciiLetter(character)
                ? char.IsUpper(character) ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character)
                : character).ToArray());

    private static string Document(string predicate)
        => $$"""{"schema":"gatekeeper.contract/1","contracts":[{"tool":"write","predicates":[{{predicate}}]}]}""";

    private static ToolUsageContractGate ResolveJson(string json)
    {
        GatekeeperOptions? captured = null;
        NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            options.LoadContractsFromJson(json);
            captured = options;
        });
        return Assert.IsType<ToolUsageContractGate>(captured!.ToolGates[0]);
    }

    private static ChatClientAgent NewAgent()
        => new(
            new ScriptedChatClient().AddText("done"),
            new ChatClientAgentOptions { Name = "path-contract-test" });

    private static JsonSchema LoadSchema()
    {
        var assembly = typeof(ToolUsageContractGate).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(".gatekeeper-contract-v1.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
