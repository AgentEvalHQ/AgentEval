// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>The kind of memory operation being evaluated.</summary>
public enum MemoryOperationKind
{
    Search,
    Recall,
    Write,
    Update,
    Delete,
    Promote,
    Reconcile,
    Audit,
}

/// <summary>The lifecycle stage at which a memory gate executes.</summary>
[Flags]
public enum MemoryGateStage
{
    None = 0,
    BeforeRead = 1 << 0,
    AfterRead = 1 << 1,
    BeforeWrite = 1 << 2,
    BeforePromotion = 1 << 3,
    BeforeAction = 1 << 4,
    AfterDecision = 1 << 5,
}

/// <summary>The integration surface carrying the memory operation.</summary>
public enum MemorySurface
{
    Tool,
    LocalMcp,
    HostedMcp,
    McpServer,
    AIContextProvider,
    ProviderNative,
}

/// <summary>The semantic category assigned to a memory candidate or recalled item.</summary>
public enum MemoryCategory
{
    Unknown,
    Fact,
    Preference,
    Summary,
    Procedure,
    ReasoningTrace,
    Message,
}

/// <summary>The kind of actor or artifact from which a memory originated.</summary>
public enum MemorySourceKind
{
    Unknown,
    User,
    Assistant,
    Tool,
    Mcp,
    ContextProvider,
    Application,
    Operator,
    Imported,
}

/// <summary>An ordered trust label; higher values represent stronger application-established trust.</summary>
public enum MemoryTrustLevel
{
    Untrusted = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    ApplicationTrusted = 4,
}

/// <summary>The proposed storage destination for a memory candidate.</summary>
public enum MemoryDestination
{
    Active,
    Quarantine,
    ProviderNative,
}

/// <summary>The explicit coverage level proven for one memory operation and surface.</summary>
public enum MemoryCoverageLevel
{
    Unsupported,
    ObserveOnly,
    ActionOnly,
    Boundary,
    FullLifecycle,
}
/// <summary>Whether adapters observe decisions or enforce them at the configured memory surface.</summary>
public enum MemorySecurityProfile
{
    Observe,
    Enforce,
}

/// <summary>Immutable policy identity and default behavior shared by one memory-gate pipeline.</summary>
public sealed class MemorySecurityPolicy
{
    public MemorySecurityPolicy(
        string policyId,
        string version,
        MemorySecurityProfile profile = MemorySecurityProfile.Enforce,
        MemoryGateAction ambiguousWriteAction = MemoryGateAction.Quarantine,
        MemoryCoverageLevel minimumCoverage = MemoryCoverageLevel.Boundary)
    {
        PolicyId = MemoryValidation.Identifier(policyId, nameof(policyId));
        Version = MemoryValidation.Identifier(version, nameof(version));
        Profile = MemoryValidation.Defined(profile, nameof(profile));
        AmbiguousWriteAction = MemoryValidation.Defined(ambiguousWriteAction, nameof(ambiguousWriteAction));
        MinimumCoverage = MemoryValidation.Defined(minimumCoverage, nameof(minimumCoverage));

        if (AmbiguousWriteAction is not (
                MemoryGateAction.Allow or
                MemoryGateAction.Quarantine or
                MemoryGateAction.RequireApproval or
                MemoryGateAction.Reject))
        {
            throw new ArgumentException(
                "Ambiguous writes may only be allowed, quarantined, approved, or rejected.",
                nameof(ambiguousWriteAction));
        }
    }

    public string PolicyId { get; }
    public string Version { get; }
    public MemorySecurityProfile Profile { get; }
    public MemoryGateAction AmbiguousWriteAction { get; }
    public MemoryCoverageLevel MinimumCoverage { get; }

    internal static MemorySecurityPolicy Default { get; } = new(
        "memory-default",
        "1",
        MemorySecurityProfile.Observe,
        MemoryGateAction.Quarantine,
        MemoryCoverageLevel.ObserveOnly);
}

/// <summary>Trusted host-resolved identity dimensions used to scope memory operations.</summary>
public sealed class MemorySecurityScope
{
    public MemorySecurityScope(
        string? tenantId = null,
        string? userId = null,
        string? agentId = null,
        string? applicationId = null,
        string? sessionId = null)
    {
        TenantId = MemoryValidation.OptionalIdentifier(tenantId, nameof(tenantId));
        UserId = MemoryValidation.OptionalIdentifier(userId, nameof(userId));
        AgentId = MemoryValidation.OptionalIdentifier(agentId, nameof(agentId));
        ApplicationId = MemoryValidation.OptionalIdentifier(applicationId, nameof(applicationId));
        SessionId = MemoryValidation.OptionalIdentifier(sessionId, nameof(sessionId));

        if (TenantId is null && UserId is null && AgentId is null && ApplicationId is null && SessionId is null)
        {
            throw new ArgumentException("At least one trusted memory scope dimension is required.");
        }
    }

    public string? TenantId { get; }
    public string? UserId { get; }
    public string? AgentId { get; }
    public string? ApplicationId { get; }
    public string? SessionId { get; }

    internal string ComputeCorrelation()
        => MemoryDigest.Compute(string.Join(
            "\u001f",
            TenantId ?? string.Empty,
            UserId ?? string.Empty,
            AgentId ?? string.Empty,
            ApplicationId ?? string.Empty,
            SessionId ?? string.Empty));
}

/// <summary>Content-free lineage, trust, and source metadata for a memory item.</summary>
public sealed class MemoryProvenance
{
    public MemoryProvenance(
        MemorySourceKind sourceKind,
        string sourceId,
        MemoryTrustLevel trust,
        string? rootLineageId = null,
        IEnumerable<string>? parentMemoryIds = null,
        IEnumerable<string>? transformations = null,
        IEnumerable<string>? citationIds = null)
    {
        SourceKind = MemoryValidation.Defined(sourceKind, nameof(sourceKind));
        SourceId = MemoryValidation.Identifier(sourceId, nameof(sourceId));
        Trust = MemoryValidation.Defined(trust, nameof(trust));
        RootLineageId = MemoryValidation.OptionalIdentifier(rootLineageId, nameof(rootLineageId)) ?? SourceId;
        ParentMemoryIds = MemoryValidation.IdentifierList(parentMemoryIds, nameof(parentMemoryIds), 32);
        Transformations = MemoryValidation.IdentifierList(transformations, nameof(transformations), 16);
        CitationIds = MemoryValidation.IdentifierList(citationIds, nameof(citationIds), 32);
    }

    public MemorySourceKind SourceKind { get; }
    public string SourceId { get; }
    public MemoryTrustLevel Trust { get; }
    public string RootLineageId { get; }
    public IReadOnlyList<string> ParentMemoryIds { get; }
    public IReadOnlyList<string> Transformations { get; }
    public IReadOnlyList<string> CitationIds { get; }
}

/// <summary>A bounded content-free summary of an existing conflicting memory.</summary>
public sealed class MemoryConflictCandidate
{
    public MemoryConflictCandidate(
        string memoryId,
        string contentDigest,
        MemoryTrustLevel trust,
        string rootLineageId,
        MemoryCategory category)
    {
        MemoryId = MemoryValidation.Identifier(memoryId, nameof(memoryId));
        ContentDigest = MemoryDigest.Validate(contentDigest, nameof(contentDigest));
        Trust = MemoryValidation.Defined(trust, nameof(trust));
        RootLineageId = MemoryValidation.Identifier(rootLineageId, nameof(rootLineageId));
        Category = MemoryValidation.Defined(category, nameof(category));
    }

    public string MemoryId { get; }
    public string ContentDigest { get; }
    public MemoryTrustLevel Trust { get; }
    public string RootLineageId { get; }
    public MemoryCategory Category { get; }
}

/// <summary>Explicit security semantics for one memory tool, MCP operation, or provider hook.</summary>
public sealed class MemoryOperationContract
{
    public MemoryOperationContract(
        string operationName,
        MemoryOperationKind kind,
        MemorySurface surface,
        IEnumerable<string>? contentArguments,
        IEnumerable<string>? scopeArguments,
        MemoryCategory category,
        bool isSideEffecting,
        bool mayReturnSensitiveContent)
    {
        OperationName = MemoryValidation.Identifier(operationName, nameof(operationName));
        Kind = MemoryValidation.Defined(kind, nameof(kind));
        Surface = MemoryValidation.Defined(surface, nameof(surface));
        ContentArguments = MemoryValidation.IdentifierList(contentArguments, nameof(contentArguments), 32);
        ScopeArguments = MemoryValidation.IdentifierList(scopeArguments, nameof(scopeArguments), 32);
        Category = MemoryValidation.Defined(category, nameof(category));
        IsSideEffecting = isSideEffecting;
        MayReturnSensitiveContent = mayReturnSensitiveContent;

        if (ContentArguments.Intersect(ScopeArguments, StringComparer.Ordinal).Any())
        {
            throw new ArgumentException("Content and scope argument names must not overlap.");
        }
    }

    public string OperationName { get; }
    public MemoryOperationKind Kind { get; }
    public MemorySurface Surface { get; }
    public IReadOnlyList<string> ContentArguments { get; }
    public IReadOnlyList<string> ScopeArguments { get; }
    public MemoryCategory Category { get; }
    public bool IsSideEffecting { get; }
    public bool MayReturnSensitiveContent { get; }
}

/// <summary>The immutable, bounded subject evaluated by memory gates.</summary>
public sealed class MemoryGateContext
{
    public const int MaximumContentCharacters = 65_536;

    public MemoryGateContext(
        string operationId,
        MemoryGateStage stage,
        MemoryOperationContract operation,
        string providerId,
        MemorySecurityScope? authenticatedScope,
        MemoryProvenance provenance,
        string? content = null,
        string? contentDigest = null,
        IReadOnlyDictionary<string, string?>? modelSuppliedScope = null,
        IEnumerable<MemoryConflictCandidate>? conflicts = null,
        string? runId = null,
        string? logicalSessionId = null,
        MemoryDestination destination = MemoryDestination.Active,
        MemoryRecordMetadata? recordMetadata = null,
        MemoryBudgetSnapshot? budget = null,
        bool hasAdministrativeCrossScopeCapability = false)
    {
        OperationId = MemoryValidation.Identifier(operationId, nameof(operationId));
        Stage = MemoryValidation.SingleStage(stage, nameof(stage));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        ProviderId = MemoryValidation.Identifier(providerId, nameof(providerId));
        AuthenticatedScope = authenticatedScope;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Content = MemoryValidation.OptionalContent(content, nameof(content));
        ContentDigest = ResolveDigest(Content, contentDigest);
        ModelSuppliedScope = MemoryValidation.ScopeDictionary(modelSuppliedScope);
        Conflicts = MemoryValidation.ConflictList(conflicts);
        RunId = MemoryValidation.OptionalIdentifier(runId, nameof(runId));
        LogicalSessionId = MemoryValidation.OptionalIdentifier(logicalSessionId, nameof(logicalSessionId));
        Destination = MemoryValidation.Defined(destination, nameof(destination));
        RecordMetadata = recordMetadata;
        Budget = budget;
        HasAdministrativeCrossScopeCapability = hasAdministrativeCrossScopeCapability;

        MemoryValidation.ValidateOperationStage(Operation.Kind, Stage);
    }

    public string OperationId { get; }
    public MemoryOperationKind Kind => Operation.Kind;
    public MemoryGateStage Stage { get; }
    public MemorySurface Surface => Operation.Surface;
    public MemoryOperationContract Operation { get; }
    public string ProviderId { get; }
    [JsonIgnore]
    public MemorySecurityScope? AuthenticatedScope { get; }
    [JsonIgnore]
    public MemoryProvenance Provenance { get; }
    [JsonIgnore]
    public string? Content { get; }
    public string ContentDigest { get; }
    [JsonIgnore]
    public IReadOnlyDictionary<string, string?> ModelSuppliedScope { get; }
    [JsonIgnore]
    public IReadOnlyList<MemoryConflictCandidate> Conflicts { get; }
    public string? RunId { get; }
    public string? LogicalSessionId { get; }
    public MemoryDestination Destination { get; }
    [JsonIgnore]
    public MemoryRecordMetadata? RecordMetadata { get; }
    public MemoryBudgetSnapshot? Budget { get; }
    public bool HasAdministrativeCrossScopeCapability { get; }

    public MemoryGateContext WithContent(string? content)
        => new(
            OperationId,
            Stage,
            Operation,
            ProviderId,
            AuthenticatedScope,
            Provenance,
            content,
            contentDigest: null,
            ModelSuppliedScope,
            Conflicts,
            RunId,
            LogicalSessionId,
            Destination);

    private static string ResolveDigest(string? content, string? suppliedDigest)
    {
        if (content is not null)
        {
            var computed = MemoryDigest.Compute(content);
            if (suppliedDigest is not null &&
                !string.Equals(computed, MemoryDigest.Validate(suppliedDigest, nameof(suppliedDigest)), StringComparison.Ordinal))
            {
                throw new ArgumentException("The supplied content digest does not match the bounded content.", nameof(suppliedDigest));
            }

            return computed;
        }

        return suppliedDigest is null
            ? MemoryDigest.Compute(string.Empty)
            : MemoryDigest.Validate(suppliedDigest, nameof(suppliedDigest));
    }
}

internal static class MemoryDigest
{
    internal static string Compute(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string Validate(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c)))
        {
            throw new ArgumentException("A SHA-256 digest must contain exactly 64 hexadecimal characters.", parameterName);
        }

        return value.ToLowerInvariant();
    }
}

internal static class MemoryValidation
{
    private const int MaximumIdentifierCharacters = 256;

    internal static string Identifier(string value, string parameterName)
        => OptionalIdentifier(value, parameterName)
            ?? throw new ArgumentException("A non-empty identifier is required.", parameterName);


    internal static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "A known enum value is required.");
        }

        return value;
    }
    internal static string? OptionalIdentifier(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaximumIdentifierCharacters || trimmed.Any(IsUnsafeIdentifierCharacter))
        {
            throw new ArgumentException(
                $"The identifier must contain 1 to {MaximumIdentifierCharacters} safe characters.",
                parameterName);
        }

        return trimmed;
    }

    internal static IReadOnlyList<string> IdentifierList(
        IEnumerable<string>? values,
        string parameterName,
        int maximumCount)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (result.Count == maximumCount)
            {
                throw new ArgumentException($"The collection exceeds its maximum count of {maximumCount}.", parameterName);
            }

            var validated = Identifier(value, parameterName);
            if (!seen.Add(validated))
            {
                throw new ArgumentException("The collection contains a duplicate identifier.", parameterName);
            }

            result.Add(validated);
        }

        return new ReadOnlyCollection<string>(result);
    }

    internal static MemoryGateStage SingleStage(MemoryGateStage value, string parameterName)
    {
        var numeric = (int)value;
        if (numeric == 0 || (numeric & (numeric - 1)) != 0 ||
            value is > MemoryGateStage.AfterDecision)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Exactly one known memory gate stage is required.");
        }

        return value;
    }

    internal static string? OptionalContent(string? content, string parameterName)
    {
        if (content is not null && content.Length > MemoryGateContext.MaximumContentCharacters)
        {
            throw new ArgumentException(
                $"Memory content exceeds the maximum of {MemoryGateContext.MaximumContentCharacters} characters.",
                parameterName);
        }

        return content;
    }

    internal static IReadOnlyDictionary<string, string?> ScopeDictionary(
        IReadOnlyDictionary<string, string?>? values)
    {
        if (values is null)
        {
            return new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(StringComparer.Ordinal));
        }

        if (values.Count > 16)
        {
            throw new ArgumentException("Model-supplied scope exceeds the maximum count of 16.", nameof(values));
        }

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in values)
        {
            var key = Identifier(entry.Key, nameof(values));
            var value = OptionalIdentifier(entry.Value, nameof(values));
            if (!result.TryAdd(key, value))
            {
                throw new ArgumentException("Model-supplied scope contains a duplicate key.", nameof(values));
            }
        }

        return new ReadOnlyDictionary<string, string?>(result);
    }

    internal static IReadOnlyList<MemoryConflictCandidate> ConflictList(
        IEnumerable<MemoryConflictCandidate>? values)
    {
        if (values is null)
        {
            return Array.Empty<MemoryConflictCandidate>();
        }

        var result = values.ToList();
        if (result.Count > 64 || result.Any(value => value is null))
        {
            throw new ArgumentException("Conflict candidates must contain at most 64 non-null entries.", nameof(values));
        }

        if (result.Select(value => value.MemoryId).Distinct(StringComparer.Ordinal).Count() != result.Count)
        {
            throw new ArgumentException("Conflict candidates contain duplicate memory identifiers.", nameof(values));
        }

        return new ReadOnlyCollection<MemoryConflictCandidate>(result);
    }

    internal static void ValidateOperationStage(MemoryOperationKind operation, MemoryGateStage stage)
    {
        var valid = operation switch
        {
            MemoryOperationKind.Search or MemoryOperationKind.Recall
                => stage is MemoryGateStage.BeforeRead or MemoryGateStage.AfterRead or MemoryGateStage.BeforeAction,
            MemoryOperationKind.Write or MemoryOperationKind.Update or MemoryOperationKind.Delete or MemoryOperationKind.Reconcile
                => stage is MemoryGateStage.BeforeWrite or MemoryGateStage.AfterDecision,
            MemoryOperationKind.Promote
                => stage is MemoryGateStage.BeforeWrite or MemoryGateStage.BeforePromotion or MemoryGateStage.AfterDecision,
            MemoryOperationKind.Audit
                => stage is MemoryGateStage.AfterDecision,
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException("The memory operation is not legal at the requested lifecycle stage.");
        }
    }

    internal static bool IsReasonCode(string value)
        => value.Length is > 0 and <= 128 &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static bool IsUnsafeIdentifierCharacter(char value)
        => char.IsControl(value) ||
           char.GetUnicodeCategory(value) is
               System.Globalization.UnicodeCategory.Format or
               System.Globalization.UnicodeCategory.LineSeparator or
               System.Globalization.UnicodeCategory.ParagraphSeparator;
}
