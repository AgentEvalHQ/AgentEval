// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text.Json;
using AgentEval.Prompts;
using Microsoft.Extensions.AI;

namespace AgentEval.RedTeam.Evaluators;

internal delegate bool ConfiguredProbeReader<T>(string? text, JudgePromptDefinition definition, out T value);
internal sealed record ConfiguredProbeReply<T>(bool Parsed, T Value, ProbeJudgeInstrument Instrument, string? Failure);

/// <summary>Shared configured dispatch, retry and observed-model accounting; each role owns its response reader.</summary>
internal static class ConfiguredProbeTransport
{
    internal static async Task<ConfiguredProbeReply<T>> RunAsync<T>(ResolvedJudgePrompt resolved,
        string input, string response, string context, LLMJudgeOptions settings, IChatClient client,
        Func<List<ChatMessage>, Func<ChatOptions, Task<ChatResponse>>, CancellationToken, Task<ChatResponse>> ask,
        ConfiguredProbeReader<T> read, string readerVersion, string evidenceVersion,
        CancellationToken executionToken, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        var definition = resolved.Definition;
        var user = JudgePromptRenderer.Render(definition, input, response, context);
        var provenance = JudgePromptRenderer.Provenance(resolved, user);
        var calls = new List<ProbeJudgeCall>();
        var messages = new List<ChatMessage> { new(ChatRole.System, definition.SystemText), new(ChatRole.User, user) };
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                executionToken.ThrowIfCancellationRequested();
                var reply = await ask(messages, SendAsync, executionToken).ConfigureAwait(false);
                callerToken.ThrowIfCancellationRequested();
                executionToken.ThrowIfCancellationRequested();
                if (read(reply.Text, definition, out var value)) return Attach(true, value, null);
                if (attempt == 0) messages.Add(new(ChatRole.User, definition.RetryText));
            }
            return Failed("invalid after one corrective retry");
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested) { return Failed("execution deadline expired"); }
        catch (Exception ex) { return Failed(ex.GetType().Name); }

        ConfiguredProbeReply<T> Failed(string actual) => Attach(false, default!,
            $"Expected: a complete configured judge response. Actual: {actual}. Suggestions: inspect the selected contract, request settings and provider availability. Because: unavailable output cannot establish the configured result.");

        async Task<ChatResponse> SendAsync(ChatOptions options)
        {
            executionToken.ThrowIfCancellationRequested();
            var index = calls.Count;
            calls.Add(new(null, options.Temperature, options.MaxOutputTokens, false));
            var reply = await client.GetResponseAsync(messages, options, executionToken).ConfigureAwait(false);
            calls[index] = calls[index] with { ModelId = string.IsNullOrWhiteSpace(reply.ModelId) ? null : reply.ModelId, ResponseReceived = true };
            return reply;
        }

        ConfiguredProbeReply<T> Attach(bool parsed, T value, string? failure)
        {
            // Preserve the existing verdict identity shape; observed calls are distinct from case content.
            var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                prompt = provenance.Digest, settings.Temperature, settings.MaxTokens,
                reader = readerVersion, evidence = evidenceVersion,
                effectiveCalls = calls.Where(c => c.ResponseReceived).Select(c => new { c.ModelId, c.Temperature, c.MaxOutputTokens }).Distinct().ToArray()
            }))).ToLowerInvariant();
            return new(parsed, value, new(provenance, digest, calls.AsReadOnly()), failure);
        }
    }
}
