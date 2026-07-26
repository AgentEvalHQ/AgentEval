// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Gates;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Closed, data-only base for predicates understood by <see cref="ToolUsageContractGate"/>. The
/// <see langword="private protected"/> constructor prevents external assemblies from adding executable or
/// unrecognized predicate implementations.
/// </summary>
public abstract class ContractPredicate
{
    private protected ContractPredicate(string? argument)
    {
        Argument = argument is null ? null : ContractValidation.RequiredName(argument, nameof(argument));
    }

    /// <summary>The exact, case-sensitive tool argument name inspected by this predicate.</summary>
    public string? Argument { get; }
}

/// <summary>Requires the named argument's bounded projections to contain no recognized PII.</summary>
public sealed class PiiPredicate : ContractPredicate
{
    /// <summary>Creates a PII predicate for <paramref name="argument"/>.</summary>
    public PiiPredicate(string argument) : base(argument) { }
}

/// <summary>Shell syntax dialect whose metacharacters are denied.</summary>
public enum ShellDialect
{
    /// <summary>PowerShell syntax.</summary>
    PowerShell,
    /// <summary>POSIX-compatible sh syntax.</summary>
    PosixSh,
    /// <summary>Windows cmd.exe syntax.</summary>
    Cmd,
}

/// <summary>
/// Denies dialect-specific shell syntax metacharacters. This is not a command allow-list or semantic safety proof.
/// </summary>
public sealed class ShellMetacharDenyPredicate : ContractPredicate
{
    /// <summary>Creates a shell-metacharacter predicate for <paramref name="argument"/>.</summary>
    public ShellMetacharDenyPredicate(string argument, ShellDialect dialect) : base(argument)
    {
        if (!Enum.IsDefined(dialect))
        {
            throw new ArgumentOutOfRangeException(nameof(dialect));
        }

        Dialect = dialect;
    }

    /// <summary>The exact shell syntax table applied to the argument.</summary>
    public ShellDialect Dialect { get; }
}

/// <summary>
/// Blocks this contract's tool after any configured trigger proposal reached the same gate earlier in the run.
/// Observation is conservative proposal history, not proof of tool execution. Concurrent calls from one model batch
/// have no deterministic happens-before here; compose <see cref="SameBatchOrderingGate"/> when that guarantee is needed.
/// </summary>
public sealed class ForbiddenIfPrecededByPredicate : ContractPredicate
{
    internal const int MaxTriggerTools = 256;
    internal const int MaxTriggerToolChars = 256;

    /// <summary>Creates a sequence predicate from the trigger tool names.</summary>
    public ForbiddenIfPrecededByPredicate(IEnumerable<string> triggerTools) : base(argument: null)
    {
        ArgumentNullException.ThrowIfNull(triggerTools);

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputCount = 0;
        foreach (var triggerTool in triggerTools)
        {
            inputCount++;
            if (inputCount > MaxTriggerTools)
            {
                throw new ArgumentException($"at most {MaxTriggerTools} trigger tools may be configured.", nameof(triggerTools));
            }

            var normalizedTool = ContractValidation.RequiredName(triggerTool, nameof(triggerTools));
            if (normalizedTool.Length > MaxTriggerToolChars)
            {
                throw new ArgumentException(
                    $"trigger tool names may contain at most {MaxTriggerToolChars} characters.",
                    nameof(triggerTools));
            }

            normalized.Add(normalizedTool);
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("at least one trigger tool is required.", nameof(triggerTools));
        }

        TriggerTools = new ReadOnlyCollection<string>(normalized
            .OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tool => tool, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>Case-insensitively matched trigger tool names.</summary>
    public IReadOnlyList<string> TriggerTools { get; }
}

/// <summary>Allows recipient mailboxes only when every normalized domain is on the configured allow-list.</summary>
public sealed class RecipientDomainAllowListPredicate : ContractPredicate
{
    /// <summary>Creates a recipient-domain predicate for <paramref name="argument"/>.</summary>
    public RecipientDomainAllowListPredicate(string argument, IEnumerable<string> allowedDomains) : base(argument)
    {
        AllowedDomains = RecipientDomainPolicy.NormalizeAllowedDomains(allowedDomains, nameof(allowedDomains));
        AllowedDomainSet = new HashSet<string>(AllowedDomains, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Normalized ASCII domains; each entry also allows its subdomains.</summary>
    public IReadOnlyList<string> AllowedDomains { get; }

    internal IReadOnlySet<string> AllowedDomainSet { get; }
}

/// <summary>Rejects literal denied keywords in the named argument's bounded decoded projections.</summary>
public sealed class DeniedKeywordsPredicate : ContractPredicate
{
    /// <summary>Creates a literal, case-insensitive denied-keyword predicate.</summary>
    public DeniedKeywordsPredicate(string argument, IEnumerable<string> keywords) : base(argument)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in keywords)
        {
            if (keyword is null)
            {
                throw new ArgumentException("keywords cannot contain null.", nameof(keywords));
            }

            string value;
            try
            {
                value = keyword.Normalize(NormalizationForm.FormKC).Trim();
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException("keywords must contain valid Unicode text.", nameof(keywords), exception);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("keywords cannot contain an empty or whitespace-only entry.", nameof(keywords));
            }

            normalized.Add(value);
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("at least one denied keyword is required.", nameof(keywords));
        }

        Keywords = new ReadOnlyCollection<string>(normalized
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>Normalized, non-empty, case-insensitively deduplicated literal keywords.</summary>
    public IReadOnlyList<string> Keywords { get; }
}

/// <summary>An immutable ordered predicate list for one uniquely named tool.</summary>
public sealed class ToolContract
{
    /// <summary>Creates and validates a tool contract.</summary>
    public ToolContract(string toolName, IEnumerable<ContractPredicate> predicates)
    {
        ToolName = ContractValidation.RequiredName(toolName, nameof(toolName));
        ArgumentNullException.ThrowIfNull(predicates);

        var frozen = predicates.ToArray();
        if (frozen.Length == 0)
        {
            throw new ArgumentException("a tool contract must contain at least one predicate.", nameof(predicates));
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var predicate in frozen)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            var kind = ContractPredicateMetadata.Kind(predicate);
            var identity = kind + "\0" + predicate.Argument;
            if (!identities.Add(identity))
            {
                throw new ArgumentException(
                    $"duplicate predicate '{kind}' for argument '{predicate.Argument}'.",
                    nameof(predicates));
            }
        }

        Predicates = new ReadOnlyCollection<ContractPredicate>(frozen);
    }

    /// <summary>The tool name, matched with <see cref="StringComparer.OrdinalIgnoreCase"/>.</summary>
    public string ToolName { get; }

    /// <summary>Predicates evaluated in declaration order; first violation blocks.</summary>
    public IReadOnlyList<ContractPredicate> Predicates { get; }
}

/// <summary>Fluent, atomic builder used by <see cref="GatekeeperOptions.Contract"/>.</summary>
public sealed class ToolContractBuilder
{
    private readonly string _toolName;
    private readonly List<ContractPredicate> _predicates = new();

    internal ToolContractBuilder(string toolName)
    {
        _toolName = ContractValidation.RequiredName(toolName, nameof(toolName));
    }

    /// <summary>Adds a fail-closed PII scan for an exact argument name.</summary>
    public ToolContractBuilder Pii(string argument)
    {
        _predicates.Add(new PiiPredicate(argument));
        return this;
    }

    /// <summary>Adds a fail-closed mailbox-domain allow-list for the named recipient argument.</summary>
    public ToolContractBuilder RecipientDomains(string argument, params string[] allowedDomains)
    {
        _predicates.Add(new RecipientDomainAllowListPredicate(argument, allowedDomains));
        return this;
    }

    /// <summary>Adds dialect-specific shell metacharacter denial for the named string argument.</summary>
    public ToolContractBuilder ShellMetacharDeny(string argument, ShellDialect dialect)
    {
        _predicates.Add(new ShellMetacharDenyPredicate(argument, dialect));
        return this;
    }

    /// <summary>Adds a run-scoped prior-trigger prohibition for this contract's tool.</summary>
    public ToolContractBuilder ForbiddenIfPrecededBy(params string[] triggerTools)
    {
        _predicates.Add(new ForbiddenIfPrecededByPredicate(triggerTools));
        return this;
    }

    /// <summary>Adds literal denied keywords for an exact argument name.</summary>
    public ToolContractBuilder DeniedKeywords(string argument, params string[] words)
    {
        _predicates.Add(new DeniedKeywordsPredicate(argument, words));
        return this;
    }

    internal ToolContract Build() => new(_toolName, _predicates);
}

/// <summary>
/// One immutable, declarative per-tool contract gate. Tool names match ordinal-ignore-case; argument names match
/// exact ordinal spelling. Missing, null, malformed, over-limit, or inconclusive values fail closed.
/// </summary>
public sealed class ToolUsageContractGate : IToolGate, IConfigurationFingerprintContributor
{
    private const int MaxSerializedArgumentBytes = ArgumentCanonicalizer.DefaultMaxLength;
    private static readonly JsonSerializerOptions ProjectionJsonOptions = new() { MaxDepth = 32 };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly IReadOnlyList<ToolContract> _contracts;
    private readonly IReadOnlyDictionary<string, ToolContract> _contractsByTool;

    private readonly HashSet<string> _sequenceTriggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<object, SequenceRunState> _sequenceStates = new();
    private readonly object _sequenceFallbackKey = new();

    /// <summary>Creates a frozen contract gate. Tool names must be unique ordinal-ignore-case.</summary>
    public ToolUsageContractGate(IEnumerable<ToolContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);

        var frozen = contracts.ToArray();
        if (frozen.Length == 0)
        {
            throw new ArgumentException("at least one tool contract is required.", nameof(contracts));
        }

        var byTool = new Dictionary<string, ToolContract>(StringComparer.OrdinalIgnoreCase);
        var cost = GateCost.PureCode;
        var requirements = GateRequirements.None;
        foreach (var contract in frozen)
        {
            ArgumentNullException.ThrowIfNull(contract);
            if (!byTool.TryAdd(contract.ToolName, contract))
            {
                throw new ArgumentException(
                    $"duplicate tool contract '{contract.ToolName}' (tool names are case-insensitive).",
                    nameof(contracts));
            }

            foreach (var predicate in contract.Predicates)
            {
                cost = (GateCost)Math.Max((int)cost, (int)ContractPredicateMetadata.Cost(predicate));
                requirements |= ContractPredicateMetadata.Requirements(predicate);
                if (predicate is ForbiddenIfPrecededByPredicate sequence)
                {
                    foreach (var triggerTool in sequence.TriggerTools)
                    {
                        _sequenceTriggers.Add(triggerTool);
                    }
                }
            }
        }

        _contracts = new ReadOnlyCollection<ToolContract>(frozen);
        _contractsByTool = new ReadOnlyDictionary<string, ToolContract>(byTool);
        Cost = cost;
        Requirements = requirements;
        ConfigurationFingerprint = ComputeConfigurationFingerprint(frozen);
    }

    /// <inheritdoc />
    public string PolicyName => "ToolUsageContractGate";

    /// <inheritdoc />
    public GateCost Cost { get; }

    /// <inheritdoc />
    public GateRequirements Requirements { get; }

    /// <summary>SHA-256 fingerprint of the normalized, secret-free contract configuration.</summary>
    public string ConfigurationFingerprint { get; }

    /// <summary>The gate's immutable tool contracts.</summary>
    public IReadOnlyList<ToolContract> Contracts => _contracts;

    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution => ConfigurationFingerprint;

    /// <inheritdoc />
    public ValueTask<ToolGateVerdict> InspectAsync(
        GatedToolCall call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        _contractsByTool.TryGetValue(call.FunctionName, out var contract);
        if (_sequenceTriggers.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var sequenceViolation = ObserveSequence(call.FunctionName, contract);
        if (contract is null)
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }

        foreach (var predicate in contract.Predicates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate is ForbiddenIfPrecededByPredicate sequence)
            {
                if (ReferenceEquals(sequence, sequenceViolation))
                {
                    return Block(contract, predicate);
                }

                continue;
            }

            if (!TryReadArgument(call.Arguments, predicate.Argument!, out var value))
            {
                return Block(contract, predicate);
            }

            var clean = predicate switch
            {
                ShellMetacharDenyPredicate shell =>
                    ShellMetacharPolicy.IsSafe(value, shell.Dialect),
                RecipientDomainAllowListPredicate recipient =>
                    RecipientDomainPolicy.AllowsAll(value, recipient.AllowedDomainSet),
                _ => InspectBoundedProjections(predicate, value),
            };

            if (!clean)
            {
                return Block(contract, predicate);
            }
        }

        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
    }

    private ForbiddenIfPrecededByPredicate? ObserveSequence(string toolName, ToolContract? contract)
    {
        if (_sequenceTriggers.Count == 0)
        {
            return null;
        }

        var key = (object?)AgentRunScope.Current ?? _sequenceFallbackKey;
        var state = _sequenceStates.GetValue(key, static _ => new SequenceRunState());
        lock (state)
        {
            var violation = contract?.Predicates
                .OfType<ForbiddenIfPrecededByPredicate>()
                .FirstOrDefault(predicate => predicate.TriggerTools.Any(state.SeenTriggers.Contains));

            if (_sequenceTriggers.Contains(toolName))
            {
                state.SeenTriggers.Add(toolName);
            }

            return violation;
        }
    }

    private static bool InspectBoundedProjections(ContractPredicate predicate, object value)
    {
        if (!TryCreateProjection(value, out var rawProjection))
        {
            return false;
        }

        var canonical = ArgumentCanonicalizer.CanonicalizeWithStatus(rawProjection);
        if (!canonical.IsComplete || canonical.Projections.Count == 0)
        {
            return false;
        }

        return predicate switch
        {
            PiiPredicate => EveryProjectionHasNoPii(canonical.Projections),
            DeniedKeywordsPredicate denied => EveryProjectionOmitsKeywords(canonical.Projections, denied.Keywords),
            _ => false,
        };
    }

    private ValueTask<ToolGateVerdict> Block(ToolContract contract, ContractPredicate predicate)
    {
        var subject = predicate.Argument is null
            ? $"Tool '{contract.ToolName}' violated predicate "
            : $"Tool '{contract.ToolName}' argument '{predicate.Argument}' violated predicate ";
        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(
            PolicyName,
            subject + $"'{ContractPredicateMetadata.Kind(predicate)}'."));
    }

    private static bool TryReadArgument(
        IReadOnlyDictionary<string, object?>? arguments,
        string name,
        out object value)
    {
        if (arguments is not null)
        {
            foreach (var pair in arguments)
            {
                if (string.Equals(pair.Key, name, StringComparison.Ordinal) && pair.Value is not null)
                {
                    value = pair.Value;
                    return true;
                }
            }
        }

        value = null!;
        return false;
    }

    private static bool TryCreateProjection(object value, out string projection)
    {
        if (value is string text)
        {
            if (text.Length <= ArgumentCanonicalizer.DefaultMaxLength)
            {
                projection = text;
                return true;
            }

            projection = string.Empty;
            return false;
        }

        try
        {
            using var stream = new BoundedWriteStream(MaxSerializedArgumentBytes);
            JsonSerializer.Serialize(stream, value, value.GetType(), ProjectionJsonOptions);
            projection = StrictUtf8.GetString(stream.WrittenSpan);
            return true;
        }
        catch (Exception exception) when
            (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            projection = string.Empty;
            return false;
        }
    }

    private static bool EveryProjectionHasNoPii(IReadOnlyList<string> projections)
    {
        foreach (var projection in projections)
        {
            if (PiiScanner.Scan(projection).Status != PiiScanStatus.Clean)
            {
                return false;
            }
        }

        return true;
    }

    private static bool EveryProjectionOmitsKeywords(
        IReadOnlyList<string> projections,
        IReadOnlyList<string> keywords)
    {
        foreach (var projection in projections)
        {
            string normalized;
            try
            {
                normalized = projection.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                return false;
            }

            foreach (var keyword in keywords)
            {
                if (normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string ComputeConfigurationFingerprint(IEnumerable<ToolContract> contracts)
    {
        var canonical = new StringBuilder("gatekeeper.contract/1\n");
        foreach (var contract in contracts
            .OrderBy(item => item.ToolName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ToolName, StringComparer.Ordinal))
        {
            AppendCanonical(canonical, "tool", contract.ToolName.ToUpperInvariant());
            foreach (var predicate in contract.Predicates)
            {
                AppendCanonical(canonical, "kind", ContractPredicateMetadata.Kind(predicate));
                if (predicate.Argument is not null)
                {
                    AppendCanonical(canonical, "argument", predicate.Argument);
                }

                if (predicate is ForbiddenIfPrecededByPredicate sequence)
                {
                    foreach (var triggerTool in sequence.TriggerTools)
                    {
                        AppendCanonical(
                            canonical,
                            "triggerToolHash",
                            ManifestFingerprint.Hash(triggerTool.ToUpperInvariant()));
                    }
                }
                else if (predicate is DeniedKeywordsPredicate denied)
                {
                    foreach (var keyword in denied.Keywords)
                    {
                        AppendCanonical(canonical, "keywordHash", ManifestFingerprint.Hash(keyword.ToUpperInvariant()));
                    }
                }
                else if (predicate is ShellMetacharDenyPredicate shell)
                {
                    AppendCanonical(canonical, "dialect", shell.Dialect.ToString());
                }
                else if (predicate is RecipientDomainAllowListPredicate recipient)
                {
                    foreach (var domain in recipient.AllowedDomains)
                    {
                        AppendCanonical(canonical, "domainHash", ManifestFingerprint.Hash(domain));
                    }
                }
            }
        }

        return ManifestFingerprint.Hash(canonical.ToString());
    }

    private static void AppendCanonical(StringBuilder target, string name, string value)
        => target.Append(name).Append(':').Append(value.Length).Append(':').Append(value).Append('\n');

    private sealed class BoundedWriteStream(int maxBytes) : Stream
    {
        private readonly MemoryStream _inner = new(Math.Min(maxBytes, 4096));

        internal ReadOnlySpan<byte> WrittenSpan => _inner.GetBuffer().AsSpan(0, checked((int)_inner.Length));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_inner.Length + buffer.Length > maxBytes)
            {
                throw new BoundedProjectionException();
            }

            _inner.Write(buffer);
        }
    }

    private sealed class SequenceRunState
    {
        internal HashSet<string> SeenTriggers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BoundedProjectionException : Exception;
}

internal static class ContractPredicateMetadata
{
    internal static string Kind(ContractPredicate predicate) => predicate switch
    {
        PiiPredicate => "piiScan",
        ForbiddenIfPrecededByPredicate => "forbiddenIfPrecededBy",
        RecipientDomainAllowListPredicate => "recipientDomainAllowList",
        ShellMetacharDenyPredicate => "shellMetacharDeny",
        DeniedKeywordsPredicate => "deniedKeywords",
        _ => throw new ArgumentException("unrecognized contract predicate type.", nameof(predicate)),
    };

    internal static GateCost Cost(ContractPredicate predicate) => predicate switch
    {
        ForbiddenIfPrecededByPredicate => GateCost.PureCode,
        PiiPredicate or RecipientDomainAllowListPredicate or ShellMetacharDenyPredicate or DeniedKeywordsPredicate => GateCost.Bounded,
        _ => throw new ArgumentException("unrecognized contract predicate type.", nameof(predicate)),
    };

    internal static GateRequirements Requirements(ContractPredicate predicate) => predicate switch
    {
        PiiPredicate or RecipientDomainAllowListPredicate or ShellMetacharDenyPredicate or DeniedKeywordsPredicate => GateRequirements.None,
        ForbiddenIfPrecededByPredicate => GateRequirements.RunScope,
        _ => throw new ArgumentException("unrecognized contract predicate type.", nameof(predicate)),
    };
}

internal static class ContractValidation
{
    internal static string RequiredName(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("value must be non-empty.", parameterName);
        }

        return trimmed;
    }
}
