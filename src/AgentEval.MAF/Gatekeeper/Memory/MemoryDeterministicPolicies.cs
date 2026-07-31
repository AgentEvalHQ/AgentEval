// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Text;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>Trusted scope dimensions required for a memory operation.</summary>
[Flags]
public enum MemoryScopeDimensions
{
    None = 0,
    Tenant = 1 << 0,
    User = 1 << 1,
    Agent = 1 << 2,
    Application = 1 << 3,
    Session = 1 << 4,
}

/// <summary>Lifecycle state of a recalled durable memory record.</summary>
public enum MemoryRecordState
{
    Active,
    Quarantined,
    Revoked,
    Superseded,
}

/// <summary>Host-supplied state used to validate a recalled record.</summary>
public sealed class MemoryRecordMetadata
{
    /// <summary>Creates bounded metadata for one recalled record.</summary>
    public MemoryRecordMetadata(
        string memoryId,
        MemorySecurityScope ownerScope,
        MemoryRecordState state = MemoryRecordState.Active,
        DateTimeOffset? createdAtUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        bool? integrityVerified = null,
        bool? citationsVerified = null)
    {
        MemoryId = MemoryValidation.Identifier(memoryId, nameof(memoryId));
        OwnerScope = ownerScope ?? throw new ArgumentNullException(nameof(ownerScope));
        State = MemoryValidation.Defined(state, nameof(state));
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        IntegrityVerified = integrityVerified;
        CitationsVerified = citationsVerified;

        if (createdAtUtc is not null && expiresAtUtc is not null && expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentException("Record expiry must be later than creation.", nameof(expiresAtUtc));
        }
    }

    public string MemoryId { get; }
    public MemorySecurityScope OwnerScope { get; }
    public MemoryRecordState State { get; }
    public DateTimeOffset? CreatedAtUtc { get; }
    public DateTimeOffset? ExpiresAtUtc { get; }
    public bool? IntegrityVerified { get; }
    public bool? CitationsVerified { get; }
}

/// <summary>Host-computed counters consumed by the deterministic resource-budget gate.</summary>
public sealed class MemoryBudgetSnapshot
{
    /// <summary>Creates a non-negative immutable counter snapshot.</summary>
    public MemoryBudgetSnapshot(
        int writesInRun = 0,
        int writesInSession = 0,
        int writesForUser = 0,
        int writesForSource = 0,
        int uniqueCandidatesForSource = 0,
        int promotionsInSession = 0,
        int reconciliationAttemptsInSession = 0,
        int recalledItemCount = 0,
        int recalledContentCharacters = 0,
        int lineageDepth = 0,
        int quarantinedItemsForScope = 0)
    {
        WritesInRun = NonNegative(writesInRun, nameof(writesInRun));
        WritesInSession = NonNegative(writesInSession, nameof(writesInSession));
        WritesForUser = NonNegative(writesForUser, nameof(writesForUser));
        WritesForSource = NonNegative(writesForSource, nameof(writesForSource));
        UniqueCandidatesForSource = NonNegative(uniqueCandidatesForSource, nameof(uniqueCandidatesForSource));
        PromotionsInSession = NonNegative(promotionsInSession, nameof(promotionsInSession));
        ReconciliationAttemptsInSession = NonNegative(
            reconciliationAttemptsInSession,
            nameof(reconciliationAttemptsInSession));
        RecalledItemCount = NonNegative(recalledItemCount, nameof(recalledItemCount));
        RecalledContentCharacters = NonNegative(recalledContentCharacters, nameof(recalledContentCharacters));
        LineageDepth = NonNegative(lineageDepth, nameof(lineageDepth));
        QuarantinedItemsForScope = NonNegative(quarantinedItemsForScope, nameof(quarantinedItemsForScope));
    }

    public int WritesInRun { get; }
    public int WritesInSession { get; }
    public int WritesForUser { get; }
    public int WritesForSource { get; }
    public int UniqueCandidatesForSource { get; }
    public int PromotionsInSession { get; }
    public int ReconciliationAttemptsInSession { get; }
    public int RecalledItemCount { get; }
    public int RecalledContentCharacters { get; }
    public int LineageDepth { get; }
    public int QuarantinedItemsForScope { get; }

    private static int NonNegative(int value, string parameterName)
        => value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "Memory budget counters cannot be negative.");
}

/// <summary>Configuration for trusted scope enforcement.</summary>
public sealed class MemoryScopeIntegrityOptions
{
    private const MemoryScopeDimensions KnownDimensions =
        MemoryScopeDimensions.Tenant |
        MemoryScopeDimensions.User |
        MemoryScopeDimensions.Agent |
        MemoryScopeDimensions.Application |
        MemoryScopeDimensions.Session;

    public MemoryScopeIntegrityOptions(
        MemoryScopeDimensions readDimensions = MemoryScopeDimensions.Tenant | MemoryScopeDimensions.User,
        MemoryScopeDimensions writeDimensions = MemoryScopeDimensions.Tenant | MemoryScopeDimensions.User,
        bool rejectModelSuppliedScope = true)
    {
        if ((readDimensions & ~KnownDimensions) != 0 || (writeDimensions & ~KnownDimensions) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readDimensions), "Only known scope dimensions are allowed.");
        }
        if ((readDimensions & writeDimensions) != writeDimensions)
        {
            throw new ArgumentException(
                "Read scope cannot be broader than authorized write scope.",
                nameof(readDimensions));
        }

        ReadDimensions = readDimensions;
        WriteDimensions = writeDimensions;
        RejectModelSuppliedScope = rejectModelSuppliedScope;
    }

    public MemoryScopeDimensions ReadDimensions { get; }
    public MemoryScopeDimensions WriteDimensions { get; }
    public bool RejectModelSuppliedScope { get; }
}

/// <summary>Configuration for deterministic write admission.</summary>
public sealed class MemoryWriteAdmissionOptions
{
    public MemoryWriteAdmissionOptions(
        int maximumContentCharacters = 16_384,
        MemoryTrustLevel minimumTrust = MemoryTrustLevel.Low,
        IEnumerable<MemoryCategory>? excludedCategories = null,
        bool sanitizeSecrets = true,
        bool rejectControlCharacters = true,
        bool quarantineInstructionLikeContent = true,
        MemoryTrustLevel minimumPromotionTrust = MemoryTrustLevel.High)
    {
        if (maximumContentCharacters is < 1 or > MemoryGateContext.MaximumContentCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumContentCharacters));
        }

        MaximumContentCharacters = maximumContentCharacters;
        MinimumTrust = MemoryValidation.Defined(minimumTrust, nameof(minimumTrust));
        ExcludedCategories = FreezeCategories(excludedCategories);
        SanitizeSecrets = sanitizeSecrets;
        RejectControlCharacters = rejectControlCharacters;
        QuarantineInstructionLikeContent = quarantineInstructionLikeContent;
        MinimumPromotionTrust = MemoryValidation.Defined(minimumPromotionTrust, nameof(minimumPromotionTrust));
    }

    public int MaximumContentCharacters { get; }
    public MemoryTrustLevel MinimumTrust { get; }
    public IReadOnlySet<MemoryCategory> ExcludedCategories { get; }
    public bool SanitizeSecrets { get; }
    public bool RejectControlCharacters { get; }
    public bool QuarantineInstructionLikeContent { get; }
    public MemoryTrustLevel MinimumPromotionTrust { get; }

    private static IReadOnlySet<MemoryCategory> FreezeCategories(IEnumerable<MemoryCategory>? categories)
    {
        var result = new HashSet<MemoryCategory>();
        foreach (var category in categories ?? Array.Empty<MemoryCategory>())
        {
            result.Add(MemoryValidation.Defined(category, nameof(categories)));
        }

        return result;
    }
}

/// <summary>Configuration for trust-aware deterministic conflict handling.</summary>
public sealed class MemoryConflictOptions
{
    public MemoryConflictOptions(
        int requiredIndependentCorroborations = 2,
        MemoryGateAction equalTrustConflictAction = MemoryGateAction.Quarantine,
        MemoryGateAction duplicateLineageAction = MemoryGateAction.Reject)
    {
        if (requiredIndependentCorroborations is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredIndependentCorroborations));
        }

        RequiredIndependentCorroborations = requiredIndependentCorroborations;
        EqualTrustConflictAction = ValidateDisposition(equalTrustConflictAction, nameof(equalTrustConflictAction));
        DuplicateLineageAction = ValidateDisposition(duplicateLineageAction, nameof(duplicateLineageAction));
    }

    public int RequiredIndependentCorroborations { get; }
    public MemoryGateAction EqualTrustConflictAction { get; }
    public MemoryGateAction DuplicateLineageAction { get; }

    private static MemoryGateAction ValidateDisposition(MemoryGateAction action, string parameterName)
    {
        MemoryValidation.Defined(action, parameterName);
        if (action is not (MemoryGateAction.Quarantine or MemoryGateAction.RequireApproval or MemoryGateAction.Reject))
        {
            throw new ArgumentException("Conflict dispositions must quarantine, require approval, or reject.", parameterName);
        }

        return action;
    }
}

/// <summary>Configuration for deterministic recall admission.</summary>
public sealed class MemoryRecallAdmissionOptions
{
    public MemoryRecallAdmissionOptions(
        MemoryTrustLevel minimumTrust = MemoryTrustLevel.Low,
        bool requireRecordMetadata = true,
        bool requireIntegrityVerification = false,
        bool requireCitationVerification = false,
        bool excludeInstructionLikeContent = true,
        bool delimitUntrustedContent = true)
    {
        MinimumTrust = MemoryValidation.Defined(minimumTrust, nameof(minimumTrust));
        RequireRecordMetadata = requireRecordMetadata;
        RequireIntegrityVerification = requireIntegrityVerification;
        RequireCitationVerification = requireCitationVerification;
        ExcludeInstructionLikeContent = excludeInstructionLikeContent;
        DelimitUntrustedContent = delimitUntrustedContent;
    }

    public MemoryTrustLevel MinimumTrust { get; }
    public bool RequireRecordMetadata { get; }
    public bool RequireIntegrityVerification { get; }
    public bool RequireCitationVerification { get; }
    public bool ExcludeInstructionLikeContent { get; }
    public bool DelimitUntrustedContent { get; }
}

/// <summary>Configuration for deterministic memory resource caps.</summary>
public sealed class MemoryResourceBudgetOptions
{
    public MemoryResourceBudgetOptions(
        int maximumRecordCharacters = 16_384,
        int maximumWritesPerRun = 32,
        int maximumWritesPerSession = 128,
        int maximumWritesPerUser = 10_000,
        int maximumWritesPerSource = 256,
        int maximumUniqueCandidatesPerSource = 128,
        int maximumPromotionsPerSession = 16,
        int maximumReconciliationsPerSession = 32,
        int maximumRecalledItems = 32,
        int maximumRecalledCharacters = 32_768,
        int maximumLineageDepth = 16,
        int maximumParentCount = 32,
        int maximumQuarantinedItemsPerScope = 1_000)
    {
        MaximumRecordCharacters = Positive(maximumRecordCharacters, nameof(maximumRecordCharacters));
        MaximumWritesPerRun = Positive(maximumWritesPerRun, nameof(maximumWritesPerRun));
        MaximumWritesPerSession = Positive(maximumWritesPerSession, nameof(maximumWritesPerSession));
        MaximumWritesPerUser = Positive(maximumWritesPerUser, nameof(maximumWritesPerUser));
        MaximumWritesPerSource = Positive(maximumWritesPerSource, nameof(maximumWritesPerSource));
        MaximumUniqueCandidatesPerSource = Positive(
            maximumUniqueCandidatesPerSource,
            nameof(maximumUniqueCandidatesPerSource));
        MaximumPromotionsPerSession = Positive(maximumPromotionsPerSession, nameof(maximumPromotionsPerSession));
        MaximumReconciliationsPerSession = Positive(
            maximumReconciliationsPerSession,
            nameof(maximumReconciliationsPerSession));
        MaximumRecalledItems = Positive(maximumRecalledItems, nameof(maximumRecalledItems));
        MaximumRecalledCharacters = Positive(maximumRecalledCharacters, nameof(maximumRecalledCharacters));
        MaximumLineageDepth = Positive(maximumLineageDepth, nameof(maximumLineageDepth));
        MaximumParentCount = Positive(maximumParentCount, nameof(maximumParentCount));
        MaximumQuarantinedItemsPerScope = Positive(
            maximumQuarantinedItemsPerScope,
            nameof(maximumQuarantinedItemsPerScope));
    }

    public int MaximumRecordCharacters { get; }
    public int MaximumWritesPerRun { get; }
    public int MaximumWritesPerSession { get; }
    public int MaximumWritesPerUser { get; }
    public int MaximumWritesPerSource { get; }
    public int MaximumUniqueCandidatesPerSource { get; }
    public int MaximumPromotionsPerSession { get; }
    public int MaximumReconciliationsPerSession { get; }
    public int MaximumRecalledItems { get; }
    public int MaximumRecalledCharacters { get; }
    public int MaximumLineageDepth { get; }
    public int MaximumParentCount { get; }
    public int MaximumQuarantinedItemsPerScope { get; }

    private static int Positive(int value, string parameterName)
        => value > 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "Memory resource caps must be positive.");
}

internal static class MemoryPolicyFingerprint
{
    internal static string Compute(params object?[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            if (value is System.Collections.IEnumerable sequence and not string)
            {
                foreach (var item in sequence)
                {
                    builder.Append(item).Append(',');
                }
            }
            else
            {
                builder.Append(value);
            }

            builder.Append('|');
        }

        return MemoryDigest.Compute(builder.ToString());
    }
}
