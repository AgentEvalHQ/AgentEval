// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli/tests/AgentEval.Cli.Tests/InitCommandTests.cs
// during the v1.1 CLI consolidation. The class under test was renamed to
// DatasetInitCommand (see src/AgentEval.Cli/Commands/Classic/DatasetInitCommand.cs)
// to coexist with the in-tree workspace InitCommand. The runtime command name
// (`agenteval init`) and its surface (--format/--output/--force) are preserved.

using AgentEval.Cli.Commands.Classic;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// Tests for the InitCommand — scaffold a starter evaluation dataset.
/// </summary>
public class DatasetInitCommandTests : IDisposable
{
    private readonly string _tempDir;

    public DatasetInitCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agenteval-init-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMMAND STRUCTURE TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Create_ReturnsCommandNamedInit()
    {
        var command = DatasetInitCommand.Create();
        Assert.Equal("init", command.Name);
    }

    [Fact]
    public void Create_HasDescription()
    {
        var command = DatasetInitCommand.Create();
        Assert.False(string.IsNullOrWhiteSpace(command.Description));
    }

    [Fact]
    public void Create_Has3Options()
    {
        // --format, -o/--output, --force
        var command = DatasetInitCommand.Create();
        Assert.Equal(3, command.Options.Count);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("force")]
    public void Create_ContainsExpectedOption(string optionName)
    {
        var command = DatasetInitCommand.Create();
        Assert.Contains(command.Options,
            o => o.Name.Contains(optionName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_HasOutputOption()
    {
        // The output option uses "-o" as the primary name and "--output" as an alias.
        // Option.Name returns "-o", so we check Aliases for "--output".
        var command = DatasetInitCommand.Create();
        Assert.Contains(command.Options, o => o.Name == "-o");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // YAML FORMAT
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_DefaultFormat_CreatesYamlFile()
    {
        var filePath = Path.Combine(_tempDir, "agenteval.yaml");

        var result = await DatasetInitCommand.ExecuteAsync("yaml", filePath, force: false);

        Assert.Equal(0, result);
        Assert.True(File.Exists(filePath));
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("greeting_test", content);
        Assert.Contains("knowledge_test", content);
        Assert.Contains("reasoning_test", content);
        Assert.Contains("input:", content);
    }

    [Fact]
    public async Task ExecuteAsync_YamlFormat_ContainsExpectedStructure()
    {
        var filePath = Path.Combine(_tempDir, "agenteval.yaml");

        await DatasetInitCommand.ExecuteAsync("yaml", filePath, force: false);

        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("expected:", content);
        Assert.Contains("context:", content);
        Assert.Contains("groundTruth:", content);
        Assert.Contains("tags:", content);
        Assert.Contains("examples:", content);
        Assert.Contains("AgentEval Evaluation Dataset", content);
    }

    [Fact]
    public async Task ExecuteAsync_YamlFormat_Has3TestCases()
    {
        var filePath = Path.Combine(_tempDir, "agenteval.yaml");
        await DatasetInitCommand.ExecuteAsync("yaml", filePath, force: false);
        var content = await File.ReadAllTextAsync(filePath);

        // 3 test cases: greeting, knowledge, reasoning
        Assert.Contains("greeting_test", content);
        Assert.Contains("knowledge_test", content);
        Assert.Contains("reasoning_test", content);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // JSON FORMAT
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_JsonFormat_CreatesJsonFile()
    {
        var filePath = Path.Combine(_tempDir, "agenteval.json");

        var result = await DatasetInitCommand.ExecuteAsync("json", filePath, force: false);

        Assert.Equal(0, result);
        Assert.True(File.Exists(filePath));
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("greeting_test", content);
        Assert.Contains("\"input\":", content);
        Assert.Contains("\"expected\":", content);
    }

    [Fact]
    public async Task ExecuteAsync_JsonFormat_IsValidJson()
    {
        var filePath = Path.Combine(_tempDir, "agenteval.json");
        await DatasetInitCommand.ExecuteAsync("json", filePath, force: false);
        var content = await File.ReadAllTextAsync(filePath);

        // Should parse as valid JSON object with examples array
        var doc = System.Text.Json.JsonDocument.Parse(content);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("examples", out var examples));
        Assert.Equal(3, examples.GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_JsonFormat_ContainsAllRequiredFields()
    {
        var filePath = Path.Combine(_tempDir, "agenteval.json");
        await DatasetInitCommand.ExecuteAsync("json", filePath, force: false);
        var content = await File.ReadAllTextAsync(filePath);

        var doc = System.Text.Json.JsonDocument.Parse(content);
        var examples = doc.RootElement.GetProperty("examples");
        foreach (var item in examples.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("id", out _), "Each test case must have an 'id'");
            Assert.True(item.TryGetProperty("input", out _), "Each test case must have an 'input'");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FORMAT CASE INSENSITIVITY
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("YAML")]
    [InlineData("Yaml")]
    [InlineData("JSON")]
    [InlineData("Json")]
    public async Task ExecuteAsync_FormatCaseInsensitive_Succeeds(string format)
    {
        var ext = format.ToLowerInvariant();
        var filePath = Path.Combine(_tempDir, $"agenteval-{Guid.NewGuid()}.{ext}");

        var result = await DatasetInitCommand.ExecuteAsync(format, filePath, force: false);

        Assert.Equal(0, result);
        Assert.True(File.Exists(filePath));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FILE EXISTS HANDLING
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_FileExists_ReturnsUsageError()
    {
        var filePath = Path.Combine(_tempDir, "agenteval.yaml");
        await File.WriteAllTextAsync(filePath, "existing content");

        var result = await DatasetInitCommand.ExecuteAsync("yaml", filePath, force: false);

        Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, result);
        // Original content should be preserved
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Equal("existing content", content);
    }

    [Fact]
    public async Task ExecuteAsync_FileExistsWithForce_Overwrites()
    {
        var filePath = Path.Combine(_tempDir, "agenteval.yaml");
        await File.WriteAllTextAsync(filePath, "old content");

        var result = await DatasetInitCommand.ExecuteAsync("yaml", filePath, force: true);

        Assert.Equal(0, result);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("greeting_test", content);
        Assert.DoesNotContain("old content", content);
    }

    [Fact]
    public async Task ExecuteAsync_ForceOnNewFile_CreatesNormally()
    {
        var filePath = Path.Combine(_tempDir, "new-file.yaml");

        var result = await DatasetInitCommand.ExecuteAsync("yaml", filePath, force: true);

        Assert.Equal(0, result);
        Assert.True(File.Exists(filePath));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UNSUPPORTED FORMAT
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("csv")]
    [InlineData("xml")]
    [InlineData("toml")]
    [InlineData("markdown")]
    public async Task ExecuteAsync_UnsupportedFormat_ReturnsUsageError(string format)
    {
        var filePath = Path.Combine(_tempDir, $"agenteval.{format}");

        var result = await DatasetInitCommand.ExecuteAsync(format, filePath, force: false);

        Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, result);
        Assert.False(File.Exists(filePath));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CUSTOM OUTPUT PATH
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_CustomOutputPath_CreatesAtSpecifiedPath()
    {
        var filePath = Path.Combine(_tempDir, "subdir", "custom-tests.yaml");

        var result = await DatasetInitCommand.ExecuteAsync("yaml", filePath, force: false);

        Assert.Equal(0, result);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task ExecuteAsync_DeepNesting_CreatesDirectories()
    {
        var filePath = Path.Combine(_tempDir, "a", "b", "c", "deep-test.json");

        var result = await DatasetInitCommand.ExecuteAsync("json", filePath, force: false);

        Assert.Equal(0, result);
        Assert.True(File.Exists(filePath));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // STDERR OUTPUT
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_OutputsGuidanceToStderr()
    {
        var filePath = Path.Combine(_tempDir, "agenteval.yaml");
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            await DatasetInitCommand.ExecuteAsync("yaml", filePath, force: false);
            var output = sw.ToString();
            Assert.Contains("Created", output);
            Assert.Contains("agenteval eval", output);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
