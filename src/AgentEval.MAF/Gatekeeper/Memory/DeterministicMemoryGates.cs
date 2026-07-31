// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>Fails closed when trusted host scope is missing, incomplete, or contradicted by model input.</summary>
public sealed class MemoryScopeIntegrityGate : IMemoryGate, IConfigurationFingerprintContributor
{
    private readonly MemoryScopeIntegrityOptions _options;

    public MemoryScopeIntegrityGate(MemoryScopeIntegrityOptions? options = null)
        => _options = options ?? new MemoryScopeIntegrityOptions();

    public string PolicyName => "memory.scope.integrity";
    public GateCost Cost => GateCost.PureCode;
    public MemoryGateStage Stages
        => MemoryGateStage.BeforeRead | MemoryGateStage.BeforeWrite | MemoryGateStage.BeforePromotion;
    public MemoryGateRequirements Requirements => MemoryGateRequirements.AuthenticatedMemoryScope;
    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => MemoryPolicyFingerprint.Compute(
            (int)_options.ReadDimensions,
            (int)_options.WriteDimensions,
            _options.RejectModelSuppliedScope);

    public ValueTask<MemoryGateVerdict> InspectAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var scope = context.AuthenticatedScope;
        if (scope is null)
        {
            return Result(MemoryGateVerdict.Reject(PolicyName, "memory.scope.missing"));
        }

        var required = context.Stage is MemoryGateStage.BeforeRead
            ? _options.ReadDimensions
            : _options.WriteDimensions;
        if (!HasDimensions(scope, required))
        {
            return Result(MemoryGateVerdict.Reject(PolicyName, "memory.scope.incomplete"));
        }

        var ignoredModelScope = false;
        foreach (var supplied in context.ModelSuppliedScope)
        {
            if (!TryGetTrustedValue(scope, supplied.Key, out var trustedValue))
            {
                if (!_options.RejectModelSuppliedScope)
                {
                    ignoredModelScope = true;
                    continue;
                }

                return Result(MemoryGateVerdict.Reject(PolicyName, "memory.scope.unknown_argument"));
            }

            if (!string.Equals(supplied.Value, trustedValue, StringComparison.Ordinal))
            {
                if (context.HasAdministrativeCrossScopeCapability)
                {
                    continue;
                }

                if (!_options.RejectModelSuppliedScope)
                {
                    ignoredModelScope = true;
                    continue;
                }

                return Result(MemoryGateVerdict.Reject(PolicyName, "memory.scope.mismatch"));
            }
        }

        var reason = ignoredModelScope
            ? "memory.scope.model_scope_ignored"
            : context.HasAdministrativeCrossScopeCapability && context.ModelSuppliedScope.Count > 0
                ? "memory.scope.administrative_override"
                : "memory.scope.verified";
        return Result(MemoryGateVerdict.Allow(PolicyName, reason));
    }

    private static bool HasDimensions(MemorySecurityScope scope, MemoryScopeDimensions dimensions)
        => (!dimensions.HasFlag(MemoryScopeDimensions.Tenant) || scope.TenantId is not null) &&
           (!dimensions.HasFlag(MemoryScopeDimensions.User) || scope.UserId is not null) &&
           (!dimensions.HasFlag(MemoryScopeDimensions.Agent) || scope.AgentId is not null) &&
           (!dimensions.HasFlag(MemoryScopeDimensions.Application) || scope.ApplicationId is not null) &&
           (!dimensions.HasFlag(MemoryScopeDimensions.Session) || scope.SessionId is not null);

    private static bool TryGetTrustedValue(
        MemorySecurityScope scope,
        string suppliedName,
        out string? trustedValue)
    {
        trustedValue = suppliedName.ToUpperInvariant() switch
        {
            "TENANT" or "TENANTID" or "TENANT_ID" => scope.TenantId,
            "USER" or "USERID" or "USER_ID" => scope.UserId,
            "AGENT" or "AGENTID" or "AGENT_ID" => scope.AgentId,
            "APPLICATION" or "APPLICATIONID" or "APPLICATION_ID" or "APPID" => scope.ApplicationId,
            "SESSION" or "SESSIONID" or "SESSION_ID" => scope.SessionId,
            _ => null,
        };

        return trustedValue is not null;
    }

    private static ValueTask<MemoryGateVerdict> Result(MemoryGateVerdict verdict)
        => ValueTask.FromResult(verdict);
}

/// <summary>Applies bounded provenance, trust, content, and promotion checks before persistence.</summary>
public sealed class MemoryWriteAdmissionGate : IMemoryGate, IConfigurationFingerprintContributor
{
    private static readonly Regex CredentialPattern = new(
        @"\b(api[_-]?key|password|secret|token)\b\s*[:=]\s*[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex EmailPattern = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(50));

    private static readonly string[] InstructionSignals =
    [
        "ignore previous",
        "system message",
        "developer message",
        "call tool",
        "execute command",
        "always obey",
        "do not reveal",
        "<tool_call",
        "[system]",
    ];

    private readonly MemoryWriteAdmissionOptions _options;

    public MemoryWriteAdmissionGate(MemoryWriteAdmissionOptions? options = null)
        => _options = options ?? new MemoryWriteAdmissionOptions();

    public string PolicyName => "memory.write.admission";
    public GateCost Cost => GateCost.Bounded;
    public MemoryGateStage Stages => MemoryGateStage.BeforeWrite | MemoryGateStage.BeforePromotion;
    public MemoryGateRequirements Requirements => MemoryGateRequirements.None;
    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => MemoryPolicyFingerprint.Compute(
            _options.MaximumContentCharacters,
            (int)_options.MinimumTrust,
            _options.ExcludedCategories.OrderBy(category => category),
            _options.SanitizeSecrets,
            _options.RejectControlCharacters,
            _options.QuarantineInstructionLikeContent,
            (int)_options.MinimumPromotionTrust);

    public ValueTask<MemoryGateVerdict> InspectAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Content is null)
        {
            return Result(MemoryGateVerdict.Reject(PolicyName, "memory.write.content_missing"));
        }

        if (context.Content.Length > _options.MaximumContentCharacters)
        {
            return Result(MemoryGateVerdict.Reject(PolicyName, "memory.write.content_too_large"));
        }

        if (_options.ExcludedCategories.Contains(context.Operation.Category))
        {
            return Result(MemoryGateVerdict.Reject(PolicyName, "memory.write.category_excluded"));
        }

        if (context.Provenance.SourceKind is MemorySourceKind.Unknown ||
            string.IsNullOrWhiteSpace(context.Provenance.RootLineageId))
        {
            return Result(MemoryGateVerdict.Reject(PolicyName, "memory.write.provenance_missing"));
        }

        if (context.Provenance.Trust < _options.MinimumTrust)
        {
            return Result(MemoryGateVerdict.Quarantine(PolicyName, "memory.write.trust_insufficient"));
        }

        if ((context.Kind is MemoryOperationKind.Promote ||
             context.Stage is MemoryGateStage.BeforePromotion) &&
            context.Provenance.Trust < _options.MinimumPromotionTrust)
        {
            return Result(MemoryGateVerdict.Quarantine(PolicyName, "memory.write.promotion_trust_insufficient"));
        }

        if (_options.RejectControlCharacters && ContainsUnsafeCharacters(context.Content))
        {
            return Result(MemoryGateVerdict.Reject(PolicyName, "memory.write.unsafe_characters"));
        }

        if (_options.QuarantineInstructionLikeContent &&
            context.Provenance.Trust < MemoryTrustLevel.ApplicationTrusted &&
            ContainsInstructionSignal(context.Content))
        {
            return Result(MemoryGateVerdict.Quarantine(PolicyName, "memory.write.instruction_like"));
        }

        if (_options.SanitizeSecrets)
        {
            var sanitized = EmailPattern.Replace(
                CredentialPattern.Replace(context.Content, "$1=[REDACTED]"),
                "[REDACTED_EMAIL]");
            if (!string.Equals(sanitized, context.Content, StringComparison.Ordinal))
            {
                return Result(MemoryGateVerdict.Sanitize(
                    PolicyName,
                    sanitized,
                    "memory.write.sensitive_data_redacted"));
            }
        }

        return Result(MemoryGateVerdict.Allow(PolicyName, "memory.write.admitted"));
    }

    internal static bool ContainsInstructionSignal(string value)
        => InstructionSignals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsUnsafeCharacters(string value)
    {
        foreach (var character in value)
        {
            if ((char.IsControl(character) && character is not ('\r' or '\n' or '\t')) ||
                character is '\u200B' or '\u200C' or '\u200D' or '\u2060' or
                    '\u202A' or '\u202B' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069')
            {
                return true;
            }
        }

        return false;
    }

    private static ValueTask<MemoryGateVerdict> Result(MemoryGateVerdict verdict)
        => ValueTask.FromResult(verdict);
}

/// <summary>Prevents lower-trust replacement and same-lineage vote amplification.</summary>
public sealed class MemoryConflictGate : IMemoryGate, IConfigurationFingerprintContributor
{
    private readonly MemoryConflictOptions _options;

    public MemoryConflictGate(MemoryConflictOptions? options = null)
        => _options = options ?? new MemoryConflictOptions();

    public string PolicyName => "memory.conflict";
    public GateCost Cost => GateCost.PureCode;
    public MemoryGateStage Stages => MemoryGateStage.BeforeWrite | MemoryGateStage.BeforePromotion;
    public MemoryGateRequirements Requirements => MemoryGateRequirements.None;
    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => MemoryPolicyFingerprint.Compute(
            _options.RequiredIndependentCorroborations,
            (int)_options.EqualTrustConflictAction,
            (int)_options.DuplicateLineageAction);

    public ValueTask<MemoryGateVerdict> InspectAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Conflicts.Count == 0)
        {
            return Result(MemoryGateVerdict.Allow(PolicyName, "memory.conflict.none"));
        }

        var contradictory = context.Conflicts
            .Where(candidate => !string.Equals(
                candidate.ContentDigest,
                context.ContentDigest,
                StringComparison.Ordinal))
            .ToArray();
        if (contradictory.Length == 0)
        {
            var sameLineage = context.Conflicts.Any(candidate => string.Equals(
                candidate.RootLineageId,
                context.Provenance.RootLineageId,
                StringComparison.Ordinal));
            return sameLineage
                ? Result(Disposition(_options.DuplicateLineageAction, "memory.conflict.duplicate_lineage"))
                : Result(MemoryGateVerdict.Allow(PolicyName, "memory.conflict.duplicate_independent"));
        }

        if (contradictory.Any(candidate => candidate.Trust > context.Provenance.Trust))
        {
            return Result(MemoryGateVerdict.Reject(PolicyName, "memory.conflict.higher_trust_exists"));
        }

        if (contradictory.Any(candidate => candidate.Trust == context.Provenance.Trust))
        {
            var supportingRoots = context.Conflicts
                .Where(candidate =>
                    candidate.Trust >= context.Provenance.Trust &&
                    string.Equals(candidate.ContentDigest, context.ContentDigest, StringComparison.Ordinal) &&
                    !string.Equals(
                        candidate.RootLineageId,
                        context.Provenance.RootLineageId,
                        StringComparison.Ordinal))
                .Select(candidate => candidate.RootLineageId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (supportingRoots < _options.RequiredIndependentCorroborations)
            {
                return Result(Disposition(
                    _options.EqualTrustConflictAction,
                    "memory.conflict.corroboration_insufficient"));
            }
        }

        return Result(MemoryGateVerdict.Allow(PolicyName, "memory.conflict.reconciliable"));
    }

    private MemoryGateVerdict Disposition(MemoryGateAction action, string reasonCode)
        => action switch
        {
            MemoryGateAction.Quarantine => MemoryGateVerdict.Quarantine(PolicyName, reasonCode),
            MemoryGateAction.RequireApproval => MemoryGateVerdict.RequireApproval(PolicyName, reasonCode),
            _ => MemoryGateVerdict.Reject(PolicyName, reasonCode),
        };

    private static ValueTask<MemoryGateVerdict> Result(MemoryGateVerdict verdict)
        => ValueTask.FromResult(verdict);
}

/// <summary>Excludes stale, cross-scope, tampered, revoked, or unsafe recalled records before context merge.</summary>
public sealed class MemoryRecallAdmissionGate : IMemoryGate, IConfigurationFingerprintContributor
{
    private readonly MemoryRecallAdmissionOptions _options;
    private readonly TimeProvider _timeProvider;

    public MemoryRecallAdmissionGate(
        MemoryRecallAdmissionOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new MemoryRecallAdmissionOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string PolicyName => "memory.recall.admission";
    public GateCost Cost => GateCost.Bounded;
    public MemoryGateStage Stages => MemoryGateStage.AfterRead;
    public MemoryGateRequirements Requirements => MemoryGateRequirements.None;
    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => MemoryPolicyFingerprint.Compute(
            (int)_options.MinimumTrust,
            _options.RequireRecordMetadata,
            _options.RequireIntegrityVerification,
            _options.RequireCitationVerification,
            _options.ExcludeInstructionLikeContent,
            _options.DelimitUntrustedContent,
            _timeProvider.GetType().FullName);

    public ValueTask<MemoryGateVerdict> InspectAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = context.RecordMetadata;
        if (metadata is null)
        {
            return Result(_options.RequireRecordMetadata
                ? MemoryGateVerdict.Exclude(PolicyName, "memory.recall.metadata_missing")
                : MemoryGateVerdict.Allow(PolicyName, "memory.recall.metadata_optional"));
        }

        if (context.AuthenticatedScope is null ||
            !string.Equals(
                context.AuthenticatedScope.ComputeCorrelation(),
                metadata.OwnerScope.ComputeCorrelation(),
                StringComparison.Ordinal))
        {
            return Result(MemoryGateVerdict.Exclude(PolicyName, "memory.recall.scope_mismatch"));
        }

        if (metadata.State is not MemoryRecordState.Active)
        {
            return Result(MemoryGateVerdict.Exclude(
                PolicyName,
                $"memory.recall.{metadata.State.ToString().ToLowerInvariant()}"));
        }

        if (metadata.ExpiresAtUtc is { } expiresAt && expiresAt <= _timeProvider.GetUtcNow())
        {
            return Result(MemoryGateVerdict.Exclude(PolicyName, "memory.recall.expired"));
        }

        if (_options.RequireIntegrityVerification && metadata.IntegrityVerified is not true)
        {
            return Result(MemoryGateVerdict.Exclude(PolicyName, "memory.recall.integrity_unverified"));
        }

        if (_options.RequireCitationVerification && metadata.CitationsVerified is not true)
        {
            return Result(MemoryGateVerdict.Exclude(PolicyName, "memory.recall.citation_unverified"));
        }

        if (context.Provenance.Trust < _options.MinimumTrust)
        {
            return Result(MemoryGateVerdict.Exclude(PolicyName, "memory.recall.trust_insufficient"));
        }

        if (_options.ExcludeInstructionLikeContent &&
            context.Content is not null &&
            context.Provenance.Trust < MemoryTrustLevel.ApplicationTrusted &&
            MemoryWriteAdmissionGate.ContainsInstructionSignal(context.Content))
        {
            return Result(MemoryGateVerdict.Exclude(PolicyName, "memory.recall.instruction_like"));
        }

        if (_options.DelimitUntrustedContent &&
            context.Content is not null &&
            context.Provenance.Trust < MemoryTrustLevel.ApplicationTrusted)
        {
            var delimited =
                $"<memory-item id=\"{HtmlEncoder.Default.Encode(metadata.MemoryId)}\" trust=\"{context.Provenance.Trust}\">\n" +
                context.Content +
                "\n</memory-item>";
            if (delimited.Length > MemoryGateContext.MaximumContentCharacters)
            {
                return Result(MemoryGateVerdict.Exclude(PolicyName, "memory.recall.delimiter_budget"));
            }

            return Result(MemoryGateVerdict.Sanitize(
                PolicyName,
                delimited,
                "memory.recall.delimited"));
        }

        return Result(MemoryGateVerdict.Allow(PolicyName, "memory.recall.admitted"));
    }

    private static ValueTask<MemoryGateVerdict> Result(MemoryGateVerdict verdict)
        => ValueTask.FromResult(verdict);
}

/// <summary>Fails closed when a host-computed write, recall, promotion, lineage, or quarantine cap is exhausted.</summary>
public sealed class MemoryResourceBudgetGate : IMemoryGate, IConfigurationFingerprintContributor
{
    private readonly MemoryResourceBudgetOptions _options;

    public MemoryResourceBudgetGate(MemoryResourceBudgetOptions? options = null)
        => _options = options ?? new MemoryResourceBudgetOptions();

    public string PolicyName => "memory.resource.budget";
    public GateCost Cost => GateCost.PureCode;
    public MemoryGateStage Stages
        => MemoryGateStage.BeforeWrite | MemoryGateStage.BeforePromotion | MemoryGateStage.AfterRead;
    public MemoryGateRequirements Requirements => MemoryGateRequirements.None;
    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => MemoryPolicyFingerprint.Compute(
            _options.MaximumRecordCharacters,
            _options.MaximumWritesPerRun,
            _options.MaximumWritesPerSession,
            _options.MaximumWritesPerUser,
            _options.MaximumWritesPerSource,
            _options.MaximumUniqueCandidatesPerSource,
            _options.MaximumPromotionsPerSession,
            _options.MaximumReconciliationsPerSession,
            _options.MaximumRecalledItems,
            _options.MaximumRecalledCharacters,
            _options.MaximumLineageDepth,
            _options.MaximumParentCount,
            _options.MaximumQuarantinedItemsPerScope);

    public ValueTask<MemoryGateVerdict> InspectAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if ((context.Content?.Length ?? 0) > _options.MaximumRecordCharacters)
        {
            return Result(Block(context, "memory.budget.record_size"));
        }

        if (context.Provenance.ParentMemoryIds.Count > _options.MaximumParentCount)
        {
            return Result(Block(context, "memory.budget.parent_count"));
        }

        var budget = context.Budget;
        if (budget is null)
        {
            return Result(Block(context, "memory.budget.snapshot_missing"));
        }

        string? exceeded = context.Stage switch
        {
            MemoryGateStage.AfterRead when budget.RecalledItemCount > _options.MaximumRecalledItems
                => "memory.budget.recalled_items",
            MemoryGateStage.AfterRead when budget.RecalledContentCharacters > _options.MaximumRecalledCharacters
                => "memory.budget.recalled_content",
            MemoryGateStage.BeforeWrite or MemoryGateStage.BeforePromotion
                when budget.WritesInRun >= _options.MaximumWritesPerRun
                => "memory.budget.writes_run",
            MemoryGateStage.BeforeWrite or MemoryGateStage.BeforePromotion
                when budget.WritesInSession >= _options.MaximumWritesPerSession
                => "memory.budget.writes_session",
            MemoryGateStage.BeforeWrite or MemoryGateStage.BeforePromotion
                when budget.WritesForUser >= _options.MaximumWritesPerUser
                => "memory.budget.writes_user",
            MemoryGateStage.BeforeWrite or MemoryGateStage.BeforePromotion
                when budget.WritesForSource >= _options.MaximumWritesPerSource
                => "memory.budget.writes_source",
            MemoryGateStage.BeforeWrite or MemoryGateStage.BeforePromotion
                when budget.UniqueCandidatesForSource >= _options.MaximumUniqueCandidatesPerSource
                => "memory.budget.unique_candidates",
            MemoryGateStage.BeforePromotion when budget.PromotionsInSession >= _options.MaximumPromotionsPerSession
                => "memory.budget.promotions",
            MemoryGateStage.BeforeWrite when context.Kind is MemoryOperationKind.Reconcile &&
                budget.ReconciliationAttemptsInSession >= _options.MaximumReconciliationsPerSession
                => "memory.budget.reconciliations",
            _ when budget.LineageDepth > _options.MaximumLineageDepth
                => "memory.budget.lineage_depth",
            _ when budget.QuarantinedItemsForScope >= _options.MaximumQuarantinedItemsPerScope
                => "memory.budget.quarantine_volume",
            _ => null,
        };

        return Result(exceeded is null
            ? MemoryGateVerdict.Allow(PolicyName, "memory.budget.available")
            : Block(context, exceeded));
    }

    private MemoryGateVerdict Block(MemoryGateContext context, string reasonCode)
        => context.Stage is MemoryGateStage.AfterRead
            ? MemoryGateVerdict.Exclude(PolicyName, reasonCode)
            : MemoryGateVerdict.Reject(PolicyName, reasonCode);

    private static ValueTask<MemoryGateVerdict> Result(MemoryGateVerdict verdict)
        => ValueTask.FromResult(verdict);
}
