// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentEval.Guardrails;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Fail-closed run-pre gate that authorizes the current operator and immutably binds the first admitted operator
/// to the session. A later change to another operator — even another allowed operator — is blocked.
/// </summary>
/// <remarks>
/// <para>
/// The caller supplies the authenticated operator under <see cref="OperatorAuthGate.OperatorMetadataKey"/>.
/// This gate owns the allow-list as well as the binding so an unauthorized operator can never establish a
/// baseline, including under observe-only execution. Use it instead of <see cref="OperatorAuthGate"/> when one
/// logical conversation must not change actors silently.
/// </para>
/// <para>
/// By default the binding follows the live <see cref="AgentSession"/> object and is garbage-collected with it.
/// Supply a stable logical-session resolver — directly or through <see cref="GatekeeperOptions.SessionIdentity"/>
/// — to preserve the binding across persisted-session reloads in this process. The stable session id must be
/// independent of the operator identity; using an actor id as the session id defeats cross-reload drift detection.
/// Only a SHA-256 digest of the bounded session id is retained.
/// </para>
/// <para>
/// Durable bindings are process-local and construction-capped. At capacity, a previously unseen session fails
/// closed rather than evicting security state. Cross-process deployments need an authoritative shared binding
/// store; this gate does not claim to provide one.
/// </para>
/// <para><b>Experimental:</b> this gate has no real-world drift corpus yet. Its exact API may change.</para>
/// </remarks>
[Experimental("AGENTEVAL_GATEKEEPER_PREVIEW001")]
public sealed class SessionIdentityDriftGate :
    SessionContextGate,
    ISessionIdentityAware,
    IConfigurationFingerprintContributor
{
    private const int DefaultMaxTrackedSessions = 10_000;
    private const int MaximumAllowedOperators = 1_024;
    private const int MaximumOperatorLength = 256;
    private const int MaximumSessionIdentityLength = 1_024;
    private const int MaximumTrackedSessions = 1_000_000;

    private readonly HashSet<string> _allowedOperators;
    private readonly ConditionalWeakTable<AgentSession, ObjectBinding> _objectBindings = new();
    private readonly Dictionary<string, string> _durableBindings = new(StringComparer.Ordinal);
    private readonly object _durableLock = new();
    private readonly int _maxTrackedSessions;
    private Func<AgentSession, string?>? _sessionKeySelector;
    private string _resolverSource;

    /// <inheritdoc/>
    public override string PolicyName => "SessionIdentityDriftGate";

    /// <summary>
    /// Stable, secret-free contribution used by Gatekeeper run receipts. It covers the normalized allow-list and
    /// durable-state cap and resolver source mode; runtime operator and session identities are never included.
    /// </summary>
    public string ConfigurationFingerprint { get; private set; }

    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => ConfigurationFingerprint;

    /// <summary>
    /// Creates an authorization-plus-binding gate.
    /// </summary>
    /// <param name="allowedOperators">
    /// The bounded set of authenticated operator identities that may establish or reuse a binding.
    /// </param>
    /// <param name="maxTrackedSessions">
    /// Maximum process-local durable-session bindings. Existing bindings continue at capacity; a new one blocks.
    /// </param>
    /// <param name="sessionKeySelector">
    /// Optional stable logical-session resolver. An explicit resolver wins over the shared Gatekeeper default.
    /// </param>
    public SessionIdentityDriftGate(
        IEnumerable<string> allowedOperators,
        int maxTrackedSessions = DefaultMaxTrackedSessions,
        Func<AgentSession, string?>? sessionKeySelector = null)
    {
        ArgumentNullException.ThrowIfNull(allowedOperators);
        if (maxTrackedSessions is < 1 or > MaximumTrackedSessions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTrackedSessions),
                $"maxTrackedSessions must be between 1 and {MaximumTrackedSessions}.");
        }

        _allowedOperators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operatorIdentity in allowedOperators)
        {
            if (_allowedOperators.Count >= MaximumAllowedOperators)
            {
                throw new ArgumentException(
                    $"allowedOperators must contain at most {MaximumAllowedOperators} distinct identities.",
                    nameof(allowedOperators));
            }

            var normalized = NormalizeConfiguredIdentity(
                operatorIdentity,
                nameof(allowedOperators),
                MaximumOperatorLength);
            if (!_allowedOperators.Add(normalized))
            {
                throw new ArgumentException(
                    "allowedOperators contains duplicate identities after canonical normalization.",
                    nameof(allowedOperators));
            }
        }

        if (_allowedOperators.Count == 0)
        {
            throw new ArgumentException(
                "allowedOperators must contain at least one identity.",
                nameof(allowedOperators));
        }

        _maxTrackedSessions = maxTrackedSessions;
        _sessionKeySelector = sessionKeySelector;
        _resolverSource = sessionKeySelector is null
            ? "object"
            : "explicit";
        ConfigurationFingerprint = ComputeConfigurationFingerprint(
            _allowedOperators,
            maxTrackedSessions,
            _resolverSource);
    }

    /// <inheritdoc/>
    void ISessionIdentityAware.UseSessionIdentityDefault(
        Func<AgentSession, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (_sessionKeySelector is not null)
        {
            return;
        }

        _sessionKeySelector = resolver;
        _resolverSource = "shared";
        ConfigurationFingerprint = ComputeConfigurationFingerprint(
            _allowedOperators,
            _maxTrackedSessions,
            _resolverSource);
    }

    /// <inheritdoc/>
    protected override ValueTask<GateVerdict> CheckSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryReadOperator(session, out var currentOperator, out var operatorFailure))
        {
            return Block(operatorFailure);
        }

        if (!_allowedOperators.Contains(currentOperator))
        {
            return Block("unauthorized_operator");
        }

        if (!TryResolveSessionIdentity(
                session,
                out var durableSessionDigest,
                out var resolverFailure))
        {
            return Block(resolverFailure);
        }

        var objectBinding = _objectBindings.GetOrCreateValue(session);
        lock (objectBinding)
        {
            if (!objectBinding.HasResolution)
            {
                objectBinding.DurableSessionDigest =
                    durableSessionDigest;
                objectBinding.HasResolution = true;
            }
            else if (!string.Equals(
                         objectBinding.DurableSessionDigest,
                         durableSessionDigest,
                         StringComparison.Ordinal))
            {
                return Block("session_identity_changed");
            }

            if (durableSessionDigest is null)
            {
                return CompareOrBindObject(
                    objectBinding,
                    currentOperator);
            }

            return CompareOrBindDurable(
                durableSessionDigest,
                currentOperator);
        }
    }

    private ValueTask<GateVerdict> CompareOrBindDurable(
        string durableSessionDigest,
        string currentOperator)
    {
        lock (_durableLock)
        {
            if (_durableBindings.TryGetValue(
                    durableSessionDigest,
                    out var baseline))
            {
                return string.Equals(
                    baseline,
                    currentOperator,
                    StringComparison.Ordinal)
                    ? Allow()
                    : Block("operator_changed");
            }

            if (_durableBindings.Count >= _maxTrackedSessions)
            {
                return Block("capacity_exhausted");
            }

            _durableBindings.Add(
                durableSessionDigest,
                currentOperator);
            return Allow();
        }
    }

    private ValueTask<GateVerdict> CompareOrBindObject(
        ObjectBinding binding,
        string currentOperator)
    {
        if (binding.Operator is null)
        {
            binding.Operator = currentOperator;
            return Allow();
        }

        return string.Equals(
            binding.Operator,
            currentOperator,
            StringComparison.Ordinal)
            ? Allow()
            : Block("operator_changed");
    }

    private bool TryResolveSessionIdentity(
        AgentSession session,
        out string? digest,
        out string failure)
    {
        digest = null;
        failure = string.Empty;
        if (_sessionKeySelector is null)
        {
            return true;
        }

        string? rawIdentity;
        try
        {
            rawIdentity = _sessionKeySelector(session);
        }
        catch
        {
            failure = "session_identity_unavailable";
            return false;
        }

        if (string.IsNullOrEmpty(rawIdentity))
        {
            return true;
        }

        if (!TryNormalizeIdentity(
                rawIdentity,
                MaximumSessionIdentityLength,
                out var normalized))
        {
            failure = "malformed_session_identity";
            return false;
        }

        digest = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    "session-identity-drift\0" +
                    normalized)));
        return true;
    }

    private static bool TryReadOperator(
        AgentSession session,
        out string currentOperator,
        out string failure)
    {
        currentOperator = string.Empty;
        failure = string.Empty;
        string? rawOperator;
        try
        {
            if (!session.StateBag.TryGetValue<string>(
                    OperatorAuthGate.OperatorMetadataKey,
                    out rawOperator,
                    JsonSerializerOptions.Default))
            {
                failure = "missing_operator";
                return false;
            }
        }
        catch
        {
            failure = "operator_identity_unavailable";
            return false;
        }

        if (!TryNormalizeIdentity(
                rawOperator,
                MaximumOperatorLength,
                out currentOperator))
        {
            failure = "malformed_operator";
            return false;
        }

        return true;
    }

    private static string NormalizeConfiguredIdentity(
        string? value,
        string parameterName,
        int maximumLength)
    {
        if (!TryNormalizeIdentity(
                value,
                maximumLength,
                out var normalized))
        {
            throw new ArgumentException(
                "Identity must normalize to visible, well-formed UTF-16 within the configured bound; the rejected value is omitted.",
                parameterName);
        }

        return normalized;
    }

    private static bool TryNormalizeIdentity(
        string? value,
        int maximumLength,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !IsWellFormedUtf16(value))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = value.Trim().Normalize(
                NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (candidate.Length is < 1 ||
            candidate.Length > maximumLength ||
            candidate.Any(IsForbiddenIdentityCharacter))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    private static bool IsForbiddenIdentityCharacter(char character)
        => char.IsControl(character) ||
           CharUnicodeInfo.GetUnicodeCategory(character) is
               UnicodeCategory.Format or
               UnicodeCategory.LineSeparator or
               UnicodeCategory.ParagraphSeparator;

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(value[index]) ||
                index + 1 >= value.Length ||
                !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private static string ComputeConfigurationFingerprint(
        IEnumerable<string> allowedOperators,
        int maxTrackedSessions,
        string resolverSource)
    {
        var canonical = new StringBuilder()
            .Append("maxTrackedSessions=")
            .Append(
                maxTrackedSessions.ToString(
                    CultureInfo.InvariantCulture))
            .Append('\n')
            .Append("resolverSource=")
            .Append(resolverSource)
            .Append('\n');
        foreach (var operatorIdentity in allowedOperators.Order(
                     StringComparer.Ordinal))
        {
            canonical
                .Append(operatorIdentity.Length.ToString(
                    CultureInfo.InvariantCulture))
                .Append(':')
                .Append(operatorIdentity)
                .Append('\n');
        }

        return ManifestFingerprint.Hash(
            canonical.ToString());
    }

    private ValueTask<GateVerdict> Allow()
        => new(GateVerdict.Allow(PolicyName));

    private ValueTask<GateVerdict> Block(string reasonCode)
        => new(
            GateVerdict.Block(
                PolicyName,
                $"session_identity_drift:{reasonCode}"));

    private sealed class ObjectBinding
    {
        public bool HasResolution;
        public string? DurableSessionDigest;
        public string? Operator;
    }
}
