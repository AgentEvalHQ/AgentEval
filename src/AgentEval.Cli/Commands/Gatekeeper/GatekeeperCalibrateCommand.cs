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
        var prompts = new JudgePromptCliOptions(cmd);
        var runtime = new GateRuntimeOptions(cmd);
        var datasetOpt = new Option<string?>("--gold-set") { Description = "Labelled JSON calibration dataset with locale, split and review metadata." };
        cmd.Options.Add(datasetOpt);
        cmd.Options.Add(gateOpt);
        model.AddTo(cmd);
        foreach (var o in new Option[] { maxDangerousOpt, minCasesOpt, maxConcOpt, certifyOpt, certPathOpt, certDirOpt })
        {
            cmd.Options.Add(o);
        }

        cmd.SetAction(async (parse, ct) =>
        {
            AgentEval.Prompts.JudgePromptOptions? selection;
            GateCalibrationDataset? dataset;
            GateRuntimeSettings settings;
            try
            {
                selection = await prompts.Read(parse).LoadAsync(ct);
                settings = runtime.Read(parse);
                _ = settings.Bind(selection, null); // validate before provider resolution
                dataset = GateCalibrationDataset.Load(parse.GetValue(datasetOpt));
            }
            catch (Exception ex) when (JudgePromptCliOptions.IsConfigurationError(ex))
            { Console.Error.WriteLine(ex.Message); return ExitCodes.UsageError; }
            var (azure, endpoint, deployment, modelName, apiKey) = model.Read(parse);
            var mr = GatekeeperModelResolver.Resolve(azure, endpoint, deployment, modelName, apiKey, Console.Error);
            if (mr.Client is null)
            {
                return mr.ExitCode;
            }
            using var client = mr.Client;

            return await RunAsync(parse.GetValue(gateOpt)!, client, mr.Fingerprint!,
                parse.GetValue(maxDangerousOpt), parse.GetValue(minCasesOpt), parse.GetValue(maxConcOpt),
                parse.GetValue(certifyOpt), parse.GetValue(certPathOpt), parse.GetValue(certDirOpt), Console.Out, Console.Error, ct,
                settings.Bind(selection, mr.RequestedModelId), dataset);
        });

        return cmd;
    }

    /// <summary>Testable core (model client + fingerprint supplied by the caller).</summary>
    public static async Task<int> RunAsync(
        string gate, IChatClient client, string fingerprint, int maxDangerousErrors, int minCasesPerDirection,
        int maxConcurrency, bool certify, string? certPath, string? certDir, TextWriter stdout, TextWriter stderr, CancellationToken ct,
        JudgeGateOptions? judgeOptions = null, GateCalibrationDataset? dataset = null)
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

        JudgeGoldSet gold;
        AgentEval.Guardrails.IChatGate judge;
        try
        {
            dataset?.Validate(axis);
            gold = dataset?.GoldSet() ?? entry.GoldSet();
            judge = entry.Create(client, false, judgeOptions); // no allow-cache in calibration
        }
        catch (Exception ex) when (JudgePromptCliOptions.IsConfigurationError(ex))
        { stderr.WriteLine(ex.Message); return ExitCodes.UsageError; }
        CalibrationReport report;
        var baseline = entry.KeywordBaseline();
        try
        {
            report = await GateCalibrationHarness.EvaluateAsync(judge, gold, new CalibrationOptions
            {
                DeterministicBaseline = baseline,
                MaxDangerousErrors = maxDangerousErrors,
                MinCasesPerDirection = minCasesPerDirection,
                MaxConcurrency = maxConcurrency,
                SplitLabel = dataset?.SplitLabel,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stderr.WriteLine($"  Error during calibration: {ex.Message}");
            return ExitCodes.RuntimeError;
        }

        var instrument = InlineReadinessStore.InstrumentOf(judge);
        var needsReview = instrument is not null || dataset is not null;
        var certificateEligible = report.IsInlineReady && (!needsReview || dataset is { Reviewed: true, SplitLabel: "held-out" });
        var dto = CalibrationReportDto.From(report, redactCaseText: GateVerdictDto.RedactAxes.Contains(axis) ||
            instrument is not null && judgeOptions?.JudgePrompts?.CaptureContent != true) with
        {
            Dataset = dataset?.Provenance, CertificateEligible = certificateEligible,
            CalibrationPolicy = new(maxDangerousErrors, minCasesPerDirection, maxConcurrency, baseline.PolicyName, "en")
        };
        stdout.WriteLine(dto.ToJson());
        if (needsReview && !certificateEligible)
            stderr.WriteLine("Configured/custom calibration is diagnostic until a reviewed held-out dataset and all readiness checks pass. Built-in keyword baselines remain English; no customer accuracy is implied.");

        if (certify)
        {
            var cert = new CalibrationCertificate(axis, fingerprint, InlineReadinessStore.GoldSetHash(gold),
                certificateEligible, DateTime.UtcNow.ToString("o"), dto, instrument, dataset?.Provenance);
            var written = InlineReadinessStore.Save(cert, certPath, certDir);
            stderr.WriteLine($"  certificate written: {written} (isInlineReady={certificateEligible})");
        }

        return certificateEligible ? ExitCodes.Success : ExitCodes.TestFailure;   // 0 / 1
    }
}
