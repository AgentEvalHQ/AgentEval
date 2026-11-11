// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
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
    private readonly ModelPricingSnapshot _pricing = ModelPricing.CaptureSnapshot();
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

    internal static IEmbeddingGenerator<string, Embedding<float>> WrapEmbeddings(
        IEmbeddingGenerator<string, Embedding<float>> generator, string? model) =>
        Active.Value is { } meter ? new MeteredEmbeddings(generator, meter, model) : generator;

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
                CacheReadInputTokens = Sum(g.Select(c => c.Estimate?.CachedInputTokens is >= 0 and var count && count <= c.Input ? (long?)count : null)),
                CostUsd = g.All(c => c.Cost is not null) ? (double?)g.Sum(c => (double)c.Cost!.Value) : null,
                CostSource = g.All(c => c.Cost is not null) ? DescribePricing(g) : null,
            }).ToArray();
            return totals.Length == 0 ? null : totals;
        }
    }

    private static string DescribePricing(IEnumerable<Call> calls)
    {
        var estimates = calls.Select(c => c.Estimate!).ToArray();
        var price = estimates[0].Pricing;
        var statuses = estimates.Select(e => e.Status).Distinct().ToArray();
        var status = statuses.Length == 1 ? statuses[0].ToString() : "MixedCacheAssumptions";
        // AEF costSource is a 128-character summary, not the rate-card document. Full
        // rate provenance belongs to CostEstimate; never copy an arbitrary URL here.
        var summary = FormattableString.Invariant($"AgentEval; {status}; USD/1K input={price?.InputPer1K}, output={price?.OutputPer1K}, cached={price?.CachedInputPer1K}");
        if (summary.Length <= 128) return summary;
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(price))).ToLowerInvariant();
        return $"AgentEval; {status}; rates=sha256:{digest}";
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

    private void Finish(Call call, string? model, long? input, long? output, long? cachedInput = null, bool mixedModels = false)
    {
        lock (_sync)
        {
            call.Model = !string.IsNullOrWhiteSpace(model) ? model : call.Model;
            call.Input = input is >= 0 ? input : null;
            call.Output = output is >= 0 ? output : null;
            call.Estimate = TokenCostEstimate.Calculate(input, output, cachedInput,
                mixedModels ? null : _pricing.GetTokenPricing(call.Model)) with
            {
                ModelId = call.Model, ModelSource = mixedModels ? "mixed" : !string.IsNullOrWhiteSpace(model) ? "response" : "configured"
            };
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
        internal TokenCostEstimate? Estimate;
        internal decimal? Cost => Estimate?.Amount;
    }

    private sealed class MeteredClient(IChatClient inner, CliUsageMeter meter, AefUsageRole role, string? model)
        : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var call = meter.Start(role, options?.ModelId ?? model);
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            meter.Finish(call, response.ModelId, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.CachedInputTokenCount);
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = meter.Start(role, options?.ModelId ?? model);
            long? input = null, output = null, cachedInput = 0;
            var mixedModels = false;
            var invalidCache = false;
            bool invalidInput = false, invalidOutput = false;
            string? reportedModel = null;
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(update.ModelId))
                {
                    mixedModels |= reportedModel is not null && !StringComparer.OrdinalIgnoreCase.Equals(reportedModel, update.ModelId);
                    reportedModel = update.ModelId;
                }
                // Model identity is observed even when enumeration later fails or is abandoned.
                meter.ObserveModel(call, update.ModelId);
                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    if (usage.Details.InputTokenCount is { } i)
                    {
                        input = Sum([input ?? 0, i]); invalidInput |= input is null;
                        invalidCache |= usage.Details.CachedInputTokenCount < 0 || usage.Details.CachedInputTokenCount > i;
                        var contribution = usage.Details.CachedInputTokenCount;
                        if (cachedInput is { } total && contribution is { } count && count > 9007199254740991L - total) invalidCache = true;
                        cachedInput = Sum([cachedInput, contribution]);
                    }
                    if (usage.Details.InputTokenCount is null && usage.Details.CachedInputTokenCount is { } separateCache)
                    {
                        invalidCache |= separateCache < 0 || cachedInput is { } total && separateCache > 9007199254740991L - total;
                        cachedInput = Sum([cachedInput, separateCache]);
                    }
                    if (usage.Details.OutputTokenCount is { } o)
                    {
                        output = Sum([output ?? 0, o]); invalidOutput |= output is null;
                    }
                }
                yield return update;
            }
            // Only completed enumeration supplies a total; throwing or early disposal leaves the call unknown.
            meter.Finish(call, reportedModel, invalidInput ? null : input, invalidOutput ? null : output,
                invalidCache ? -1 : cachedInput, mixedModels);
        }
    }

    private sealed class MeteredEmbeddings(IEmbeddingGenerator<string, Embedding<float>> inner,
        CliUsageMeter meter, string? model) : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
    {
        public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            // This is a scoring instrument, not an LLM judge. Retain the enclosing case, not its role.
            using var attribution = Attribute(AefUsageRole.Other);
            var call = meter.Start(AefUsageRole.Other, options?.ModelId ?? model);
            var response = await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
            // Keep absent SDK counts unknown, even when the provider normally charges only for input.
            meter.Finish(call, null, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.CachedInputTokenCount);
            return response;
        }
    }
}
