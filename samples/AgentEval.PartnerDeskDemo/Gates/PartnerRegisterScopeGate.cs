// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;

namespace AgentEval.PartnerDeskDemo.Gates;

/// <summary>
/// States what <c>query_partner_database</c> is <b>for</b>: looking up one named partner, never exporting the
/// register. A call must name a partner and must not ask for more than <see cref="MaxRows"/> rows.
/// </summary>
/// <remarks>
/// <para><b>Why this is a hand-written gate and not a <see cref="ToolUsageContractGate"/> predicate.</b>
/// The shipped contract predicates are <c>piiScan</c>, <c>recipientDomainAllowList</c>, <c>maxDistinctValues</c>,
/// <c>shellMetacharDeny</c>, <c>forbiddenIfPrecededBy</c>, <c>pathContainment</c>, and <c>deniedKeywords</c>
/// (see <c>docs/gatekeeper/tool-and-result-gates.md</c>). None of them expresses "this argument must be present
/// and non-empty" or "this numeric argument must not exceed N", so the register-scope rule cannot be written as a
/// declarative contract today. Rather than bend an unrelated predicate into the shape — or, worse, ship a wrapper
/// that looks like a contract and enforces nothing — this is a plain deterministic
/// <see cref="IToolGate"/>: the documented extension point, pure code, no model, no network, microseconds.
/// The sibling <c>send_email</c> rule <i>does</i> map exactly onto <c>recipientDomainAllowList</c> and is written
/// as a real contract; see <see cref="PartnerDeskGates"/>.</para>
/// <para>This is authorization, not detection. It does not decide whether the model was manipulated — it decides
/// whether this call is inside the capability the tool was granted for, and it keeps deciding that after any
/// upstream detector has allowed a paraphrase.</para>
/// </remarks>
public sealed class PartnerRegisterScopeGate : IToolGate
{
    private readonly string _toolName;
    private readonly string _nameArgument;
    private readonly string _limitArgument;

    /// <summary>Creates the gate over one tool's name and limit arguments.</summary>
    /// <param name="toolName">The exact tool this gate authorizes.</param>
    /// <param name="maxRows">The largest <paramref name="limitArgument"/> value a single call may ask for.</param>
    /// <param name="nameArgument">The argument that must name a single partner.</param>
    /// <param name="limitArgument">The argument that bounds how many rows come back.</param>
    public PartnerRegisterScopeGate(
        string toolName,
        int maxRows,
        string nameArgument = "partnerName",
        string limitArgument = "limit")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(nameArgument);
        ArgumentException.ThrowIfNullOrWhiteSpace(limitArgument);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);

        _toolName = toolName;
        _nameArgument = nameArgument;
        _limitArgument = limitArgument;
        MaxRows = maxRows;
    }

    /// <summary>The largest number of rows one authorized call may request.</summary>
    public int MaxRows { get; }

    /// <inheritdoc />
    public string PolicyName => "partner-register-scope";

    /// <inheritdoc />
    public GateCost Cost => GateCost.PureCode;

    /// <summary>
    /// Refusing is the whole purpose of this gate, so it may not be silently resolved down to observation-only.
    /// </summary>
    public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;

    /// <inheritdoc />
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!string.Equals(call.FunctionName, _toolName, StringComparison.Ordinal))
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }

        var arguments = call.Arguments;
        var partnerName = arguments is not null && arguments.TryGetValue(_nameArgument, out var raw)
            ? AsText(raw)
            : null;

        if (string.IsNullOrWhiteSpace(partnerName))
        {
            return Block(
                $"'{_toolName}' is authorized for single-partner lookup only: '{_nameArgument}' must name one " +
                "partner. A call with no partner name is a register export, which this tool is not for.");
        }

        if (arguments is not null && arguments.TryGetValue(_limitArgument, out var limitRaw))
        {
            var limit = AsInteger(limitRaw);
            if (limit is null)
            {
                // Cannot prove the request is inside the bound ⇒ fail closed.
                return Block(
                    $"'{_toolName}' supplied a '{_limitArgument}' this gate could not read as a number, so the " +
                    $"{MaxRows}-row bound could not be proven. Refusing rather than guessing.");
            }

            if (limit > MaxRows)
            {
                return Block(
                    $"'{_toolName}' is authorized for at most {MaxRows} rows per call; this call asked for " +
                    $"{limit.Value.ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
    }

    private ValueTask<ToolGateVerdict> Block(string reason) =>
        new(ToolGateVerdict.Block(PolicyName, reason));

    private static string? AsText(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.ToString(),
        _ => value.ToString(),
    };

    private static int? AsInteger(object? value) => value switch
    {
        null => null,
        int i => i,
        long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
        short s => s,
        double d when !double.IsNaN(d) && !double.IsInfinity(d) => (int)Math.Clamp(d, int.MinValue, int.MaxValue),
        float f when !float.IsNaN(f) && !float.IsInfinity(f) => (int)Math.Clamp(f, int.MinValue, int.MaxValue),
        decimal m => (int)Math.Clamp(m, int.MinValue, int.MaxValue),
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetDouble(out var parsedDouble) =>
            (int)Math.Clamp(parsedDouble, int.MinValue, int.MaxValue),
        _ => int.TryParse(
                 value.ToString(),
                 NumberStyles.Integer,
                 CultureInfo.InvariantCulture,
                 out var text)
             ? text
             : null,
    };
}
