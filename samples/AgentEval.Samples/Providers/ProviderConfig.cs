// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.ClientModel;
using System.Globalization;
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
    public const string BitdeerDefaultEndpoint = "https://api-inference.bitdeer.ai/v1";
    public const string BitdeerDefaultModel = "zai-org/GLM-5.3-Flash";
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
        var client = new OpenAIClient(
            new ApiKeyCredential(key),
            new OpenAIClientOptions { Endpoint = new Uri(BitdeerEndpoint) });
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

    /// <summary>Picks the transport from the keys present (JEV_TRANSPORT breaks a tie), or null when neither key is set.</summary>
    public static SystemOneClientOptions? CreateJevOptions()
    {
        var forced = Env("JEV_TRANSPORT")?.ToLowerInvariant();
        var useTypeSafe = forced switch
        {
            "typesafe" => true,
            "openrouter" => false,
            _ => TypeSafeApiKey is not null,
        };

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

    /// <summary>The HttpClient the Jev sample hands to <see cref="SystemOneDecisionClient"/>; wire-logging when asked.</summary>
    public static HttpClient CreateJevHttpClient()
    {
        HttpMessageHandler handler = ShowRawWire ? new RawWireLoggingHandler(new HttpClientHandler()) : new HttpClientHandler();
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    private sealed class RawWireLoggingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"   ┌─ RAW → {request.Method} {request.RequestUri}   (Authorization header present, not shown)");
            Console.WriteLine($"   │ {body}");

            var response = await base.SendAsync(request, cancellationToken);

            // ReadAsStringAsync buffers the content, so the client's own read afterwards still works.
            var reply = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"   ├─ RAW ← HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            Console.WriteLine($"   │ {reply}");
            Console.WriteLine("   └─");
            Console.ResetColor();
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
