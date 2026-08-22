// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Gatekeeper;

namespace AgentEval.PartnerDeskDemo.Gates;

/// <summary>
/// The demo's durable containment state: a <see cref="JsonFileContainmentStore"/> plus the two targets Level 2
/// enforces against.
/// </summary>
/// <remarks>
/// <para>
/// Containment is <b>enforcement state, not a compromise classifier</b>. Nothing in this type decides that
/// PartnerIntel is hostile. The Level 2 result gate decides that; <see cref="ContainAsync"/> is the operator step
/// that turns the finding into a durable decision, and <see cref="ContainmentOverrideGate"/> — installed
/// automatically by <c>UseGatekeeper</c> once a store is configured — is what refuses the next call.
/// </para>
/// <para>
/// Release is deliberately impossible here: the verifier denies every authorization, so a demo cannot accidentally
/// un-quarantine the server by re-running a phase. Restarting the demo starts from a fresh store directory.
/// </para>
/// </remarks>
public sealed class DemoContainment : IDisposable
{
    private readonly string _directory;

    private DemoContainment(string directory, JsonFileContainmentStore store, string sessionId)
    {
        _directory = directory;
        Store = store;
        SessionTarget = PartnerDeskGates.SessionTarget(sessionId);
        PartnerIntelTarget = PartnerDeskGates.PartnerIntelTarget();
    }

    /// <summary>The durable single-process containment store.</summary>
    public JsonFileContainmentStore Store { get; }

    /// <summary>The session boundary, used by the run-pre <c>ContainedIdentityGate</c>.</summary>
    public ContainmentTarget.Session SessionTarget { get; }

    /// <summary>The third-party MCP server boundary, used by the tool-seam <c>ContainmentOverrideGate</c>.</summary>
    public ContainmentTarget.McpServer PartnerIntelTarget { get; }

    /// <summary>Creates a store under a fresh temporary directory.</summary>
    public static DemoContainment Create(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var directory = Path.Combine(
            Path.GetTempPath(),
            "agenteval-partnerdesk-containment",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var store = new JsonFileContainmentStore(
            Path.Combine(directory, "containment.json"),
            new DenyAllReleaseVerifier(),
            new JsonFileContainmentStoreOptions { BootstrapIfMissing = true });

        return new DemoContainment(directory, store, sessionId);
    }

    /// <summary>The current durable state of the PartnerIntel target.</summary>
    public ContainmentSnapshotState PartnerIntelState => Store.GetCurrent(PartnerIntelTarget).State;

    /// <summary>Records the operator decision to isolate PartnerIntel, driven by a result-admission finding.</summary>
    public async Task<ContainmentMutationResult> ContainAsync(
        string reasonCode,
        string evidenceReference,
        CancellationToken cancellationToken = default) =>
        await Store.ContainAsync(
            new ContainmentRequest(
                PartnerIntelTarget,
                reasonCode,
                evidenceReference,
                issuer: "partnerdesk-demo-operator"),
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Dispose()
    {
        Store.Dispose();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a demo over.
        }
    }

    private sealed class DenyAllReleaseVerifier : IContainmentReleaseAuthorizationVerifier
    {
        public bool Verify(
            ContainmentReleaseAuthorization authorization,
            ReadOnlyMemory<byte> canonicalPayload) => false;
    }
}
