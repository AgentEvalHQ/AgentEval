// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Runtime.CompilerServices;
using AgentEval.Models;
using AgentEval.Results.Writing;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Invocation-scoped observed usage. Missing reports never become zero or guessed bounds.</summary>
internal sealed class CliUsageMeter : IDisposable
{
    private static readonly AsyncLocal<CliUsageMeter?> Active = new();
    private static readonly AsyncLocal<Attribution?> AttributionContext = new();
    private readonly CliUsageMeter? _previous;
    private readonly object _sync = new();
    private readonly List<Call> _calls = [];
    private bool _disposed;

    private CliUsageMeter() { _previous = Active.Value; Active.Value = this; }
    internal static CliUsageMeter Begin() => new();
    internal static IReadOnlyList<AefUsage>? CurrentUsage => Active.Value?.Snapshot();
    internal static IReadOnlyList<AefUsage>? ForCase(string caseId) => Active.Value?.Snapshot(caseId);
    internal static IChatClient Wrap(IChatClient client, string? role, string? model = null) =>
        Active.Value is { } meter ? new MeteredClient(client, meter, role switch
        {
            "agent" or "sut" or "subject" or "auto-audit" or "replay-target" or "gatekeeper demo agent" => AefUsageRole.Agent,
            "judge" => AefUsageRole.Judge,
            "attacker" => AefUsageRole.Attacker,
            _ => AefUsageRole.Other,
        }, model) : client;

    /// <summary>Attributes calls in this asynchronous flow; nested scopes restore their parent.</summary>
    internal static IDisposable Attribute(AefUsageRole? role = null, string? caseId = null)
    {
        var previous = AttributionContext.Value;
        var parent = previous?.Meter == Active.Value ? previous : null;
        AttributionContext.Value = new(Active.Value, role ?? parent?.Role, caseId ?? parent?.CaseId);
        return new RestoreAttribution(previous);
    }

    private sealed record Attribution(CliUsageMeter? Meter, AefUsageRole? Role, string? CaseId);
    private sealed class RestoreAttribution(Attribution? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AttributionContext.Value = previous;
        }
    }

    internal IReadOnlyList<AefUsage>? Snapshot(string? caseId = null)
    {
        lock (_sync)
        {
            var totals = _calls.Where(c => caseId is null || c.CaseId == caseId).GroupBy(c => (c.Role, c.Model)).Select(g => new AefUsage
            {
                Role = g.Key.Role, Model = g.Key.Model,
                InputTokens = Sum(g.Select(c => c.Input)), OutputTokens = Sum(g.Select(c => c.Output)),
                CostUsd = g.All(c => c.Cost is not null) ? (double?)g.Sum(c => (double)c.Cost!.Value) : null,
                CostSource = g.All(c => c.Cost is not null) ? "ModelPricing configured rates; baseline " + ModelPricing.TableName : null,
            }).ToArray();
            return totals.Length == 0 ? null : totals;
        }
    }

    private static long? Sum(IEnumerable<long?> values)
    {
        long sum = 0;
        foreach (var value in values)
        {
            if (value is null or < 0 || value > 9007199254740991L - sum) return null;
            sum += value.Value;
        }
        return sum;
    }

    private Call Start(AefUsageRole role, string? model)
    {
        var attribution = AttributionContext.Value;
        if (attribution?.Meter != this) attribution = null;
        var call = new Call { Role = attribution?.Role ?? role, Model = model, CaseId = attribution?.CaseId };
        lock (_sync) _calls.Add(call);
        return call;
    }

    private void Finish(Call call, string? model, long? input, long? output)
    {
        lock (_sync)
        {
            call.Model = model ?? call.Model;
            call.Input = input is >= 0 ? input : null;
            call.Output = output is >= 0 ? output : null;
            if (call.Input is { } i && call.Output is { } o && ModelPricing.GetPricing(call.Model) is { } rates
                && rates.InputPer1K >= 0 && rates.OutputPer1K >= 0)
            {
                try { call.Cost = checked(i / 1000m * rates.InputPer1K + o / 1000m * rates.OutputPer1K); }
                catch (OverflowException) { call.Cost = null; }
            }
        }
    }

    private void ObserveModel(Call call, string? model)
    {
        if (model is null) return;
        lock (_sync) call.Model = model;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Active.Value = _previous;
    }

    private sealed class Call
    {
        internal AefUsageRole Role;
        internal string? Model;
        internal string? CaseId;
        internal long? Input, Output;
        internal decimal? Cost;
    }

    private sealed class MeteredClient(IChatClient inner, CliUsageMeter meter, AefUsageRole role, string? model)
        : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var call = meter.Start(role, options?.ModelId ?? model);
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            meter.Finish(call, response.ModelId, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = meter.Start(role, options?.ModelId ?? model);
            long? input = null, output = null;
            bool invalidInput = false, invalidOutput = false;
            string? reportedModel = null;
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                reportedModel = update.ModelId ?? reportedModel;
                // Model identity is observed even when enumeration later fails or is abandoned.
                meter.ObserveModel(call, update.ModelId);
                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    if (usage.Details.InputTokenCount is { } i)
                    {
                        input = Sum([input ?? 0, i]); invalidInput |= input is null;
                    }
                    if (usage.Details.OutputTokenCount is { } o)
                    {
                        output = Sum([output ?? 0, o]); invalidOutput |= output is null;
                    }
                }
                yield return update;
            }
            // Only completed enumeration supplies a total; throwing or early disposal leaves the call unknown.
            meter.Finish(call, reportedModel, invalidInput ? null : input, invalidOutput ? null : output);
        }
    }
}
