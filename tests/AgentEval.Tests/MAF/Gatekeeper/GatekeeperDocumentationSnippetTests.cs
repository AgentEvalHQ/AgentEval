// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Gates;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Xunit;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class GatekeeperDocumentationSnippetTests
{
    // docs-snippet:coordinated-stack:start
    private static AIAgent BuildProtectedAgent(AIAgent baseAgent, AgentTrace trace) =>
        baseAgent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                options.Add(new ForbiddenToolGate("delete_all_customers"));
                options.Add(new RunBudgetGate(maxToolCalls: 20));
                options.AddPreGate(new TokenInjectionGate());
                options.Trace = trace;
            })
            .Build();
    // docs-snippet:coordinated-stack:end

    // docs-snippet:result-admission:start
    private static AIAgent BuildResultProtectedAgent(AIAgent baseAgent, AgentTrace trace) =>
        baseAgent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                options.AddResultGate(new ToolResultSecretGate());
                options.AddResultGate(new ToolResultSizeGate(maxLength: 4096));
                options.Trace = trace;
            })
            .Build();
    // docs-snippet:result-admission:end

    [Theory]
    [InlineData("coordinated-stack", "examples.md")]
    [InlineData("result-admission", "examples.md")]
    [InlineData("coordinated-stack", "introduction.md")]
    public void Examples_CompiledSnippet_MatchesDocumentation(string snippetId, string documentationFile)
    {
        var root = RepoRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "tests",
            "AgentEval.Tests",
            "MAF",
            "Gatekeeper",
            "GatekeeperDocumentationSnippetTests.cs"));
        var documentation = File.ReadAllText(Path.Combine(root, "docs", "gatekeeper", documentationFile));

        Assert.Equal(
            Normalize(ExtractSourceSnippet(source, snippetId)),
            Normalize(ExtractDocumentationSnippet(documentation, snippetId)));
    }

    private static string ExtractSourceSnippet(string source, string snippetId)
    {
        var startMarker = $"// docs-snippet:{snippetId}:start";
        var endMarker = $"// docs-snippet:{snippetId}:end";
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Missing source snippet markers for '{snippetId}'.");
        start = source.IndexOf('\n', start) + 1;
        return source[start..end];
    }

    private static string ExtractDocumentationSnippet(string documentation, string snippetId)
    {
        var marker = $"<!-- compiled-snippet:{snippetId} -->";
        var markerIndex = documentation.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Missing documentation marker for '{snippetId}'.");
        var fenceStart = documentation.IndexOf("```csharp", markerIndex, StringComparison.Ordinal);
        Assert.True(fenceStart >= 0, $"Missing C# fence for '{snippetId}'.");
        var contentStart = documentation.IndexOf('\n', fenceStart) + 1;
        var fenceEnd = documentation.IndexOf("\n```", contentStart, StringComparison.Ordinal);
        Assert.True(fenceEnd > contentStart, $"Missing closing fence for '{snippetId}'.");
        return documentation[contentStart..fenceEnd];
    }

    private static string Normalize(string value)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .SkipWhile(string.IsNullOrWhiteSpace)
            .Reverse()
            .SkipWhile(string.IsNullOrWhiteSpace)
            .Reverse()
            .ToArray();
        var indentation = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Min(line => line.TakeWhile(char.IsWhiteSpace).Count());
        return string.Join('\n', lines.Select(line => line.Length >= indentation ? line[indentation..] : line));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentEval.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
