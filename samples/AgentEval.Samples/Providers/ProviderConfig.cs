// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using AgentEval.Core;
using AgentEval.Decisions;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentEval.Samples.Providers;

/// <summary>
/// Configuration for group N (Bitdeer-hosted GLM-5.3 Flash and TypeSafe Jev). Everything is an
/// environment variable; nothing here is persisted, and no key is ever printed.
///
/// Bitdeer:
///   BITDEER_API_KEY              required
///   BITDEER_ENDPOINT             default https://api-inference.bitdeer.ai/v1
///   BITDEER_MODEL                default zai-org/GLM-5.3-Flash
///   BITDEER_PRICE_INPUT_PER_1M   optional, USD — Bitdeer's list price was not verifiable from
///   BITDEER_PRICE_OUTPUT_PER_1M  their public pages on 2026-09-20, so it is not hard-coded here
///
/// Jev (either key selects its transport; JEV_TRANSPORT forces one when both are set):
///   TYPESAFE_API_KEY             TypeSafe direct — https://api.typesafe.ai/v1/systemone
///   OPENROUTER_API_KEY           OpenRouter relay — https://openrouter.ai/api/v1/systemone
///   JEV_TRANSPORT                "typesafe" | "openrouter"
///   JEV_MODEL                    overrides the transport's default model id
///
/// Dry run (both samples):
///   --dry-run on the command line, or AGENTEVAL_SAMPLES_DRY_RUN=1 — prints every payload that
///   WOULD be sent, through the real serializer, and sends nothing.
/// </summary>
internal static class ProviderConfig
{
    public const string BitdeerDefaultEndpoint = AIConfig.BitdeerDefaultEndpoint;
    public const string BitdeerDefaultModel = AIConfig.BitdeerDefaultModel;
    public const string BitdeerProviderTag = "bitdeer";

    // ── Bitdeer ──────────────────────────────────────────────────────────────

    public static string? BitdeerApiKey => Env("BITDEER_API_KEY");
    public static string BitdeerEndpoint => Env("BITDEER_ENDPOINT") ?? BitdeerDefaultEndpoint;
    public static string BitdeerModel => Env("BITDEER_MODEL") ?? BitdeerDefaultModel;
    public static bool IsBitdeerConfigured => BitdeerApiKey is not null;

    /// <summary>The benchmark identity: model AND provider, because serving changes what a model does.</summary>
    public static string BitdeerAgentName => $"{BitdeerModel}@{BitdeerProviderTag}";

    /// <summary>Per-1K rates from BITDEER_PRICE_*_PER_1M, or null when the run is not priced.</summary>
    public static (double InputPer1K, double OutputPer1K)? BitdeerPricePer1K
    {
        get
        {
            var input = ParseUsd(Env("BITDEER_PRICE_INPUT_PER_1M"));
            var output = ParseUsd(Env("BITDEER_PRICE_OUTPUT_PER_1M"));
            return input is null || output is null ? null : (input.Value / 1000.0, output.Value / 1000.0);
        }
    }

    /// <summary>
    /// The same construction the CLI's <c>EndpointFactory.CreateOpenAICompatible</c> uses for
    /// <c>--endpoint --model --api-key</c>: an OpenAI client pointed at a different base URL. There is
    /// no Bitdeer-specific code anywhere in AgentEval, and there does not need to be.
    /// </summary>
    public static IChatClient CreateBitdeerChatClient()
    {
        var key = BitdeerApiKey ?? throw new InvalidOperationException("BITDEER_API_KEY is not set.");
        // BITDEER_ENDPOINT is user-controlled and will carry the key: https only (loopback http for a local proxy).
        if (!AgentEval.Providers.InferenceProviderEnvironment.TryValidateEndpoint(BitdeerEndpoint, out var endpoint, out var why))
            throw new InvalidOperationException($"BITDEER_ENDPOINT='{BitdeerEndpoint}' {why}");
        // AGENTEVAL_SAMPLES_SHOW_RAW=1 prints this client's requests and replies too, through the same logger as Jev's.
        var client = new OpenAIClient(
            new ApiKeyCredential(key),
            new OpenAIClientOptions { Endpoint = endpoint, Transport = new HttpClientPipelineTransport(CreateWireLoggedHttpClient(key)) });
        return client.GetChatClient(BitdeerModel).AsIChatClient();
    }

    public static void PrintMissingBitdeerWarning()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ⚠️  BITDEER_API_KEY is not set — this sample makes REAL calls only   ║");
        Console.WriteLine("║                                                                      ║");
        Console.WriteLine("║  Set:  BITDEER_API_KEY = <key from Bitdeer AI Model Studio>          ║");
        Console.WriteLine("║  Optional: BITDEER_MODEL, BITDEER_ENDPOINT, BITDEER_PRICE_*_PER_1M   ║");
        Console.WriteLine("║                                                                      ║");
        Console.WriteLine("║  There is no mock path. A canned reply would measure nothing.        ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    // ── Jev ──────────────────────────────────────────────────────────────────

    public static string? TypeSafeApiKey => Env("TYPESAFE_API_KEY");
    public static string? OpenRouterApiKey => Env("OPENROUTER_API_KEY");
    public static string? JevModelOverride => Env("JEV_MODEL");
    public static bool IsJevConfigured => TypeSafeApiKey is not null || OpenRouterApiKey is not null;

    /// <summary>
    /// Picks the transport from the keys present (JEV_TRANSPORT breaks a tie), or null when neither key
    /// is set. An unrecognised JEV_TRANSPORT value selects NOTHING and says why — a typo must not route a
    /// paid call to a provider the user did not choose.
    /// </summary>
    public static SystemOneClientOptions? CreateJevOptions()
    {
        var forced = Env("JEV_TRANSPORT")?.ToLowerInvariant();
        bool useTypeSafe;
        switch (forced)
        {
            case null: useTypeSafe = TypeSafeApiKey is not null; break;
            case "typesafe": useTypeSafe = true; break;
            case "openrouter": useTypeSafe = false; break;
            default:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"   ⚠️  JEV_TRANSPORT='{forced}' is not one of typesafe | openrouter — no transport selected.");
                Console.ResetColor();
                return null;
        }

        if (useTypeSafe && TypeSafeApiKey is { } ts)
            return JevModelOverride is { } m1 ? SystemOneClientOptions.ForTypeSafe(ts, m1) : SystemOneClientOptions.ForTypeSafe(ts);
        if (!useTypeSafe && OpenRouterApiKey is { } or)
            return JevModelOverride is { } m2 ? SystemOneClientOptions.ForOpenRouter(or, m2) : SystemOneClientOptions.ForOpenRouter(or);
        return null;
    }

    public static void PrintMissingJevWarning()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ⚠️  No Jev key — this sample makes REAL decision calls only          ║");
        Console.WriteLine("║                                                                      ║");
        Console.WriteLine("║  Set ONE of:                                                         ║");
        Console.WriteLine("║    TYPESAFE_API_KEY   = <key>   → api.typesafe.ai  (direct)          ║");
        Console.WriteLine("║    OPENROUTER_API_KEY = <key>   → openrouter.ai    (relay)           ║");
        Console.WriteLine("║  Optional: JEV_TRANSPORT=typesafe|openrouter, JEV_MODEL=<id>         ║");
        Console.WriteLine("║                                                                      ║");
        Console.WriteLine("║  There is no mock path. A canned probability would measure nothing.  ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// AGENTEVAL_SAMPLES_SHOW_RAW=1 prints the exact request body and the exact response body of every
    /// Jev call — the evidence ADR-033 §7 asks for. The Authorization header is never printed.
    /// </summary>
    public static bool ShowRawWire =>
        Env("AGENTEVAL_SAMPLES_SHOW_RAW") is { } v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The HttpClient the Jev sample hands to <see cref="SystemOneDecisionClient"/>; wire-logging when
    /// asked. The logger is told the key so it can scrub it from anything it prints — a provider or
    /// proxy that echoes the request headers must not put the bearer on the console.
    /// </summary>
    public static HttpClient CreateJevHttpClient(SystemOneClientOptions options) => CreateWireLoggedHttpClient(options.ApiKey);

    /// <summary>
    /// An HttpClient that prints every request and reply body when AGENTEVAL_SAMPLES_SHOW_RAW=1 and is a plain
    /// client otherwise. Used for the Jev transport and, through <c>AIConfig.CreateChatClient</c>, for the chat
    /// provider's SDK transport — so the wire evidence ADR-033 §7 asks for exists for EVERY provider, not one.
    /// <paramref name="secret"/> is scrubbed from anything printed.
    /// </summary>
    public static HttpClient CreateWireLoggedHttpClient(string secret)
    {
        HttpMessageHandler handler = ShowRawWire ? new RawWireLoggingHandler(new HttpClientHandler(), secret) : new HttpClientHandler();
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    }

    private sealed class RawWireLoggingHandler(HttpMessageHandler inner, string secret) : DelegatingHandler(inner)
    {
        private string Scrub(string text) => text.Replace(secret, "[redacted]", StringComparison.Ordinal);

        private static readonly object s_console = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // One block per call, printed atomically AFTER the reply. A composite evaluates its leaves concurrently,
            // and printing the request before the await let another call's reply land under this call's request.
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var block = new System.Text.StringBuilder();
            // The URI is user-configured (BITDEER_ENDPOINT, OPENAI_COMPATIBLE_ENDPOINT) and could carry the key; scrub it too.
            block.AppendLine($"   ┌─ RAW → {request.Method} {Scrub(request.RequestUri?.ToString() ?? "")}   (Authorization header present, not shown)");
            block.AppendLine($"   │ {Scrub(body)}");

            var response = await base.SendAsync(request, cancellationToken);

            // A streaming reply (SSE) must not be buffered: the samples that measure time-to-first-token would
            // see the whole response arrive at once. The stream passes through untouched and is not shown.
            var streaming = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)
                || body.Contains("\"stream\":true", StringComparison.OrdinalIgnoreCase);
            string reply;
            if (streaming)
            {
                reply = "[streaming reply — passed through unbuffered, not shown]";
            }
            else
            {
                // Buffer with the SAME cap the client enforces, so a debugging aid cannot be the way an
                // oversized body gets into memory; the buffered content stays readable for the client.
                try
                {
                    await response.Content.LoadIntoBufferAsync(SystemOneDecisionClient.MaxResponseBytes, cancellationToken);
                    reply = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                catch (HttpRequestException)
                {
                    reply = $"[body larger than {SystemOneDecisionClient.MaxResponseBytes:N0} bytes — not shown; the client will refuse it]";
                }
            }
            block.AppendLine($"   ├─ RAW ← HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            block.AppendLine($"   │ {Scrub(reply)}");
            block.AppendLine("   └─");
            lock (s_console)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(block.ToString());
                Console.ResetColor();
            }
            return response;
        }
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    public static bool IsDryRun =>
        Env("AGENTEVAL_SAMPLES_DRY_RUN") is { } v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    public static void PrintDryRunBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   ── DRY RUN: every payload below is rendered through the real serializer; NOTHING is sent. ──");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static double? ParseUsd(string? s) =>
        s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : null;
}
