// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.PartnerDeskDemo.Demo;
using Microsoft.Extensions.AI;

namespace AgentEval.PartnerDeskDemo.Mcp;

/// <summary>
/// A presentation-only decorator over the real <c>McpClientTool</c>: it forwards the call unchanged and prints
/// what the third party sent back.
/// </summary>
/// <remarks>
/// <para>
/// The name, description, and JSON schema are the MCP server's own — <see cref="DelegatingAIFunction"/> forwards
/// them — and the invocation is the SDK's, so this changes nothing about the boundary. It exists because the tail
/// of the supplier's response is the most important thing in the room during Phase 2, and because seeing it arrive
/// and then seeing it withheld is what makes Phase 4 legible.
/// </para>
/// <para>
/// It prints the last few lines of the response verbatim, with no inspection of what they say. Deciding that a
/// response is hostile is a gate's job, at result admission, and this deliberately does not do it.
/// </para>
/// </remarks>
public sealed class NarratedMcpTool : DelegatingAIFunction
{
    private const int TailLines = 14;

    private readonly DemoOutput _output;
    private readonly bool _announceOnly;

    /// <summary>Wraps <paramref name="inner"/>, narrating each response to <paramref name="output"/>.</summary>
    /// <param name="inner">The real MCP client tool.</param>
    /// <param name="output">Where the narration goes.</param>
    /// <param name="announceOnly">
    /// When true, print only that a response arrived — not its body. Set this once a tool-result admission gate is
    /// installed (Level 2): the gate withholds the response from the model, and dumping the poisoned tail to the
    /// console would visually contradict "the addendum never reaches the model". With no result gate (Phases 2 and
    /// 3), the body is shown so the audience sees the injection arrive.
    /// </param>
    public NarratedMcpTool(AIFunction inner, DemoOutput output, bool announceOnly = false)
        : base(inner)
    {
        _output = output;
        _announceOnly = announceOnly;
    }

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        var text = Stringify(result);

        if (_announceOnly)
        {
            _output.ToolAllowed(
                Name,
                $"third-party response received, {DemoOutput.Number(text.Length)} characters — handing to result admission");
            return result;
        }

        _output.ToolAllowed(Name, $"third-party response received, {DemoOutput.Number(text.Length)} characters");

        var lines = text
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        _output.Line();
        _output.Line(ConsoleColor.Magenta, "     +--- PARTNERINTEL RESPONSE (tail, verbatim, uninspected) ------------------");
        foreach (var line in lines.TakeLast(TailLines))
        {
            _output.Line(ConsoleColor.Magenta, "     | " + Clip(line.TrimEnd(), 84));
        }

        _output.Line(ConsoleColor.Magenta, "     +---------------------------------------------------------------------------");
        _output.Line();

        return result;
    }

    private static string Stringify(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.ToString(),
        _ => result.ToString() ?? string.Empty,
    };

    private static string Clip(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "...";
}
