// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.PartnerDeskDemo.Agent;
using AgentEval.PartnerDeskDemo.Gates;
using AgentEval.PartnerDeskDemo.Mcp;
using AgentEval.PartnerDeskDemo.Tools;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.PartnerDeskDemo.Demo;

/// <summary>Which run the chat-client factory is being asked to supply a client for.</summary>
/// <param name="Phase">The phase being executed.</param>
/// <param name="IsRetryAfterContainment">True for the second Level 2 run, made after the source was contained.</param>
public readonly record struct PhaseRunContext(DemoPhase Phase, bool IsRetryAfterContainment);

/// <summary>
/// Executes one phase end to end: opens (or re-opens) the PartnerIntel MCP session, builds the agent with the
/// phase's gate level, asks the question, and returns the recorded <see cref="PhaseOutcome"/>.
/// </summary>
/// <remarks>
/// The chat client is supplied by the caller. The console demo supplies a live Azure OpenAI client; the tests
/// supply a scripted one, so the same phase code paths are exercised deterministically in CI without a model.
/// </remarks>
public sealed class PartnerDeskRunner : IAsyncDisposable
{
    private readonly Func<PhaseRunContext, IChatClient> _chatClientFactory;
    private readonly DemoOutput _output;
    private readonly int _printedRegisterRows;

    private PartnerIntelSession? _partnerIntel;
    private bool _partnerIntelEvil;

    /// <summary>Creates a runner over a chat-client factory, a console sink, and a local outbox path.</summary>
    /// <param name="chatClientFactory">Supplies the provider for each run: live Azure OpenAI, or scripted.</param>
    /// <param name="output">Where the stage-legible narration goes.</param>
    /// <param name="outboxPath">The local file the faked email tool appends to.</param>
    /// <param name="register">The loaded register; loaded from disk when omitted.</param>
    /// <param name="printedRegisterRows">How many rows a bulk listing prints before summarising the remainder.</param>
    public PartnerDeskRunner(
        Func<PhaseRunContext, IChatClient> chatClientFactory,
        DemoOutput output,
        string outboxPath,
        PartnerRegister? register = null,
        int printedRegisterRows = PartnerDatabaseTool.DefaultPrintedRows)
    {
        ArgumentNullException.ThrowIfNull(chatClientFactory);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxPath);

        _chatClientFactory = chatClientFactory;
        _output = output;
        OutboxPath = outboxPath;
        Register = register ?? PartnerRegister.Load();
        _printedRegisterRows = printedRegisterRows;
    }

    /// <summary>
    /// Clears the local outbox so a demo session starts from an empty "what would have left the building" log.
    /// </summary>
    /// <remarks>
    /// Called by the console at startup, never by the phase runs themselves — the test fixture drives
    /// <see cref="RunAsync"/> directly across all four phases and relies on one accumulated outbox.
    /// </remarks>
    public void ResetOutbox()
    {
        try
        {
            if (File.Exists(OutboxPath))
            {
                File.Delete(OutboxPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale outbox is cosmetic; never fail a demo over it.
        }
    }

    /// <summary>The synthetic partner register the faked database tool reads.</summary>
    public PartnerRegister Register { get; }

    /// <summary>The local file the faked email tool appends to. Nothing is ever transmitted.</summary>
    public string OutboxPath { get; }

    /// <summary>The evil-mode setting of the currently connected PartnerIntel session, if any.</summary>
    public bool? ConnectedEvilMode => _partnerIntel is null ? null : _partnerIntelEvil;

    /// <summary>Maps a phase to the two things it changes: the supplier, and the gates.</summary>
    public static (bool EvilMode, GateLevel Level) Configuration(DemoPhase phase) => phase switch
    {
        DemoPhase.Clean => (false, GateLevel.None),
        DemoPhase.Compromised => (true, GateLevel.None),
        DemoPhase.Level1 => (true, GateLevel.ToolContracts),
        DemoPhase.Level2 => (true, GateLevel.ResultAdmissionAndContainment),
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    /// <summary>A short phase title for the banner.</summary>
    public static string Title(DemoPhase phase) => phase switch
    {
        DemoPhase.Clean => "IT WORKS",
        DemoPhase.Compromised => "THE SUPPLIER TURNS",
        DemoPhase.Level1 => "LEVEL 1 — TOOL CONTRACTS",
        DemoPhase.Level2 => "LEVEL 2 — DETECT AND CONTAIN",
        _ => phase.ToString(),
    };

    /// <summary>Runs one phase and returns its recorded outcome.</summary>
    public async Task<PhaseOutcome> RunAsync(
        DemoPhase phase,
        string question,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var (evilMode, level) = Configuration(phase);
        _output.PhaseBanner(((int)phase).ToString(), Title(phase), evilMode, PartnerDeskGates.Describe(level));
        _output.Line();
        _output.Line(ConsoleColor.White, "  Compliance officer:");
        _output.Paragraph(question, indent: "    ");
        _output.Line();

        var session = await EnsurePartnerIntelAsync(evilMode, cancellationToken).ConfigureAwait(false);

        if (level != GateLevel.ResultAdmissionAndContainment)
        {
            return await RunOnceAsync(phase, evilMode, level, session, containment: null, question, isRetry: false, cancellationToken)
                .ConfigureAwait(false);
        }

        using var containment = DemoContainment.Create($"partnerdesk-phase-{(int)phase}");
        var first = await RunOnceAsync(phase, evilMode, level, session, containment, question, isRetry: false, cancellationToken)
            .ConfigureAwait(false);

        if (first.PoisonWithheldAtResultAdmission)
        {
            var mutation = await containment.ContainAsync(
                reasonCode: "poisoned_tool_result",
                evidenceReference: "partnerdesk-result-admission",
                cancellationToken).ConfigureAwait(false);

            _output.Line();
            _output.Line(ConsoleColor.Cyan,
                $"  OPERATOR STEP — containment of {PartnerDeskGates.PartnerIntelServerId}: " +
                $"{mutation.Disposition}, state {mutation.Snapshot.State}");
            _output.Paragraph(
                "Containment enforces a decision; it does not discover the compromise. The result-admission " +
                "finding above is what discovered it. This step writes that finding down as durable state.",
                indent: "    ");
        }
        else
        {
            _output.Line();
            _output.Line(ConsoleColor.Yellow,
                "  OPERATOR STEP skipped: no result-admission finding, so there is nothing to contain.");
        }

        _output.Line();
        _output.Line(ConsoleColor.White, "  Same question again, against the contained source:");
        var retry = await RunOnceAsync(phase, evilMode, level, session, containment, question, isRetry: true, cancellationToken)
            .ConfigureAwait(false);

        _output.Line();
        _output.Line(ConsoleColor.White, "  PartnerDesk, on the retry:");
        _output.Paragraph(
            string.IsNullOrWhiteSpace(retry.AnswerText) ? "(no text)" : retry.AnswerText,
            indent: "    ");

        // Spec §4: name the semantic judge in one sentence, and say why it is not what blocked here.
        _output.Line();
        _output.Line(ConsoleColor.DarkGray,
            "  Note: a semantic indirect-injection judge exists for this too. It stays shadow-only until it");
        _output.Line(ConsoleColor.DarkGray,
            "  clears a task-specific calibration bar, so this demo blocks with the deterministic result gate.");

        return first with { RetryAfterContainment = retry };
    }

    private async Task<PhaseOutcome> RunOnceAsync(
        DemoPhase phase,
        bool evilMode,
        GateLevel level,
        PartnerIntelSession partnerIntel,
        DemoContainment? containment,
        string question,
        bool isRetry,
        CancellationToken cancellationToken)
    {
        var ledger = new ToolEffectLedger();
        var journal = new ToolCallJournal(_output);
        var trace = new AgentTrace { TraceName = $"partnerdesk-phase-{(int)phase}{(isRetry ? "-retry" : string.Empty)}" };

        // Once result admission is installed (Level 2), the poisoned response is withheld from the model, so the
        // narrator announces only that a response arrived rather than dumping its tail to the console.
        var resultGated = level == GateLevel.ResultAdmissionAndContainment;
        AITool[] tools =
        [
            new NarratedMcpTool(partnerIntel.ReportTool, _output, announceOnly: resultGated),
            PartnerDatabaseTool.Create(Register, ledger, _output, _printedRegisterRows),
            EmailTool.Create(ledger, _output, OutboxPath),
        ];

        var chatClient = _chatClientFactory(new PhaseRunContext(phase, isRetry));
        var recording = new RecordingChatClient(chatClient, journal);
        var baseAgent = PartnerDeskAgent.Build(recording, tools);
        var agent = PartnerDeskAgent.ApplyGates(baseAgent, level, journal, containment, trace, tools);

        var agentSession = await agent.CreateSessionAsync().ConfigureAwait(false);
        var response = await agent.RunAsync(question, agentSession, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new PhaseOutcome
        {
            Phase = phase,
            EvilMode = evilMode,
            Level = level,
            Proposals = journal.Proposals,
            Findings = journal.Findings,
            DatabaseReads = ledger.DatabaseReads,
            Emails = ledger.Emails,
            AnswerText = response.Text ?? string.Empty,
            PartnerIntelContainment = containment?.PartnerIntelState,
        };
    }

    /// <summary>
    /// Opens a PartnerIntel session in the requested mode, re-opening the child process when the mode changed.
    /// </summary>
    private async Task<PartnerIntelSession> EnsurePartnerIntelAsync(bool evilMode, CancellationToken cancellationToken)
    {
        if (_partnerIntel is not null && _partnerIntelEvil == evilMode)
        {
            return _partnerIntel;
        }

        if (_partnerIntel is not null)
        {
            await _partnerIntel.DisposeAsync().ConfigureAwait(false);
            _partnerIntel = null;
        }

        _output.Line(ConsoleColor.DarkGray,
            $"  [opening MCP session to {PartnerIntelServer.ServerName} (child process, stdio), " +
            $"addendum {(evilMode ? "ON" : "off")}]");
        _partnerIntel = await PartnerIntelSession.OpenAsync(evilMode, cancellationToken).ConfigureAwait(false);
        _partnerIntelEvil = evilMode;
        return _partnerIntel;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_partnerIntel is not null)
        {
            await _partnerIntel.DisposeAsync().ConfigureAwait(false);
            _partnerIntel = null;
        }
    }
}
