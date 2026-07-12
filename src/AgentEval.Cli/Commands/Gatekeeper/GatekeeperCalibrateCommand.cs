// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Guardrails.Judges;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>
/// <c>agenteval gatekeeper calibrate --gate judge:&lt;axis&gt; --model …</c> — score a judge against its gold set +
/// keyword-oracle baseline (at a zero-missed-attacks bar) and print the <see cref="CalibrationReport"/>. With
/// <c>--certify</c>, write the certificate the <c>inspect</c> honesty guard reads. Exit 0 if inline-ready, else 1
/// (CI-gate friendly). Honors <c>--min-cases-per-direction</c> / <c>--max-concurrency</c> by calling the harness
/// directly (the bundle's CalibrateAsync would silently drop them).
/// </summary>
internal static class GatekeeperCalibrateCommand
{
    public static Command Create()
    {
        var gateOpt = new Option<string?>("--gate") { Description = "judge:<axis> (only judge axes are calibratable)", Required = true };
        var model = new ModelOptions();
        var maxDangerousOpt = new Option<int>("--max-dangerous-errors") { Description = "Max missed attacks (default 0 = zero-miss)", DefaultValueFactory = _ => 0 };
        var minCasesOpt = new Option<int>("--min-cases-per-direction") { Description = "Min gold cases per direction (default 20)", DefaultValueFactory = _ => 20 };
        var maxConcOpt = new Option<int>("--max-concurrency") { Description = "Max concurrent judge calls (default 4)", DefaultValueFactory = _ => 4 };
        var certifyOpt = new Option<bool>("--certify") { Description = "Write a calibration certificate the inspect honesty-guard reads" };
        var certPathOpt = new Option<string?>("--cert-path") { Description = "Exact certificate output file (overrides --cert-dir)" };
        var certDirOpt = new Option<string?>("--cert-dir") { Description = "Directory to write the certificate into (default .agenteval/gatekeeper/certs/); read back with inspect --cert-dir" };

        var cmd = new Command("calibrate", "Calibrate a judge axis against its gold set + baseline; optionally certify it.");
        cmd.Options.Add(gateOpt);
        model.AddTo(cmd);
        foreach (var o in new Option[] { maxDangerousOpt, minCasesOpt, maxConcOpt, certifyOpt, certPathOpt, certDirOpt })
        {
            cmd.Options.Add(o);
        }

        cmd.SetAction(async (parse, ct) =>
        {
            var (azure, endpoint, deployment, modelName, apiKey) = model.Read(parse);
            var mr = GatekeeperModelResolver.Resolve(azure, endpoint, deployment, modelName, apiKey, Console.Error);
            if (mr.Client is null)
            {
                return mr.ExitCode;
            }

            return await RunAsync(parse.GetValue(gateOpt)!, mr.Client, mr.Fingerprint!,
                parse.GetValue(maxDangerousOpt), parse.GetValue(minCasesOpt), parse.GetValue(maxConcOpt),
                parse.GetValue(certifyOpt), parse.GetValue(certPathOpt), parse.GetValue(certDirOpt), Console.Out, Console.Error, ct);
        });

        return cmd;
    }

    /// <summary>Testable core (model client + fingerprint supplied by the caller).</summary>
    public static async Task<int> RunAsync(
        string gate, IChatClient client, string fingerprint, int maxDangerousErrors, int minCasesPerDirection,
        int maxConcurrency, bool certify, string? certPath, string? certDir, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (!gate.StartsWith("judge:", StringComparison.Ordinal))
        {
            stderr.WriteLine("  Error: calibrate requires --gate judge:<axis>.");
            return ExitCodes.UsageError;
        }

        var axis = gate["judge:".Length..];
        var entry = JudgeAxisRegistry.For(axis);
        if (entry is null)
        {
            stderr.WriteLine($"  Error: unknown judge axis '{axis}'.");
            return ExitCodes.UsageError;
        }

        var gold = entry.GoldSet();
        CalibrationReport report;
        try
        {
            var judge = entry.Create(client, false);   // cache:false — measure the model, not a cached allow
            report = await GateCalibrationHarness.EvaluateAsync(judge, gold, new CalibrationOptions
            {
                DeterministicBaseline = entry.KeywordBaseline(),
                MaxDangerousErrors = maxDangerousErrors,
                MinCasesPerDirection = minCasesPerDirection,
                MaxConcurrency = maxConcurrency,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stderr.WriteLine($"  Error during calibration: {ex.Message}");
            return ExitCodes.RuntimeError;
        }

        var dto = CalibrationReportDto.From(report, redactCaseText: GateVerdictDto.RedactAxes.Contains(axis));
        stdout.WriteLine(dto.ToJson());

        if (certify)
        {
            var cert = new CalibrationCertificate(axis, fingerprint, InlineReadinessStore.GoldSetHash(gold),
                report.IsInlineReady, DateTime.UtcNow.ToString("o"), dto);
            var written = InlineReadinessStore.Save(cert, certPath, certDir);
            stderr.WriteLine($"  certificate written: {written} (isInlineReady={report.IsInlineReady})");
        }

        return report.IsInlineReady ? ExitCodes.Success : ExitCodes.TestFailure;   // 0 / 1
    }
}
