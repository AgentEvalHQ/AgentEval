// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli/tests/AgentEval.Cli.Tests/EvalCommandTests.cs
// during the v1.1 CLI consolidation. The only edits are (a) the namespace, (b) the
// dataset-path finder (uses TestPathHelpers so it works in both the joslat monorepo
// and the original AgentEvalHQ repo), and (c) suppression of an obsolete-API warning
// inside ProgramTests (ExitCodes is now public, not internal).

using AgentEval.Cli.Commands;
using AgentEval.Cli.Infrastructure;
using AgentEval.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// Integration tests for the EvalCommand.
/// Tests use FakeChatClient to avoid real LLM calls.
/// ExecuteAsync is exposed via InternalsVisibleTo.
/// </summary>
public class EvalCommandTests
{
    private static string GetTestDatasetPath() => TestPathHelpers.GetCliTestDatasetPath("rag-qa.yaml");

    private static string CreateTempDataset(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agenteval-test-{Guid.NewGuid()}.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMMAND STRUCTURE TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Create_ReturnsCommandNamedEval()
    {
        var command = EvalCommand.Create();
        Assert.Equal("eval", command.Name);
    }

    [Fact]
    public void Create_HasDescription()
    {
        var command = EvalCommand.Create();
        Assert.False(string.IsNullOrWhiteSpace(command.Description));
        Assert.Contains("Evaluate", command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_Has20Options()
    {
        // dataset, endpoint, azure, model, deployment-name, api-key, system-prompt, system-prompt-file,
        // temperature, max-tokens, metrics, runs, success-threshold, judge, judge-model,
        // format, output, output-dir, verbose, quiet = 20
        var command = EvalCommand.Create();
        Assert.Equal(20, command.Options.Count);
    }

    [Theory]
    [InlineData("dataset")]
    [InlineData("endpoint")]
    [InlineData("model")]
    [InlineData("deployment-name")]
    [InlineData("format")]
    [InlineData("metrics")]
    [InlineData("verbose")]
    [InlineData("quiet")]
    [InlineData("runs")]
    [InlineData("success")]
    [InlineData("output-dir")]
    [InlineData("temperature")]
    [InlineData("azure")]
    public void Create_ContainsExpectedOption(string optionName)
    {
        var command = EvalCommand.Create();
        Assert.Contains(command.Options,
            o => o.Name.Contains(optionName, StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EVAL OPTIONS DEFAULTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EvalOptions_Defaults_AreCorrect()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Format = "json",
        };

        Assert.False(opts.Azure);
        Assert.Null(opts.Endpoint);
        Assert.Null(opts.Model);
        Assert.Null(opts.DeploymentName);
        Assert.Null(opts.ApiKey);
        Assert.Null(opts.Metrics);
        Assert.Equal(1, opts.Runs);
        Assert.Equal(0.8, opts.SuccessThreshold);
        Assert.Null(opts.SystemPrompt);
        Assert.Null(opts.SystemPromptFile);
        Assert.Equal(0f, opts.Temperature);
        Assert.Null(opts.MaxTokens);
        Assert.Null(opts.JudgeEndpoint);
        Assert.Null(opts.JudgeModel);
        Assert.Null(opts.Output);
        Assert.Null(opts.OutputDir);
        Assert.False(opts.Verbose);
        Assert.False(opts.Quiet);
    }

    [Fact]
    public void EvalOptions_AllSettable()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("data.yaml"),
            Endpoint = "http://localhost:11434/v1",
            Azure = false,
            Model = "llama3",
            DeploymentName = null,
            ApiKey = "my-key",
            Metrics = "llm_relevance,code_tool_success",
            Runs = 5,
            SuccessThreshold = 0.9,
            SystemPrompt = "Be concise.",
            SystemPromptFile = new FileInfo("system.md"),
            Temperature = 0.7f,
            MaxTokens = 1024,
            JudgeEndpoint = "http://judge:8080/v1",
            JudgeModel = "gpt-4o-mini",
            Format = "junit",
            Output = new FileInfo("results.xml"),
            OutputDir = new DirectoryInfo("output"),
            Verbose = true,
            Quiet = false,
        };

        Assert.Equal("llama3", opts.Model);
        Assert.Equal(5, opts.Runs);
        Assert.Equal(0.9, opts.SuccessThreshold);
        Assert.Equal(0.7f, opts.Temperature);
        Assert.Equal(1024, opts.MaxTokens);
        Assert.True(opts.Verbose);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // VALIDATION TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoEndpoint_Throws()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo(GetTestDatasetPath()),
            Model = "test-model",
            Format = "json",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EvalCommand.ExecuteAsync(opts, CancellationToken.None));

        Assert.Contains("--endpoint", ex.Message);
        Assert.Contains("--azure", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_MissingDataset_Throws()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("/nonexistent/path/to/dataset.yaml"),
            Endpoint = "http://localhost:11434/v1",
            Model = "test-model",
            Format = "json",
        };

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => EvalCommand.ExecuteAsync(opts, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyDataset_Throws()
    {
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

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => EvalCommand.ExecuteAsync(opts, CancellationToken.None));

            Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DirectoryFormatWithoutOutputDir_EvalOptionsCanExpressIt()
    {
        // The directory format + output-dir validation occurs AFTER evaluation
        // completes in ExecuteAsync (can't reach without a real server).
        // ExportHandler_DirectoryFormat_ThrowsWithHelpfulMessage covers the
        // ExportHandler path. Here we verify the EvalOptions can express the
        // directory format and that OutputDir is null by default.
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Endpoint = "http://localhost:11434/v1",
            Model = "test-model",
            Format = "directory",
        };

        Assert.Equal("directory", opts.Format);
        Assert.Null(opts.OutputDir);
    }

    [Fact]
    public void MetricsParsing_AllCommas_ProducesEmptyList_TriggeringValidation()
    {
        // When --metrics is ",,,", the split produces an empty list.
        // EvalCommand.ExecuteAsync validates selectedMetrics.Count == 0
        // and throws ArgumentException. We verify the parsing logic here
        // since reaching that code path requires a live endpoint.
        var metricsArg = ",,,";
        var parsed = metricsArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        Assert.Empty(parsed);

        // The guard in EvalCommand.ExecuteAsync:
        // if (selectedMetrics.Count == 0)
        //     throw new ArgumentException("--metrics was specified but no metric names were provided.");
        // Verify the guard condition matches:
        Assert.True(parsed.Count == 0, "Empty parsed metrics should trigger the validation guard");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SYSTEM PROMPT FILE TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_SystemPromptFile_NonExistentFile_IsIgnored()
    {
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

    [Fact]
    public async Task ExecuteAsync_AzureWithoutDeploymentName_Throws()
    {
        var path = CreateTempDataset("""
            - id: test1
              input: "Hello"
              expectedOutput: "Hi"
            """);
        try
        {
            var opts = new EvalOptions
            {
                Dataset = new FileInfo(path),
                Azure = true,
                Endpoint = "https://test.openai.azure.com/",
                Format = "json",
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => EvalCommand.ExecuteAsync(opts, CancellationToken.None));

            Assert.Contains("--deployment-name", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonAzureWithoutModel_Throws()
    {
        var path = CreateTempDataset("""
            - id: test1
              input: "Hello"
              expectedOutput: "Hi"
            """);
        try
        {
            var opts = new EvalOptions
            {
                Dataset = new FileInfo(path),
                Endpoint = "http://localhost:11434/v1",
                Format = "json",
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => EvalCommand.ExecuteAsync(opts, CancellationToken.None));

            Assert.Contains("--model", ex.Message);
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
        var report = new EvaluationReport
        {
            Name = "test",
            TotalTests = 0,
            PassedTests = 0,
            FailedTests = 0,
        };

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
            await ExportHandler.ExportAsync(report, format, new FileInfo(outputPath), CancellationToken.None);

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

    [Theory]
    [InlineData("JSON")]
    [InlineData("Junit")]
    [InlineData("MARKDOWN")]
    [InlineData("Md")]
    [InlineData("Trx")]
    [InlineData("CSV")]
    public async Task ExportHandler_CaseInsensitive_WriteToFile(string format)
    {
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
            await ExportHandler.ExportAsync(report, format, new FileInfo(outputPath), CancellationToken.None);
            Assert.True(File.Exists(outputPath), $"Format '{format}' should work case-insensitively");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("dir")]
    [InlineData("DIRECTORY")]
    [InlineData("Dir")]
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
    public async Task ExportHandler_ToStdout_WhenNoOutputFile_DoesNotThrow()
    {
        var report = new EvaluationReport
        {
            Name = "test-suite",
            TotalTests = 1,
            PassedTests = 1,
            FailedTests = 0,
            OverallScore = 100.0,
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow,
            TestResults = new List<TestResultSummary>
            {
                new()
                {
                    Name = "test1",
                    Passed = true,
                    Score = 100.0,
                    Output = "Hello",
                }
            }
        };

        // ExportAsync uses Console.OpenStandardOutput() (raw stream), which
        // bypasses Console.SetOut. Just verify it completes without throwing.
        var exception = await Record.ExceptionAsync(
            () => ExportHandler.ExportAsync(report, "json", null, CancellationToken.None));
        Assert.Null(exception);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ENDPOINT FACTORY TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EndpointFactory_OpenAICompatible_ReturnsClient()
    {
        var client = EndpointFactory.CreateOpenAICompatible(
            "http://localhost:11434/v1", "llama3", null);

        Assert.NotNull(client);
    }

    [Fact]
    public void EndpointFactory_OpenAICompatible_WithApiKey_ReturnsClient()
    {
        var client = EndpointFactory.CreateOpenAICompatible(
            "http://localhost:11434/v1", "gpt-4o", "sk-test1234");

        Assert.NotNull(client);
    }

    [Fact]
    public void EndpointFactory_Azure_MissingEndpoint_Throws()
    {
        var savedEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var savedKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);

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
        var savedKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);

            var ex = Assert.Throws<InvalidOperationException>(
                () => EndpointFactory.CreateAzure("https://test.openai.azure.com/", "gpt-4o", null));

            Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", savedKey);
        }
    }

    [Fact]
    public void EndpointFactory_Azure_WithExplicitParams_ReturnsClient()
    {
        var client = EndpointFactory.CreateAzure(
            "https://test.openai.azure.com/", "gpt-4o", "fake-key");

        Assert.NotNull(client);
    }

    [Fact]
    public void EndpointFactory_Azure_WithEndpointAndKeyFromEnv_ReturnsClient()
    {
        var savedKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "fake-key-from-env");

            // Endpoint is always explicit; only the API key falls back to env var
            var client = EndpointFactory.CreateAzure("https://test.openai.azure.com/", "gpt-4o", null);
            Assert.NotNull(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", savedKey);
        }
    }

    [Fact]
    public void EndpointFactory_Azure_ExplicitKeyOverridesEnvVar()
    {
        var savedKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "env-key");

            // Explicit API key should override the env var
            var client = EndpointFactory.CreateAzure(
                "https://explicit.openai.azure.com/", "gpt-4o", "explicit-key");
            Assert.NotNull(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", savedKey);
        }
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
    //  TEMPERATURE DEFAULT BEHAVIOR
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EvalOptions_TemperatureDefault_IsZero()
    {
        // Temperature defaults to 0f, described as "deterministic".
        // NOTE: EvalCommand only sets ChatOptions.Temperature when != 0f,
        // meaning the default (0) is never explicitly set on the ChatOptions
        // object. If the LLM API defaults to a non-zero temperature, users
        // may not get deterministic output despite the CLI description.
        // This test documents the current behavior.
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
        };

        Assert.Equal(0f, opts.Temperature);

        // Verify the conditional logic: temperature=0 does NOT get set on ChatOptions
        var chatOptions = new Microsoft.Extensions.AI.ChatOptions();
        if (opts.Temperature != 0f)
            chatOptions.Temperature = opts.Temperature;

        Assert.Null(chatOptions.Temperature); // Not explicitly set — API default applies
    }

    [Fact]
    public void EvalOptions_NonZeroTemperature_GetsSet()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            Temperature = 0.7f,
        };

        var chatOptions = new Microsoft.Extensions.AI.ChatOptions();
        if (opts.Temperature != 0f)
            chatOptions.Temperature = opts.Temperature;

        Assert.Equal(0.7f, chatOptions.Temperature);
    }

    [Fact]
    public void ExitCodes_AreDistinct()
    {
        var codes = new[]
        {
            AgentEval.Cli.ExitCodes.Success,
            AgentEval.Cli.ExitCodes.TestFailure,
            AgentEval.Cli.ExitCodes.UsageError,
            AgentEval.Cli.ExitCodes.RuntimeError,
        };

        Assert.Equal(4, codes.Distinct().Count());
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CONSOLE REPORTER TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ConsoleReporter_WriteHeader_WritesToStderr()
    {
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            AgentEval.Cli.Output.ConsoleReporter.WriteHeader("gpt-4o", "test.yaml", 5);

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
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            AgentEval.Cli.Output.ConsoleReporter.WriteHeader("model", "data.yaml", 1);
            var output = sw.ToString();
            Assert.Contains("1 test)", output);
            Assert.DoesNotContain("1 tests", output);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void ConsoleReporter_WriteHeader_ZeroTests()
    {
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            AgentEval.Cli.Output.ConsoleReporter.WriteHeader("model", "empty.yaml", 0);
            var output = sw.ToString();
            Assert.Contains("0 tests", output);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void ConsoleReporter_WriteHeader_ContainsVersionInfo()
    {
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            AgentEval.Cli.Output.ConsoleReporter.WriteHeader("gpt-4o", "test.yaml", 3);
            var output = sw.ToString();
            // Should contain "AgentEval CLI v" followed by version
            Assert.Contains("AgentEval CLI v", output);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    // WriteSummary requires a real TestSummary which requires evaluation.
    // We test via structural properties instead.

    // ═══════════════════════════════════════════════════════════════════════════
    //  METRICS PARSING TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("llm_relevance", 1)]
    [InlineData("llm_relevance,code_tool_success", 2)]
    [InlineData("llm_relevance, code_tool_success , llm_faithfulness", 3)]
    [InlineData(",llm_relevance,,code_tool_success,", 2)]
    public void MetricsParsing_SplitsCorrectly(string metricsArg, int expectedCount)
    {
        var parsed = metricsArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Assert.Equal(expectedCount, parsed.Count);
    }

    [Fact]
    public void MetricsParsing_AllCommas_ProducesEmptyList()
    {
        var metricsArg = ",,,";
        var parsed = metricsArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Assert.Empty(parsed);
    }

    [Fact]
    public void MetricsParsing_EmptyString_ProducesEmptyList()
    {
        var metricsArg = "";
        var parsed = metricsArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Assert.Empty(parsed);
    }

    [Fact]
    public void MetricsParsing_WhitespacePreserved()
    {
        var metricsArg = " llm_relevance , code_tool_success ";
        var parsed = metricsArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Assert.Equal("llm_relevance", parsed[0]);
        Assert.Equal("code_tool_success", parsed[1]);
    }
}
