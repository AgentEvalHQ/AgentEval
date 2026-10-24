// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// An <see cref="IAgentEvalLogger"/> that sends every line through <see cref="Config.Redact(string?)"/> before the
/// console logger prints it.
/// </summary>
/// <remarks>
/// For the verbose <c>WorkflowEvaluationHarness</c> in Eval 02: its failure lines carry the raw exception text
/// (<c>"❌ Workflow execution failed: {message}"</c>, <c>"• Exception: {message}"</c>), which can name the provider's
/// host or model. Exceptions are never handed to the inner logger — it would print their message unredacted — but
/// formatted here through <see cref="Config.Redact(Exception?, bool)"/>.
/// </remarks>
internal sealed class RedactingAgentEvalLogger(IAgentEvalLogger inner) : IAgentEvalLogger
{
    public void Log(LogLevel level, string message) => inner.Log(level, Config.Redact(message));

    public void Log(LogLevel level, Exception exception, string message) =>
        inner.Log(level, $"{Config.Redact(message)} — {exception.GetType().Name}: {Config.Redact(exception)}");

    public void Log(LogLevel level, string message, params (string Key, object? Value)[] properties) =>
        inner.Log(level, Config.Redact($"{message} {{ {string.Join(", ", properties.Select(p => $"{p.Key}={p.Value}"))} }}"));

    public void LogMetricResult(MetricResult result) =>
        inner.Log(result.Passed ? LogLevel.Information : LogLevel.Warning,
                  Config.Redact(result.Measured
                      ? $"{(result.Passed ? "✓" : "✗")} {result.MetricName}: {result.Score:F2} - {result.Explanation}"
                      : $"? {result.MetricName}: not measured - {result.Explanation}"));

    public void LogFailure(FailureReport report) => inner.Log(LogLevel.Error, Config.Redact(report.ToFormattedReport()));

    public void LogTimeline(ToolCallTimeline timeline) => inner.Log(LogLevel.Information, Config.Redact(timeline.ToAsciiDiagram()));

    public bool IsEnabled(LogLevel level) => inner.IsEnabled(level);

    public IDisposable BeginScope(string scopeName, params (string Key, object? Value)[] properties) =>
        inner.BeginScope(Config.Redact(scopeName), [.. properties.Select(p => (p.Key, (object?)Config.Redact(p.Value?.ToString())))]);
}
