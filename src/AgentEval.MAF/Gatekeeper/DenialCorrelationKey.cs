// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Builds the bounded, domain-separated, SHA-256-only repeated-denial dimension.</summary>
internal static class DenialCorrelationKey
{
    internal const string Domain = "agenteval.gatekeeper.denial-correlation/v1";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments =
        new Dictionary<string, object?>();
    private static readonly byte[] InconclusiveArgumentsHash =
        SHA256.HashData(Encoding.ASCII.GetBytes(Domain + "/arguments-inconclusive"));

    internal static DenialCorrelation Create(
        GatedToolCall call,
        string policyName,
        string configurationFingerprint,
        Func<AgentSession, IReadOnlyList<ContainmentTarget>>? stableTargets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(policyName);
        ArgumentNullException.ThrowIfNull(configurationFingerprint);
        cancellationToken.ThrowIfCancellationRequested();

        var argumentsCanonical = ContractValueCanonicalizer.TryHash(
            call.Arguments ?? EmptyArguments,
            out var argumentsHash);
        if (!argumentsCanonical)
        {
            argumentsHash = InconclusiveArgumentsHash;
        }

        var scope = AgentRunScope.Current
            ?? throw new InvalidOperationException("Denial correlation requires an active run scope.");
        var tenant = "run";
        var session = scope.RunId;
        var identitySource = "run";

        if (stableTargets is not null
            && scope.Session is { } agentSession
            && ContainmentGateEvaluator.TryResolve(
                stableTargets,
                agentSession,
                requireAtLeastOne: true,
                cancellationToken,
                out var targets)
            && targets[0] is ContainmentTarget.Session stableSession)
        {
            tenant = stableSession.Tenant;
            session = stableSession.SessionId;
            identitySource = "stable_session";
        }

        using var accumulator = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendAscii(accumulator, Domain);
        AppendAscii(accumulator, "\n");
        AppendText(accumulator, "tenant", tenant);
        AppendText(accumulator, "session", session);
        AppendText(accumulator, "policy", policyName);
        AppendText(accumulator, "configuration", configurationFingerprint);
        AppendText(accumulator, "tool", call.FunctionName);
        AppendBytes(accumulator, "arguments", argumentsHash);
        AppendText(accumulator, "canonical", argumentsCanonical ? "true" : "false");

        var hash = accumulator.GetHashAndReset();
        return new DenialCorrelation(
            Convert.ToHexString(hash),
            hash,
            argumentsCanonical,
            identitySource);
    }

    private static void AppendText(IncrementalHash accumulator, string name, string value)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            bytes = Encoding.ASCII.GetBytes("<invalid-utf16>");
        }

        AppendBytes(accumulator, name, bytes);
    }

    private static void AppendBytes(IncrementalHash accumulator, string name, ReadOnlySpan<byte> value)
    {
        AppendAscii(accumulator, name);
        AppendAscii(accumulator, ":");
        AppendAscii(accumulator, value.Length.ToString(CultureInfo.InvariantCulture));
        AppendAscii(accumulator, ":");
        accumulator.AppendData(value);
        AppendAscii(accumulator, "\n");
    }

    private static void AppendAscii(IncrementalHash accumulator, string value)
        => accumulator.AppendData(Encoding.ASCII.GetBytes(value));
}

/// <summary>Secret-free correlation metadata emitted with an enforced tool denial.</summary>
internal readonly record struct DenialCorrelation(
    string Hash,
    ReadOnlyMemory<byte> HashBytes,
    bool ArgumentsCanonical,
    string IdentitySource);
