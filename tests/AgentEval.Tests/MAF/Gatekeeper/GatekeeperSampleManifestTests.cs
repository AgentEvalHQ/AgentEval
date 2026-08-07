// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class GatekeeperSampleManifestTests
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
    public void Manifest_CurrentRepository_MatchesSourcesLauncherAndCatalog()
    {
        var root = RepoRoot();
        var manifestPath = Path.Combine(
            root,
            "samples",
            "AgentEval.Samples",
            "Gatekeeper",
            "sample-manifest.json");
        var programPath = Path.Combine(root, "samples", "AgentEval.Samples", "Program.cs");
        var samplesProjectPath = Path.Combine(root, "samples", "AgentEval.Samples", "AgentEval.Samples.csproj");
        var catalogPath = Path.Combine(root, "docs", "gatekeeper", "sample-index.md");

        Assert.True(File.Exists(manifestPath), $"Missing sample manifest at {manifestPath}.");
        using var document = JsonDocument.Parse(
            File.ReadAllText(manifestPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });

        var rootElement = document.RootElement;
        Assert.Equal(JsonValueKind.Object, rootElement.ValueKind);
        Assert.Equal(
            ["samples", "schemaVersion"],
            rootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("1.0", rootElement.GetProperty("schemaVersion").GetString());

        var samples = rootElement.GetProperty("samples");
        Assert.Equal(JsonValueKind.Array, samples.ValueKind);
        Assert.Equal(30, samples.GetArrayLength());

        var program = File.ReadAllText(programPath);
        var samplesProject = File.ReadAllText(samplesProjectPath);
        var catalog = File.ReadAllText(catalogPath);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var handlers = new HashSet<string>(StringComparer.Ordinal);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var menuCount = 0;
        var directCount = 0;

        foreach (var sample in samples.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, sample.ValueKind);
            Assert.Equal(
                RequiredProperties.Order().ToArray(),
                sample.EnumerateObject().Select(property => property.Name).Order().ToArray());

            var id = RequiredString(sample, "id");
            var handler = RequiredString(sample, "handler");
            var source = RequiredString(sample, "source");
            var execution = RequiredString(sample, "executionMode");
            var complexity = RequiredString(sample, "complexity");
            var composition = RequiredString(sample, "compositionMode");
            var launcher = RequiredString(sample, "launcherStatus");

            Assert.True(ids.Add(id), $"Duplicate Gatekeeper sample id '{id}'.");
            Assert.True(handlers.Add(handler), $"Duplicate Gatekeeper sample handler '{handler}'.");
            Assert.True(sources.Add(source), $"Duplicate Gatekeeper sample source '{source}'.");
            Assert.Contains(execution, new[] { "offline", "live-model", "live-boundary", "hybrid" });
            Assert.Contains(complexity, new[] { "introductory", "intermediate", "advanced" });
            Assert.Contains(composition, new[] { "supported-composite", "intentional-low-level" });
            Assert.Contains(launcher, new[] { "menu", "direct" });

            RequiredString(sample, "name");
            RequiredString(sample, "description");
            RequiredString(sample, "compositionRationale");
            RequiredString(sample, "guarantee");
            RequiredString(sample, "nonGuarantee");
            RequiredString(sample, "benignControl");
            RequiredString(sample, "passOracle");
            RequiredStringArray(sample, "boundaries");
            RequiredStringArray(sample, "mechanisms");
            RequiredStringArray(sample, "threats");
            RequiredStringArray(sample, "externalEffects");

            var fullSource = Path.GetFullPath(Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar)));
            var gatekeeperRoot = Path.GetFullPath(
                Path.Combine(root, "samples", "AgentEval.Samples", "Gatekeeper")) +
                Path.DirectorySeparatorChar;
            Assert.StartsWith(gatekeeperRoot, fullSource, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(fullSource), $"Manifest source does not exist: {source}.");
            var sourceText = File.ReadAllText(fullSource);
            Assert.Contains(
                $"GatekeeperSampleContractRenderer.Print(\"{id}\")",
                sourceText,
                StringComparison.Ordinal);

            if (int.TryParse(id, out var numericId) && numericId <= 10)
            {
                Assert.Equal("hybrid", execution);
                Assert.Contains($"GatekeeperOfflineScenarioSuite.ExecuteAsync(\"{id}\")", sourceText, StringComparison.Ordinal);
            }

            if (composition == "supported-composite")
            {
                Assert.Contains(".UseGatekeeper(", sourceText, StringComparison.Ordinal);
            }

            if (sourceText.Contains(".UseAgentEvalToolGate(", StringComparison.Ordinal))
            {
                Assert.Equal("intentional-low-level", composition);
            }

            if (launcher == "menu")
            {
                menuCount++;
                Assert.Contains($"{handler}.RunAsync", program, StringComparison.Ordinal);

                // The Gatekeeper menu registration line must carry the stable sample ID as the name prefix
                // and fit the launcher's fixed column widths (name <= 28, description <= 58) without truncation.
                var registrationIndex = program.IndexOf($"{handler}.RunAsync", StringComparison.Ordinal);
                var lineStart = program.LastIndexOf('\n', registrationIndex) + 1;
                var lineEnd = program.IndexOf('\n', registrationIndex);
                var registrationLine = program[lineStart..(lineEnd < 0 ? program.Length : lineEnd)];
                var literals = System.Text.RegularExpressions.Regex.Matches(registrationLine, "\"([^\"]*)\"");
                Assert.True(
                    literals.Count >= 2,
                    $"Menu registration for '{handler}' must carry name and description string literals.");
                var menuName = literals[0].Groups[1].Value;
                var menuDescription = literals[1].Groups[1].Value;
                Assert.StartsWith($"{id} ", menuName, StringComparison.Ordinal);
                Assert.True(
                    menuName.Length <= 28,
                    $"Menu name for '{id}' exceeds the 28-char column ({menuName.Length}): '{menuName}'.");
                Assert.True(
                    menuDescription.Length <= 58,
                    $"Menu description for '{id}' exceeds the 58-char column ({menuDescription.Length}): '{menuDescription}'.");
            }
            else
            {
                directCount++;
            }

            // The non-interactive offline suite must contain exactly the offline-capable samples.
            var suiteEntry = $"(\"{id}\", \"";
            if (execution is "offline" or "hybrid")
            {
                Assert.Contains(suiteEntry, program, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain(suiteEntry, program, StringComparison.Ordinal);
            }

            Assert.Contains($"| {id} |", catalog, StringComparison.Ordinal);

            // The first `| {id} |` occurrence is the catalog table row (the boundary matrix comes later
            // in the document). Its complexity and execution-mode CELLS must equal the manifest values —
            // cell-scoped so a matching word elsewhere in the row can never mask a wrong column.
            var catalogRowStart = catalog.IndexOf($"| {id} |", StringComparison.Ordinal);
            var catalogRowEnd = catalog.IndexOf('\n', catalogRowStart);
            var catalogRow = catalogRowEnd < 0 ? catalog[catalogRowStart..] : catalog[catalogRowStart..catalogRowEnd];
            var catalogCells = catalogRow.Split('|');
            Assert.True(catalogCells.Length > 4, $"Catalog row for '{id}' is missing columns: '{catalogRow}'.");
            var executionLabel = execution switch
            {
                "live-model" => "Live model",
                "live-boundary" => "Live boundary",
                _ => execution,
            };
            Assert.Equal(complexity, catalogCells[3].Trim(), ignoreCase: true);
            Assert.Equal(executionLabel, catalogCells[4].Trim(), ignoreCase: true);
        }

        Assert.Equal(29, menuCount);
        Assert.Equal(1, directCount);
        Assert.Equal(6, program.Split("Recommended: true", StringSplitOptions.None).Length - 1);
        Assert.Contains("], Progressive: true)", program, StringComparison.Ordinal);
        foreach (var recommendedHandler in new[]
        {
            "GatekeeperHelloWorld",
            "GatekeeperBeachhead",
            "GatekeeperExplainabilityAndTrust",
            "GatekeeperPoisonedToolKillChain",
            "GatekeeperJailbreakAndToolAbuse",
            "GatekeeperHttpWireBoundary",
        })
        {
            Assert.Contains($"{recommendedHandler}.RunAsync, Recommended: true", program, StringComparison.Ordinal);
        }
        var sourceFiles = Directory
            .EnumerateFiles(
                Path.Combine(root, "samples", "AgentEval.Samples", "Gatekeeper"),
                "*_Gatekeeper*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(sourceFiles.Order().ToArray(), sources.Order().ToArray());
        Assert.Contains(
            "<EmbeddedResource Include=\"Gatekeeper\\sample-manifest.json\"",
            samplesProject,
            StringComparison.Ordinal);
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        var text = value.GetString();
        Assert.False(string.IsNullOrWhiteSpace(text), $"Property '{propertyName}' must be non-empty.");
        Assert.InRange(text!.Length, 1, 512);
        return text;
    }

    private static void RequiredStringArray(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        Assert.InRange(value.GetArrayLength(), 1, 32);
        foreach (var item in value.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, item.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(item.GetString()));
            Assert.InRange(item.GetString()!.Length, 1, 256);
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentEval.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Could not find the repository root by walking up from the test binary.");
    }
}
