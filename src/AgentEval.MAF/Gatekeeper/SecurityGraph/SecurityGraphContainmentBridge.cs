// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// An evidence-backed graph decision that can be represented by a Phase-3 containment target.
/// Decisions can only be created from a complete report with relevant observed facts.
/// </summary>
public sealed class SecurityGraphContainmentDecision
{
    private SecurityGraphContainmentDecision(
        ContainmentTarget target,
        string reasonCode,
        string evidenceReference)
    {
        Target = target;
        ReasonCode = ContainmentValidation.Token(
            reasonCode,
            nameof(reasonCode),
            ContainmentValidation.MaxReasonCodeLength);
        EvidenceReference = ContainmentValidation.Token(
            evidenceReference,
            nameof(evidenceReference),
            ContainmentValidation.MaxEvidenceReferenceLength);
    }

    /// <summary>The exact Phase-3 target to contain.</summary>
    public ContainmentTarget Target { get; }

    /// <summary>A bounded reason-class token.</summary>
    public string ReasonCode { get; }

    /// <summary>An opaque reference to operator-only evidence.</summary>
    public string EvidenceReference { get; }

    /// <summary>Creates a tenant-wide decision from a complete report containing accepted calls.</summary>
    public static SecurityGraphContainmentDecision ForTenant(
        SecurityGraphReport report,
        string reasonCode,
        string evidenceReference)
    {
        ValidateCompleteReport(report);
        var expectedRate = report.TotalCallCount > 0
            ? (double)report.TotalBlockedCallCount / report.TotalCallCount
            : double.NaN;
        if (report.TotalCallCount < 1 ||
            report.TotalBlockedCallCount < 0 ||
            report.TotalBlockedCallCount > report.TotalCallCount ||
            report.FleetBlockRate is not { } rate ||
            !double.IsFinite(rate) ||
            Math.Abs(rate - expectedRate) > 1e-12)
        {
            throw new ArgumentException(
                "A tenant containment decision requires complete observed call facts.",
                nameof(report));
        }

        return new SecurityGraphContainmentDecision(
            new ContainmentTarget.TenantScope(report.Tenant),
            reasonCode,
            evidenceReference);
    }

    /// <summary>
    /// Creates a decision for an observed MCP server or agent endpoint. Agent/tool nodes are not enforceable
    /// Phase-3 containment identities and are rejected.
    /// </summary>
    public static SecurityGraphContainmentDecision ForNode(
        SecurityGraphReport report,
        SecurityGraphNode node,
        string reasonCode,
        string evidenceReference)
    {
        ValidateCompleteReport(report);
        ArgumentNullException.ThrowIfNull(node);
        var matches = report.Nodes
            .Where(candidate => candidate is not null && candidate.Node == node)
            .ToArray();
        if (matches.Length != 1 ||
            matches[0].NeverObserved ||
            matches[0].LastObservedAtUtc is null)
        {
            throw new ArgumentException(
                "A node containment decision requires one actually observed node.",
                nameof(node));
        }

        ContainmentTarget target = node.Kind switch
        {
            SecurityGraphNodeKind.McpServer =>
                new ContainmentTarget.McpServer(
                    report.Tenant,
                    node.Identifier),
            SecurityGraphNodeKind.AgentEndpoint =>
                new ContainmentTarget.AgentEndpoint(
                    report.Tenant,
                    node.Identifier),
            _ => throw new ArgumentException(
                "Only MCP-server and agent-endpoint graph nodes map to containment targets.",
                nameof(node)),
        };
        return new SecurityGraphContainmentDecision(
            target,
            reasonCode,
            evidenceReference);
    }

    private static void ValidateCompleteReport(SecurityGraphReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        SecurityGraphValidation.Identity(
            report.Tenant,
            nameof(report),
            ContainmentValidation.MaxTenantLength);
        if (report.Coverage != SecurityGraphCoverageState.Complete ||
            report.Nodes is null ||
            report.Edges is null ||
            report.NeverObservedNodes is null ||
            report.StaleNodes is null)
        {
            throw new ArgumentException(
                "A graph containment decision requires a complete structurally valid report.",
                nameof(report));
        }
    }
}

/// <summary>
/// Fixed-tenant bridge that persists an evidence-backed graph decision into the existing Phase-3 containment
/// store. The bridge does not own or dispose the store.
/// </summary>
public sealed class SecurityGraphContainmentBridge
{
    private const string Issuer = "security-graph";
    private readonly string _tenant;
    private readonly IContainmentStore _store;

    /// <summary>Creates a bridge for exactly one normalized tenant and caller-owned containment store.</summary>
    public SecurityGraphContainmentBridge(
        string tenant,
        IContainmentStore store)
    {
        _tenant = SecurityGraphValidation.Identity(
            tenant,
            nameof(tenant),
            ContainmentValidation.MaxTenantLength);
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Idempotently persists the graph decision through the Phase-3 containment lifecycle.</summary>
    public async ValueTask<ContainmentMutationResult> ApplyAsync(
        SecurityGraphContainmentDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
            decision.Target.Tenant,
            _tenant,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The graph containment decision belongs to a different tenant.",
                nameof(decision));
        }

        var result = await _store.ContainAsync(
            new ContainmentRequest(
                decision.Target,
                decision.ReasonCode,
                decision.EvidenceReference,
                Issuer),
            cancellationToken).ConfigureAwait(false);
        if (result is null ||
            result.Snapshot is null ||
            result.Snapshot.Target != decision.Target)
        {
            throw new InvalidOperationException(
                "Containment store returned an invalid graph-decision result.");
        }

        if ((result.Disposition is
                ContainmentMutationDisposition.Applied or
                ContainmentMutationDisposition.Unchanged) &&
            result.Snapshot.State == ContainmentSnapshotState.Active)
        {
            return result;
        }

        if (result.Disposition == ContainmentMutationDisposition.Indeterminate &&
            result.Snapshot.State == ContainmentSnapshotState.Indeterminate)
        {
            return result;
        }

        throw new InvalidOperationException(
            "Containment store did not prove the graph-decision outcome.");
    }
}
