// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Assertions;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Approval gate that escalates a tool call to human review when its serialized arguments match a pattern
/// (e.g. a high-value amount, an external destination, a sensitive keyword) — and auto-approves otherwise. It is
/// the approval-flow counterpart of <see cref="ArgumentPatternGate"/>: a match routes the call to a human rather
/// than blocking it. Reuses the ReDoS-safe, relaxed-encoding scanner so injection metacharacters are matched on
/// the surface the tool will actually receive.
/// <para><b>Fail-closed:</b> if the arguments cannot be serialized for inspection, the call is escalated (the
/// gate cannot prove it routine).</para>
/// </summary>
public sealed class ArgumentPatternApprovalGate : IToolApprovalGate
{
    // Relaxed encoder: default JSON escaping turns injection metacharacters (< > & ' +) into \uXXXX, so a pattern
    // like "' OR" would never match the escaped surface and the gate would auto-approve a borderline call.
    private static readonly JsonSerializerOptions ScanOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Regex _escalatePattern;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <summary>Creates the gate from a pattern, compiled with a bounded (100 ms) match timeout (ReDoS-safe).</summary>
    public ArgumentPatternApprovalGate(string escalateWhenArgumentsMatch, string? policyName = null)
        : this(new Regex(escalateWhenArgumentsMatch, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), policyName)
    {
    }

    /// <summary>Creates the gate from a caller-supplied <see cref="Regex"/> (must have a bounded MatchTimeout).</summary>
    public ArgumentPatternApprovalGate(Regex escalateWhenArgumentsMatch, string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(escalateWhenArgumentsMatch);
        if (escalateWhenArgumentsMatch.MatchTimeout == Regex.InfiniteMatchTimeout)
        {
            throw new ArgumentException(
                "escalateWhenArgumentsMatch must have a bounded MatchTimeout (ReDoS guard).", nameof(escalateWhenArgumentsMatch));
        }

        _escalatePattern = escalateWhenArgumentsMatch;
        PolicyName = policyName ?? "ArgumentPatternApprovalGate";
    }

    /// <inheritdoc/>
    public ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Arguments is null || call.Arguments.Count == 0)
        {
            return new ValueTask<bool>(true);   // nothing to inspect ⇒ auto-approve
        }

        string serialized;
        try
        {
            serialized = JsonSerializer.Serialize(call.Arguments, ScanOptions);
        }
        catch (NotSupportedException)
        {
            return new ValueTask<bool>(false);   // cannot inspect ⇒ escalate (fail-closed)
        }

        // A match means the call is borderline ⇒ escalate (NOT auto-approvable).
        var hit = ForbiddenPatternScanner.ScanForForbiddenPattern(serialized, _escalatePattern);
        return new ValueTask<bool>(!hit);
    }
}
