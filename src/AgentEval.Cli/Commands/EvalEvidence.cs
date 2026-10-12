// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

/// <summary>Incremental CLI evidence with the existing explicit-error/default-warning policy.</summary>
internal sealed class EvalEvidence : IDisposable
{
    private readonly EvalOptions _options;
    private readonly Func<AefUsage?> _judgeUsage;
    private AefEvalWriter.Session? _session;
    private bool _failed;

    public EvalEvidence(EvalOptions options, Func<AefEvalRunOptions> runOptions, Func<AefUsage?> judgeUsage,
        DateTimeOffset startedAt, int? plannedTrials = null, int? requirePasses = null)
    {
        _options = options;
        _judgeUsage = judgeUsage;
        var root = EvalAef.Root(options);
        if (root is null)
        {
            if (!options.NoAef && !options.Quiet)
                Console.Error.WriteLine("  AEF: no .agenteval workspace here, so no run was written (--aef <dir> writes one anywhere).");
            return;
        }
        try { _session = AefEvalWriter.Start(Path.Combine(root, "runs"), runOptions(), startedAt, plannedTrials, requirePasses); }
        catch (Exception ex) when (IsWriteFailure(ex)) { Failed(ex); }
    }

    public void Add(TestResult result, string caseId, int? passingScore = null, AefNativeContent? content = null)
    {
        try { _session?.Add(result, caseId, passingScore, content); }
        catch (Exception ex) when (IsWriteFailure(ex)) { Failed(ex); }
    }

    public bool Complete(AefCommandGate? gate = null, string? abortReason = null)
    {
        try
        {
            if (_session is not null)
            {
                var (directory, hash) = _session.Complete(DateTimeOffset.UtcNow, gate, abortReason, _judgeUsage());
                if (!_options.Quiet) Console.Error.WriteLine($"  AEF run: {directory} ({(hash is null ? "unsealed" : $"run hash {hash[..12]}…")})");
                _session.Dispose();
                _session = null;
            }
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { Failed(ex); }
        return _failed;
    }

    public void AddMetrics(string caseId, IReadOnlyList<AgentEval.Core.MetricResult> metrics)
    {
        try { _session?.AddMetrics(caseId, metrics); }
        catch (Exception ex) when (IsWriteFailure(ex)) { Failed(ex); }
    }

    private static bool IsWriteFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException;

    private void Failed(Exception ex)
    {
        _failed |= _options.Aef is not null;
        if (_failed || !_options.Quiet)
            Console.Error.WriteLine($"  {(_failed ? "Error" : "Warning")}: the AEF run {(_session is null ? "was not written" : "could not be completed")}: {ex.Message}");
        _session?.Dispose();
        _session = null;
    }

    public void Dispose() => _session?.Dispose();
}
