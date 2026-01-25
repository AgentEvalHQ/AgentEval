// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Output;

/// <summary>
/// Base class for AgentEval test fixtures providing automatic tracing and rich output.
/// Inherit from this class to get automatic trace capture and test output.
/// </summary>
public abstract class AgentEvalTestBase : IDisposable
{
    private readonly EvaluationOutputWriter _outputWriter;
    private readonly TraceArtifactManager _artifactManager;
    private readonly TextWriter? _externalOutput;
    private readonly StringWriter _capturedOutput;
    private bool _disposed;

    /// <summary>
    /// Creates a new AgentEvalTestBase with default settings.
    /// </summary>
    protected AgentEvalTestBase() : this(null, null)
    {
    }

    /// <summary>
    /// Creates a new AgentEvalTestBase with a custom output writer.
    /// </summary>
    /// <param name="output">TextWriter for test output (e.g., Console.Out or a StringWriter).</param>
    protected AgentEvalTestBase(TextWriter? output) : this(output, null)
    {
    }

    /// <summary>
    /// Creates a new AgentEvalTestBase with custom settings.
    /// </summary>
    /// <param name="output">TextWriter for test output (e.g., Console.Out or a StringWriter).</param>
    /// <param name="settings">Custom verbosity settings.</param>
    protected AgentEvalTestBase(TextWriter? output, VerbositySettings? settings)
    {
        _externalOutput = output;
        Settings = settings ?? new VerbositySettings { Level = VerbosityConfiguration.Current };
        
        _capturedOutput = new StringWriter();
        _outputWriter = new EvaluationOutputWriter(Settings, _capturedOutput);
        _artifactManager = new TraceArtifactManager(Settings.TraceOutputDirectory ?? VerbosityConfiguration.TraceDirectory);
    }

    /// <summary>
    /// The verbosity settings for this test.
    /// </summary>
    protected VerbositySettings Settings { get; }

    /// <summary>
    /// Whether to save traces for all tests, not just failures.
    /// Default: false (only save on failure).
    /// </summary>
    protected bool SaveTracesForAllTests { get; set; } = false;

    /// <summary>
    /// Called at the end of a test to record and output results.
    /// </summary>
    /// <param name="result">The test result to record.</param>
    protected void RecordResult(TestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Write formatted output
        _outputWriter.WriteTestResult(result);

        // Save trace if configured
        if (Settings.SaveTraceFiles)
        {
            if (SaveTracesForAllTests || !result.Passed)
            {
                var tracePath = _artifactManager.SaveTestResult(result);
                _capturedOutput.WriteLine($"Trace saved: {tracePath}");
            }
        }

        // Flush to xUnit output
        FlushOutput();
    }

    /// <summary>
    /// Saves a TimeTravelTrace for debugging.
    /// </summary>
    /// <param name="trace">The trace to save.</param>
    /// <returns>Path to the saved trace file.</returns>
    protected string SaveTrace(TimeTravelTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return _artifactManager.SaveTrace(trace);
    }

    /// <summary>
    /// Gets the path to the trace output directory.
    /// </summary>
    protected string TraceDirectory => _artifactManager.OutputDirectory;

    /// <summary>
    /// Writes a message to the test output.
    /// </summary>
    /// <param name="message">Message to write.</param>
    protected void WriteLine(string message)
    {
        _capturedOutput.WriteLine(message);
    }

    /// <summary>
    /// Writes a formatted message to the test output.
    /// </summary>
    /// <param name="format">Format string.</param>
    /// <param name="args">Format arguments.</param>
    protected void WriteLine(string format, params object[] args)
    {
        _capturedOutput.WriteLine(format, args);
    }

    /// <summary>
    /// Flushes captured output to the external output writer.
    /// </summary>
    protected void FlushOutput()
    {
        if (_externalOutput is not null)
        {
            var output = _capturedOutput.ToString();
            if (!string.IsNullOrEmpty(output))
            {
                _externalOutput.Write(output);
                _externalOutput.Flush();
            }
        }

        // Clear the buffer
        _capturedOutput.GetStringBuilder().Clear();
    }

    /// <summary>
    /// Gets the captured output as a string (for assertions in tests).
    /// </summary>
    protected string GetCapturedOutput()
    {
        return _capturedOutput.ToString();
    }

    /// <summary>
    /// Creates a TestResult builder for the current test.
    /// </summary>
    /// <param name="testName">Name of the test.</param>
    /// <returns>A result builder.</returns>
    protected TestResultBuilder CreateResult(string testName)
    {
        return new TestResultBuilder(testName, this);
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    /// <param name="disposing">Whether this is a dispose call.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                FlushOutput();
                _capturedOutput.Dispose();
            }
            _disposed = true;
        }
    }

    /// <summary>
    /// Builder for constructing TestResult with fluent API.
    /// </summary>
    public sealed class TestResultBuilder
    {
        private readonly string _testName;
        private readonly AgentEvalTestBase _testBase;
        private readonly TestResult _result;
        private readonly ToolUsageReport _toolUsage;
        private readonly PerformanceMetrics _performance;

        internal TestResultBuilder(string testName, AgentEvalTestBase testBase)
        {
            _testName = testName;
            _testBase = testBase;
            _toolUsage = new ToolUsageReport();
            _performance = new PerformanceMetrics { StartTime = DateTimeOffset.UtcNow };
            _result = new TestResult 
            { 
                TestName = testName,
                ToolUsage = _toolUsage,
                Performance = _performance
            };
        }

        /// <summary>
        /// Sets the test as passed.
        /// </summary>
        /// <param name="score">Score (0-100).</param>
        public TestResultBuilder Passed(int score = 100)
        {
            _result.Passed = true;
            _result.Score = score;
            return this;
        }

        /// <summary>
        /// Sets the test as failed.
        /// </summary>
        /// <param name="details">Failure details.</param>
        /// <param name="score">Score (0-100).</param>
        public TestResultBuilder Failed(string details, int score = 0)
        {
            _result.Passed = false;
            _result.Score = score;
            _result.Details = details;
            return this;
        }

        /// <summary>
        /// Sets the actual output from the agent.
        /// </summary>
        public TestResultBuilder WithOutput(string output)
        {
            _result.ActualOutput = output;
            return this;
        }

        /// <summary>
        /// Sets an error on the result.
        /// </summary>
        public TestResultBuilder WithError(Exception error)
        {
            _result.Error = error;
            _result.Passed = false;
            return this;
        }

        /// <summary>
        /// Records a tool call.
        /// </summary>
        public TestResultBuilder WithToolCall(
            string toolName,
            string callId,
            Dictionary<string, object?>? arguments = null,
            object? result = null,
            TimeSpan? duration = null)
        {
            var call = new ToolCallRecord
            {
                Name = toolName,
                CallId = callId,
                Order = _toolUsage.Count + 1,
                Arguments = arguments,
                Result = result,
                StartTime = DateTimeOffset.UtcNow - (duration ?? TimeSpan.FromMilliseconds(50)),
                EndTime = DateTimeOffset.UtcNow
            };
            _toolUsage.AddCall(call);
            return this;
        }

        /// <summary>
        /// Sets token usage.
        /// </summary>
        public TestResultBuilder WithTokens(int promptTokens, int completionTokens)
        {
            _performance.PromptTokens = promptTokens;
            _performance.CompletionTokens = completionTokens;
            return this;
        }

        /// <summary>
        /// Sets estimated cost.
        /// </summary>
        public TestResultBuilder WithCost(decimal cost)
        {
            _performance.EstimatedCost = cost;
            return this;
        }

        /// <summary>
        /// Builds and records the result.
        /// </summary>
        /// <returns>The built TestResult.</returns>
        public TestResult Build()
        {
            _performance.EndTime = DateTimeOffset.UtcNow;
            _testBase.RecordResult(_result);
            return _result;
        }
    }
}
