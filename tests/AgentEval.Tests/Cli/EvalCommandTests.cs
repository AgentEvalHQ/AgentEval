// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET9_0_OR_GREATER

using AgentEval.Cli.Commands;
using AgentEval.Cli.Infrastructure;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Integration tests for the EvalCommand.
/// Tests use FakeChatClient to avoid real LLM calls.
/// ExecuteAsync is exposed via InternalsVisibleTo.
/// </summary>
public class EvalCommandTests
{
    private static string GetTestDatasetPath()
    {
        // Navigate from bin/Debug/net9.0 up to repo root, then to samples/datasets
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;

        if (dir == null)
            throw new InvalidOperationException("Could not find repo root (AgentEval.sln)");

        return Path.Combine(dir.FullName, "samples", "datasets", "rag-qa.yaml");
    }

    private static string CreateTempDataset(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agenteval-test-{Guid.NewGuid()}.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // VALIDATION TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoEndpoint_Throws()
    {
        // Arrange: neither --endpoint nor --azure
        var opts = new EvalOptions
        {
            Dataset = new FileInfo(GetTestDatasetPath()),
            Model = "test-model",
            Format = "json",
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EvalCommand.ExecuteAsync(opts, CancellationToken.None));

        Assert.Contains("--endpoint", ex.Message);
        Assert.Contains("--azure", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_MissingDataset_Throws()
    {
        // Arrange: non-existent file
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("/nonexistent/path/to/dataset.yaml"),
            Endpoint = "http://localhost:11434/v1",
            Model = "test-model",
            Format = "json",
        };

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => EvalCommand.ExecuteAsync(opts, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyDataset_Throws()
    {
        // Arrange: empty YAML dataset
        var path = CreateTempDataset("examples: []");
        try
        {
            var opts = new EvalOptions
            {
                Dataset = new FileInfo(path),
                Endpoint = "http://localhost:11434/v1",
                Model = "test-model",
                Format = "json",
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => EvalCommand.ExecuteAsync(opts, CancellationToken.None));

            Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXPORT HANDLER TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExportHandler_UnknownFormat_Throws()
    {
        // Arrange
        var report = new EvaluationReport
        {
            Name = "test",
            TotalTests = 0,
            PassedTests = 0,
            FailedTests = 0,
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => ExportHandler.ExportAsync(report, "invalid-format", null, CancellationToken.None));

        Assert.Contains("invalid-format", ex.Message);
        Assert.Contains("Valid formats", ex.Message);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("junit")]
    [InlineData("xml")]
    [InlineData("markdown")]
    [InlineData("md")]
    [InlineData("trx")]
    [InlineData("csv")]
    public async Task ExportHandler_AllFormats_WriteToFile(string format)
    {
        // Arrange
        var report = new EvaluationReport
        {
            Name = "test-suite",
            TotalTests = 1,
            PassedTests = 1,
            FailedTests = 0,
            OverallScore = 95.0,
            StartTime = DateTimeOffset.UtcNow.AddSeconds(-5),
            EndTime = DateTimeOffset.UtcNow,
            TestResults = new List<TestResultSummary>
            {
                new()
                {
                    Name = "test1",
                    Passed = true,
                    Score = 95.0,
                    Output = "Paris",
                }
            }
        };

        var outputPath = Path.Combine(Path.GetTempPath(), $"agenteval-test-{Guid.NewGuid()}.out");
        try
        {
            // Act
            await ExportHandler.ExportAsync(report, format, new FileInfo(outputPath), CancellationToken.None);

            // Assert — file was created and has content
            Assert.True(File.Exists(outputPath), $"Output file not created for format '{format}'");
            var content = await File.ReadAllTextAsync(outputPath);
            Assert.False(string.IsNullOrWhiteSpace(content), $"Output file is empty for format '{format}'");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ENDPOINT FACTORY TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EndpointFactory_OpenAICompatible_ReturnsClient()
    {
        // Act — should not throw, even with fake endpoint
        var client = EndpointFactory.CreateOpenAICompatible(
            "http://localhost:11434/v1", "llama3", null);

        // Assert
        Assert.NotNull(client);
    }

    [Fact]
    public void EndpointFactory_Azure_MissingEndpoint_Throws()
    {
        // Arrange: clear env vars
        var savedEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var savedKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(
                () => EndpointFactory.CreateAzure(null, "gpt-4o", null));

            Assert.Contains("endpoint", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", savedEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", savedKey);
        }
    }

    [Fact]
    public void EndpointFactory_Azure_MissingKey_Throws()
    {
        // Arrange: set endpoint, clear key
        var savedEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var savedKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://test.openai.azure.com/");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(
                () => EndpointFactory.CreateAzure(null, "gpt-4o", null));

            Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", savedEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", savedKey);
        }
    }

    [Fact]
    public void EndpointFactory_Azure_WithExplicitParams_ReturnsClient()
    {
        // Act
        var client = EndpointFactory.CreateAzure(
            "https://test.openai.azure.com/", "gpt-4o", "fake-key");

        // Assert
        Assert.NotNull(client);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  EXIT CODE TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ExitCodes_HaveCorrectValues()
    {
        Assert.Equal(0, AgentEval.Cli.ExitCodes.Success);
        Assert.Equal(1, AgentEval.Cli.ExitCodes.TestFailure);
        Assert.Equal(2, AgentEval.Cli.ExitCodes.UsageError);
        Assert.Equal(3, AgentEval.Cli.ExitCodes.RuntimeError);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SYSTEM PROMPT FILE TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_SystemPromptFile_NonExistentFile_IsIgnored()
    {
        // Arrange: system prompt file that doesn't exist — code uses `is { Exists: true }` guard
        // so it should be silently skipped
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("/nonexistent/dataset.yaml"),
            Endpoint = "http://localhost:11434/v1",
            Model = "test-model",
            Format = "json",
            SystemPromptFile = new FileInfo("/nonexistent/system-prompt.md"),
        };

        // Should throw FileNotFoundException for dataset, NOT for the system prompt file
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => EvalCommand.ExecuteAsync(opts, CancellationToken.None));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("dir")]
    public async Task ExportHandler_DirectoryFormat_ThrowsWithHelpfulMessage(string format)
    {
        var report = new EvaluationReport
        {
            Name = "test",
            TotalTests = 1,
            PassedTests = 1,
            OverallScore = 100,
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow,
            TestResults = new List<TestResultSummary>()
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => ExportHandler.ExportAsync(report, format, null, CancellationToken.None));
        Assert.Contains("--output-dir", ex.Message);
    }

    [Fact]
    public void ExportHandler_SupportedFormats_Contains9Entries()
    {
        // The format map should have all 9 aliases (including directory + dir)
        var formats = ExportHandler.SupportedFormats;
        Assert.Equal(9, formats.Count);
        Assert.Contains("json", formats);
        Assert.Contains("junit", formats);
        Assert.Contains("xml", formats);
        Assert.Contains("markdown", formats);
        Assert.Contains("md", formats);
        Assert.Contains("trx", formats);
        Assert.Contains("csv", formats);
        Assert.Contains("directory", formats);
        Assert.Contains("dir", formats);
    }

    [Fact]
    public void ConsoleReporter_WriteHeader_WritesToStderr()
    {
        // Arrange: capture stderr
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            // Act
            AgentEval.Cli.Output.ConsoleReporter.WriteHeader("gpt-4o", "test.yaml", 5);

            // Assert
            var output = sw.ToString();
            Assert.Contains("gpt-4o", output);
            Assert.Contains("test.yaml", output);
            Assert.Contains("5 tests", output);
            Assert.Contains("AgentEval CLI", output);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void ConsoleReporter_WriteHeader_SingularTest()
    {
        // Verify pluralization: 1 test (not "1 tests")
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            AgentEval.Cli.Output.ConsoleReporter.WriteHeader("model", "data.yaml", 1);
            var output = sw.ToString();
            Assert.Contains("1 test)", output);     // singular
            Assert.DoesNotContain("1 tests", output); // not plural
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}

#endif
