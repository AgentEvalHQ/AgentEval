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
        "launcherStatus",
        "source",
        "boundaries",
        "mechanisms",
        "threats",
        "externalEffects",
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
        Assert.Equal(19, samples.GetArrayLength());

        var program = File.ReadAllText(programPath);
        var catalog = File.ReadAllText(catalogPath);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var handlers = new HashSet<string>(StringComparer.Ordinal);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            var launcher = RequiredString(sample, "launcherStatus");

            Assert.True(ids.Add(id), $"Duplicate Gatekeeper sample id '{id}'.");
            Assert.True(handlers.Add(handler), $"Duplicate Gatekeeper sample handler '{handler}'.");
            Assert.True(sources.Add(source), $"Duplicate Gatekeeper sample source '{source}'.");
            Assert.Contains(execution, new[] { "offline", "live-model", "live-boundary", "hybrid" });
            Assert.Contains(complexity, new[] { "introductory", "intermediate", "advanced" });
            Assert.Contains(launcher, new[] { "menu", "direct" });

            RequiredString(sample, "name");
            RequiredString(sample, "description");
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

            if (launcher == "menu")
            {
                Assert.Contains($"{handler}.RunAsync", program, StringComparison.Ordinal);
            }

            Assert.Contains($"| {id} |", catalog, StringComparison.Ordinal);
        }

        var sourceFiles = Directory
            .EnumerateFiles(
                Path.Combine(root, "samples", "AgentEval.Samples", "Gatekeeper"),
                "*_Gatekeeper*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(sourceFiles.Order().ToArray(), sources.Order().ToArray());
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
