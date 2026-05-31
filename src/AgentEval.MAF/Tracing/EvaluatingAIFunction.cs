// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Tracing;

/// <summary>
/// Wraps an <see cref="AIFunction"/> to record its execution (start, duration, args, result, exception)
/// into an <see cref="AgentTrace"/> as a <see cref="TraceEntryScope.ToolExecution"/> entry, correlated to
/// the parent chat turn via <see cref="ToolCorrelationScope"/>. Closes the one gap that <see cref="IChatClient"/>
/// middleware structurally cannot reach: the actual tool invocation (timing/result/exception).
/// </summary>
/// <remarks>
/// Lives in <c>AgentEval.MAF</c> (tool wiring is MAF's surface) but reads
/// <see cref="ToolCorrelationScope.Current"/> from <c>AgentEval.Core</c> (MAF references Core). The
/// correlation id is established by the caller once per invocation, so it flows here regardless of where
/// the recorder sits in the chat pipeline.
/// </remarks>
public sealed class EvaluatingAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly AgentTrace _trace;

    /// <summary>Wraps <paramref name="inner"/>, recording each invocation into <paramref name="trace"/>.</summary>
    public EvaluatingAIFunction(AIFunction inner, AgentTrace trace)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
    }

    /// <inheritdoc />
    public override string Name => _inner.Name;

    /// <inheritdoc />
    public override string Description => _inner.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _inner.JsonSchema;

    /// <inheritdoc />
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;

    /// <inheritdoc />
    public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // Settled (see plan §1.2 / C14): call the PUBLIC InvokeAsync, NOT the protected InvokeCoreAsync —
        // C# CS1540 forbids accessing a protected member through a base-typed (_inner : AIFunction) reference
        // from a sibling derived class. InvokeAsync routes to the inner's InvokeCoreAsync, so behaviour is identical.
        var index = _trace.Entries.Count;
        var correlationId = ToolCorrelationScope.Current;
        var argsJson = SafeSerialize(arguments);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            _trace.AddEntry(TraceEntry.ForToolExecution(
                index, correlationId, _inner.Name, argsJson, SafeSerialize(result), sw.ElapsedMilliseconds, succeeded: true, error: null));
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _trace.AddEntry(TraceEntry.ForToolExecution(
                index, correlationId, _inner.Name, argsJson, result: null, sw.ElapsedMilliseconds, succeeded: false, error: ex.Message));
            throw;
        }
    }

    private static string? SafeSerialize(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return value.ToString();
        }
        catch (JsonException)
        {
            return value.ToString();
        }
    }
}
