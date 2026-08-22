// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.PartnerDeskDemo;
using AgentEval.PartnerDeskDemo.Demo;
using AgentEval.PartnerDeskDemo.Tools;

namespace AgentEval.PartnerDeskDemo.Tests;

/// <summary>
/// Runs all four phases once, against a scripted provider and the real PartnerIntel MCP child process, and
/// exposes the recorded outcomes to the per-phase tests.
/// </summary>
/// <remarks>
/// The model is scripted so the suite is deterministic in CI; everything downstream of the model is the real
/// thing — a genuine MCP session over stdio, the shipped gates, the demo's own tools and containment store.
/// </remarks>
public sealed class PartnerDeskPhaseFixture : IAsyncLifetime
{
    private readonly Dictionary<DemoPhase, PhaseOutcome> _outcomes = [];
    private string _workingDirectory = string.Empty;
    private PartnerDeskRunner? _runner;

    /// <summary>The outcome recorded for each phase.</summary>
    public IReadOnlyDictionary<DemoPhase, PhaseOutcome> Outcomes => _outcomes;

    /// <summary>The local outbox file the faked email tool appended to.</summary>
    public string OutboxPath { get; private set; } = string.Empty;

    /// <summary>The register the demo loaded.</summary>
    public PartnerRegister Register { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "agenteval-partnerdesk-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workingDirectory);
        OutboxPath = Path.Combine(_workingDirectory, "outbox.log");

        Register = PartnerRegister.Load();
        _runner = new PartnerDeskRunner(
            context => ScriptedPartnerDeskModel.Create(context, Register),
            DemoOutput.Silent,
            OutboxPath,
            Register);

        try
        {
            foreach (var phase in new[] { DemoPhase.Clean, DemoPhase.Compromised, DemoPhase.Level1, DemoPhase.Level2 })
            {
                _outcomes[phase] = await _runner.RunAsync(phase, Program.StandardQuestion, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // xunit does not call DisposeAsync when InitializeAsync throws, so release the MCP child and temp dir
            // here rather than leaking them for the life of the test process.
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_runner is not null)
        {
            await _runner.DisposeAsync().ConfigureAwait(false);
            _runner = null;
        }

        try
        {
            if (Directory.Exists(_workingDirectory))
            {
                Directory.Delete(_workingDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory (e.g. a still-terminating MCP child holding a handle) must not fail the
            // suite, and must not mask the original init failure when DisposeAsync runs from the init catch path.
        }
    }
}
