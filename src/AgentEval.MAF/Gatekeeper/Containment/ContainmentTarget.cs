// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>The durable boundary whose activity can be contained.</summary>
public enum ContainmentTargetKind
{
    /// <summary>A durable logical agent session.</summary>
    Session,

    /// <summary>An explicitly identified MCP server.</summary>
    McpServer,

    /// <summary>A delegated or remote agent endpoint.</summary>
    AgentEndpoint,

    /// <summary>Every Gatekeeper boundary in one tenant.</summary>
    TenantScope,
}

/// <summary>
/// Closed, tenant-scoped containment target. Equality is ordinal over normalized tenant, kind, and identifier.
/// Validation failures never echo identifiers.
/// </summary>
public abstract class ContainmentTarget : IEquatable<ContainmentTarget>
{
    private ContainmentTarget(string tenant, string identifier)
    {
        Tenant = ContainmentValidation.Identity(
            tenant,
            nameof(tenant),
            ContainmentValidation.MaxTenantLength);
        Identifier = ContainmentValidation.Identity(
            identifier,
            nameof(identifier),
            ContainmentValidation.MaxIdentifierLength);
    }

    /// <summary>The normalized, case-sensitive tenant or namespace.</summary>
    public string Tenant { get; }

    /// <summary>The normalized, case-sensitive identifier within <see cref="Tenant"/>.</summary>
    public string Identifier { get; }

    /// <summary>The target boundary kind.</summary>
    public abstract ContainmentTargetKind Kind { get; }

    /// <inheritdoc/>
    public bool Equals(ContainmentTarget? other)
        => other is not null
            && Kind == other.Kind
            && string.Equals(Tenant, other.Tenant, StringComparison.Ordinal)
            && string.Equals(Identifier, other.Identifier, StringComparison.Ordinal);

    /// <inheritdoc/>
    public sealed override bool Equals(object? obj)
        => obj is ContainmentTarget other && Equals(other);

    /// <inheritdoc/>
    public sealed override int GetHashCode()
        => HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(Tenant), StringComparer.Ordinal.GetHashCode(Identifier));

    /// <summary>Value equality for containment targets.</summary>
    public static bool operator ==(ContainmentTarget? left, ContainmentTarget? right)
        => ReferenceEquals(left, right) || (left?.Equals(right) ?? false);

    /// <summary>Value inequality for containment targets.</summary>
    public static bool operator !=(ContainmentTarget? left, ContainmentTarget? right)
        => !(left == right);

    /// <inheritdoc/>
    public sealed override string ToString() => $"ContainmentTarget.{Kind}";

    /// <summary>A durable logical agent session.</summary>
    public sealed class Session : ContainmentTarget
    {
        /// <summary>Creates a tenant-scoped session target.</summary>
        public Session(string tenant, string sessionId) : base(tenant, sessionId) { }

        /// <inheritdoc/>
        public override ContainmentTargetKind Kind => ContainmentTargetKind.Session;

        /// <summary>The normalized session identifier.</summary>
        public string SessionId => Identifier;
    }

    /// <summary>An explicitly identified MCP server.</summary>
    public sealed class McpServer : ContainmentTarget
    {
        /// <summary>Creates a tenant-scoped MCP-server target.</summary>
        public McpServer(string tenant, string serverId) : base(tenant, serverId) { }

        /// <inheritdoc/>
        public override ContainmentTargetKind Kind => ContainmentTargetKind.McpServer;

        /// <summary>The normalized MCP server identifier.</summary>
        public string ServerId => Identifier;
    }

    /// <summary>A delegated or remote agent endpoint.</summary>
    public sealed class AgentEndpoint : ContainmentTarget
    {
        /// <summary>Creates a tenant-scoped agent-endpoint target.</summary>
        public AgentEndpoint(string tenant, string agentId) : base(tenant, agentId) { }

        /// <inheritdoc/>
        public override ContainmentTargetKind Kind => ContainmentTargetKind.AgentEndpoint;

        /// <summary>The normalized agent endpoint identifier.</summary>
        public string AgentId => Identifier;
    }

    /// <summary>Every Gatekeeper boundary in one tenant.</summary>
    public sealed class TenantScope : ContainmentTarget
    {
        /// <summary>The canonical durable identifier for a tenant-wide target.</summary>
        public const string GlobalIdentifier = "global";

        /// <summary>Creates the one global containment target for a tenant.</summary>
        public TenantScope(string tenant)
            : base(tenant, GlobalIdentifier)
        {
        }

        /// <inheritdoc/>
        public override ContainmentTargetKind Kind => ContainmentTargetKind.TenantScope;
    }
}
