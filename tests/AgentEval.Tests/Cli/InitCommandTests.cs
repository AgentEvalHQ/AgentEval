// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;

namespace AgentEval.Tests.Cli;

public class InitCommandTests : IDisposable
{
    private readonly string _root;

    public InitCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-init-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        // Plant a .git folder so WorkspaceRootDiscovery would find it, but we bypass it via rootOverride
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Init_FreshDir_CreatesSolutionAndTemplates()
    {
        var result = await InitCommand.RunAsync(name: null, rootOverride: _root);

        Assert.Equal(0, result);

        var dir = Path.Combine(_root, ".agenteval");
        Assert.True(Directory.Exists(dir));

        var solutionFile = Path.Combine(dir, "solution.json");
        Assert.True(File.Exists(solutionFile));

        var json = await File.ReadAllTextAsync(solutionFile);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("1.0", doc.RootElement.GetProperty("schemaVersion").GetString());
        Assert.True(Guid.TryParse(doc.RootElement.GetProperty("id").GetString(), out var id));
        Assert.NotEqual(Guid.Empty, id);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("name").GetString()));

        Assert.True(File.Exists(Path.Combine(dir, "README.md")));
        Assert.True(File.Exists(Path.Combine(dir, ".gitignore")));
    }

    [Fact]
    public async Task Init_AlreadyInitialized_IsNoop()
    {
        // First init
        var first = await InitCommand.RunAsync(name: "First", rootOverride: _root);
        Assert.Equal(0, first);

        var solutionFile = Path.Combine(_root, ".agenteval", "solution.json");
        var originalContent = await File.ReadAllTextAsync(solutionFile);

        // Second init
        var second = await InitCommand.RunAsync(name: "Second", rootOverride: _root);
        Assert.Equal(0, second);

        // solution.json should be unchanged
        var afterContent = await File.ReadAllTextAsync(solutionFile);
        Assert.Equal(originalContent, afterContent);

        // Name from second run should NOT have overwritten first
        var doc = JsonDocument.Parse(afterContent);
        Assert.Equal("First", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Init_WithName_UsesProvidedName()
    {
        var result = await InitCommand.RunAsync(name: "MyCustomSolution", rootOverride: _root);

        Assert.Equal(0, result);

        var solutionFile = Path.Combine(_root, ".agenteval", "solution.json");
        var json = await File.ReadAllTextAsync(solutionFile);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("MyCustomSolution", doc.RootElement.GetProperty("name").GetString());
    }
}
