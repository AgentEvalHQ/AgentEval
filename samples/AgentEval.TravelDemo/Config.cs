// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentEval.TravelDemo;

/// <summary>
/// The model provider the TravelDemo agents (and the TravelDemo.Evals judges) run on, as resolved by
/// <see cref="Config.Resolve"/>. Carries the key, so <see cref="ToString"/> redacts it.
/// </summary>
/// <param name="ProviderTag"><c>azure</c>, <c>bitdeer</c>, <c>openai</c>, <c>foundry</c>, <c>openai-compatible</c>, or <c>none</c>.</param>
/// <param name="DisplayName">Human-readable provider name for banners.</param>
/// <param name="Endpoint">Base URL for the OpenAI-protocol hosts, or the resource endpoint for the Azure-protocol hosts.</param>
/// <param name="EndpointSource">The variable the endpoint was read from, or <c>default</c> for a provider's published endpoint.</param>
/// <param name="ApiKey">The API key. Never log it.</param>
/// <param name="Model">The model from the environment or the provider's default — WITHOUT <see cref="Config.ModelOverride"/>.</param>
/// <param name="ModelSource">Where <paramref name="Model"/> came from, for the banner.</param>
/// <param name="IsExplicit">True when <c>AI_INFERENCE_PROVIDER</c> named the provider; false when it was auto-detected.</param>
/// <param name="Diagnostic">Why nothing is configured, when nothing is.</param>
public sealed record TravelProviderSettings(
    string ProviderTag,
    string DisplayName,
    Uri? Endpoint,
    string? EndpointSource,
    string? ApiKey,
    string? Model,
    string? ModelSource,
    bool IsExplicit,
    string? Diagnostic)
{
    /// <summary>True when a provider with credentials is selected.</summary>
    public bool IsConfigured => ProviderTag != "none";

    /// <summary>True for the hosts that speak the Azure OpenAI protocol (Azure OpenAI, Foundry).</summary>
    public bool UsesAzureProtocol => ProviderTag is "azure" or "foundry";

    /// <summary>Never prints the key — a positional record's generated <c>ToString()</c> would.</summary>
    public override string ToString() =>
        $"{nameof(TravelProviderSettings)} {{ ProviderTag = {ProviderTag}, Endpoint = {Endpoint}, " +
        $"ApiKey = {(ApiKey is null ? "null" : "[redacted]")}, Model = {Model}, IsExplicit = {IsExplicit}, Diagnostic = {Diagnostic} }}";
}

/// <summary>
/// Model-provider configuration for TravelDemo + TravelDemo.Evals, sourced from environment variables.
/// </summary>
/// <remarks>
/// <para>
/// The selector is <c>AI_INFERENCE_PROVIDER</c>, with the same variables, defaults and auto-detection
/// order as AgentEval's <c>InferenceProviderEnvironment</c> (<c>src/AgentEval.Core/Providers</c>). This
/// project deliberately does not reference AgentEval, so the rules below are a small copy;
/// TravelDemo.Evals, which does reference AgentEval, compares the two at startup and stops if they disagree.
/// </para>
/// <code>
/// AI_INFERENCE_PROVIDER = azure | bitdeer | openai | foundry | openai-compatible
///
/// azure               AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY   (AZURE_OPENAI_DEPLOYMENT optional, default gpt-5-mini;
///                     auto-detected without it only when no other provider has its variables)
/// bitdeer             BITDEER_API_KEY            (BITDEER_ENDPOINT, BITDEER_MODEL default to Bitdeer's GLM-5.3 Flash)
/// openai              OPENAI_API_KEY             (OPENAI_BASE_URL, OPENAI_MODEL default to api.openai.com, gpt-4o-mini)
/// foundry             FOUNDRY_ENDPOINT + FOUNDRY_API_KEY + FOUNDRY_MODEL   (a Foundry resource's Azure OpenAI-compatible endpoint)
/// openai-compatible   OPENAI_COMPATIBLE_ENDPOINT + OPENAI_COMPATIBLE_MODEL   (OPENAI_COMPATIBLE_API_KEY optional)
/// </code>
/// <para>
/// When the selector is unset, the first provider with credentials wins: Azure, Bitdeer, OpenAI, Foundry,
/// then the generic endpoint (an Azure setup without a deployment only after all of them — see below).
/// When it names a provider whose variables are missing, or something unknown,
/// nothing is selected and <see cref="Diagnostic"/> says why — it never falls back to a host you did not choose.
/// </para>
/// <para>
/// One difference from Core is deliberate: an Azure setup may omit <c>AZURE_OPENAI_DEPLOYMENT</c>, and the
/// deployment then falls back to <see cref="PreferredDeployment"/>; Core requires the variable. The fallback
/// decides the MODEL, never the PROVIDER: auto-detection first applies Core's rules (Azure with its deployment,
/// then Bitdeer, OpenAI, Foundry, the generic endpoint), and only when none of those has its variables does an
/// Azure endpoint + key without a deployment win. <c>AI_INFERENCE_PROVIDER=azure</c> takes the fallback
/// directly. So a shell that also holds a Bitdeer or OpenAI key selects the same host here as in the rest of
/// AgentEval. The Evals startup check (<c>ProviderDriftGuard</c>) compares the two, and gives Core the
/// fallback only in those same cases.
/// </para>
/// <para>
/// The console does not name the provider, the model or the endpoint unless asked (<see cref="ShowModel"/>:
/// <c>--show-model</c> or <c>TRAVELDEMO_SHOW_MODEL=1</c>; <c>--hide-model</c> forces it off) — this demo is
/// presented on stage. Every exception or assertion message a demo or eval prints goes through
/// <see cref="Redact(Exception?, bool)"/> or <see cref="Redact(string?)"/>.
/// </para>
/// </remarks>
public static class Config
{
    /// <summary>The selector variable — the same one AgentEval's samples and CLI read.</summary>
    public const string SelectorVariable = "AI_INFERENCE_PROVIDER";

    public const string BitdeerDefaultEndpoint = "https://api-inference.bitdeer.ai/v1";
    public const string BitdeerDefaultModel    = "zai-org/GLM-5.3-Flash";
    public const string OpenAIDefaultEndpoint  = "https://api.openai.com/v1";
    public const string OpenAIDefaultModel     = "gpt-4o-mini";

    /// <summary>The credential sent to a keyless OpenAI-compatible host (Ollama, LM Studio, vLLM); the SDK needs a non-empty one.</summary>
    public const string NoKeyNeeded = "no-key-needed";

    /// <summary>
    /// Recommended Azure deployment for TravelDemo + TravelDemo.Evals — picked for
    /// a balance of fast per-turn latency and reliable function-calling on
    /// Azure AI Foundry. Used when the provider is Azure and <c>AZURE_OPENAI_DEPLOYMENT</c>
    /// is unset (Azure selected explicitly, or the only provider configured); the other providers
    /// have their own defaults (see the class remarks).
    /// </summary>
    /// <remarks>
    /// Empirical baseline on the <c>msfoundryjose</c> Foundry resource:
    /// <list type="bullet">
    ///   <item><c>gpt-5-chat</c>: ~3 min for the full 18-tool TravelAgent flow (reasoning-heavy, slow per turn).</item>
    ///   <item><c>gpt-5-mini</c>: ~30–60 s for the same flow — same reasoning family, faster turns. RECOMMENDED.</item>
    ///   <item><c>gpt-4o</c> / <c>gpt-4o-mini</c>: ~15–30 s — battle-tested function-calling, fastest overall.</item>
    /// </list>
    /// Override with <c>AZURE_OPENAI_DEPLOYMENT</c> to pin a specific deployment.
    /// </remarks>
    public const string PreferredDeployment = "gpt-5-mini";

    /// <summary>
    /// Programmatic model override. When non-null, takes precedence over the selected
    /// provider's model variable (<c>AZURE_OPENAI_DEPLOYMENT</c>, <c>BITDEER_MODEL</c>, ...)
    /// AND its default. Set at the top of <c>Program.cs</c> (or before a demo's <c>RunAsync</c>)
    /// to pin a specific model for an entire process without juggling
    /// shell env vars across runs. <c>--model</c> on the command line sets it.
    /// </summary>
    /// <example>
    /// <code>
    /// // Force gpt-4o-mini for the whole demo session:
    /// Config.ModelOverride = "gpt-4o-mini";
    /// await Demo01_TravelAgent.RunAsync();
    /// </code>
    /// </example>
    public static string? ModelOverride { get; set; }

    // ── What the console may show ─────────────────────────────────────────────

    /// <summary>The variable that turns the model display on, as an alternative to <c>--show-model</c>.</summary>
    public const string ShowModelVariable = "TRAVELDEMO_SHOW_MODEL";

    /// <summary>
    /// Set by the command line on either entry point: <c>true</c> for <c>--show-model</c>, <c>false</c> for
    /// <c>--hide-model</c>, <c>null</c> when neither was passed. Set, it wins over <see cref="ShowModelVariable"/>;
    /// <c>--hide-model</c> also wins over <c>--show-model</c>, so a rehearsal shell that exports
    /// <c>TRAVELDEMO_SHOW_MODEL=1</c> can still run the stage default.
    /// </summary>
    public static bool? ShowModelRequested { get; set; }

    /// <summary>
    /// True when the console may name the provider, the model and the endpoint. Off by default: this
    /// demo is presented on stage, where the host it runs on is not the subject. <c>--show-model</c> or
    /// <c>TRAVELDEMO_SHOW_MODEL=1</c> turns it on; <c>--hide-model</c> keeps it off whatever the variable says.
    /// Hidden means not printed — never replaced by another name — and only the console changes: the
    /// snapshots TravelDemo.Evals saves for Eval 3 still record the provider and the model, and so do
    /// <c>EvaluationOptions.ModelName</c> and <c>IAgentFactory.ModelId</c> where an eval passes them.
    /// </summary>
    public static bool ShowModel => ResolveShowModel(ShowModelRequested, Environment.GetEnvironmentVariable(ShowModelVariable));

    /// <summary>The rule behind <see cref="ShowModel"/>: the command line when it said anything, otherwise the variable.</summary>
    public static bool ResolveShowModel(bool? requested, string? variableValue) =>
        requested ?? variableValue?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    /// <summary>What a console line calls the model host: its display name when <see cref="ShowModel"/>, otherwise a neutral word.</summary>
    public static string HostLabel => ShowModel ? ProviderName : "The model endpoint";

    // ── Redaction: the ONE path every printed error and assertion message takes ──

    private const string Hidden = "[hidden]";
    private const int MaxInnerExceptions = 6;

    /// <summary>What replaces the free text of a provider's HTTP error in hidden mode.</summary>
    public const string ProviderMessageHidden = "— the provider's message is hidden (--show-model shows it)";

    /// <summary>
    /// Names of the stage host and its model family, taken out in hidden mode whatever the configuration says: a
    /// host's error text spells them in ways no configured value predicts ("GLM-5.3 Flash", "Zhipu", "z.ai").
    /// </summary>
    private static readonly string[] BrandFragments = ["api-inference", "bitdeer", "zai-org", "zhipu", "z.ai", "chatglm", "bigmodel", "glm"];

    /// <summary>
    /// The status line of a provider's HTTP error, in the two shapes the SDKs write it: OpenAI's
    /// <c>HTTP 404 (invalid_request_error: model_not_found)</c> and System.ClientModel / Azure.Core's
    /// <c>Status: 404 (Not Found)</c> with an optional <c>ErrorCode:</c> line. What follows it — the host's own
    /// message, the response content, the headers — is free text no list of names can be trusted to clean.
    /// Non-backtracking: linear time, so no match timeout that could fail open under load.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ProviderStatusLine = new(
        @"(?:HTTP |Status: )[1-5][0-9][0-9](?: \([^)\r\n]{0,200}\))?(?:\r?\nErrorCode: [^\r\n]{0,200})?",
        System.Text.RegularExpressions.RegexOptions.NonBacktracking | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Text for the console with the endpoint and the model names taken out unless <see cref="ShowModel"/>.
    /// For SDK error messages and assertion messages, which can carry the host or the model; the error itself is
    /// still printed. The API key is taken out in both modes.
    /// </summary>
    public static string Redact(string? text) => Redact(text, Settings, ModelOverride, hideIdentity: !ShowModel);

    /// <summary>
    /// An exception as the console may show it: its message, then each distinct inner exception (an
    /// <see cref="AggregateException"/>'s inner exceptions included) as <c>↳ Type: message</c>, then — only when
    /// <paramref name="includeStackTrace"/> — the stack trace, all through <see cref="Redact(string?)"/>.
    /// Continuation lines are indented for the demos' <c>"  ❌ …"</c> layout. Hidden, a provider's HTTP error shows
    /// its type, status and error code only (<see cref="ProviderMessageHidden"/>).
    /// </summary>
    public static string Redact(Exception? exception, bool includeStackTrace = false) =>
        exception is null ? "" : Describe(exception, includeStackTrace, Redact, hideIdentity: !ShowModel);

    /// <summary>
    /// <see cref="Redact(Exception?, bool)"/> against given settings — pure, so the offline self-test can check it
    /// without reading the process environment.
    /// </summary>
    public static string Redact(Exception exception, bool includeStackTrace, TravelProviderSettings settings, string? modelOverride, bool hideIdentity = true)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Describe(exception, includeStackTrace, text => Redact(text, settings, modelOverride, hideIdentity), hideIdentity);
    }

    /// <summary>
    /// <see cref="Redact(string?)"/> against given settings — pure. <paramref name="hideIdentity"/> false (the
    /// <c>--show-model</c> case) still takes the API key out.
    /// </summary>
    /// <remarks>
    /// Hidden, in this order: a provider's HTTP error keeps its status line and loses the free text after it (the
    /// robust part — it needs no list of spellings); then the configured model's spacing variants
    /// (<c>GLM-5.3 Flash</c> for <c>GLM-5.3-Flash</c>); then the exact names — endpoint, host:port, model, path
    /// segments, provider names — and <see cref="BrandFragments"/>, all case-insensitively.
    /// </remarks>
    public static string Redact(string? text, TravelProviderSettings settings, string? modelOverride, bool hideIdentity = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrEmpty(text)) return text ?? "";

        // A host's error text can echo the key ("Incorrect API key provided: …"): never printed, in either mode.
        if (settings.ApiKey is { Length: >= 8 } key && key != NoKeyNeeded)
            text = text.Replace(key, Hidden, StringComparison.Ordinal);

        if (!hideIdentity) return text;
        text = DropProviderMessage(text);
        foreach (var variant in ModelVariants(settings, modelOverride))
            text = variant.Replace(text, Hidden);
        foreach (var term in IdentityTerms(settings, modelOverride))
            text = text.Replace(term, Hidden, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>
    /// Keeps a provider error's status line and replaces everything after it with <see cref="ProviderMessageHidden"/>.
    /// Text without a status line is returned unchanged; so is text already cut (idempotent).
    /// </summary>
    private static string DropProviderMessage(string text)
    {
        var status = ProviderStatusLine.Match(text);
        if (!status.Success) return text;
        var rest = text[(status.Index + status.Length)..];
        if (string.IsNullOrWhiteSpace(rest) || rest.TrimStart().StartsWith(ProviderMessageHidden, StringComparison.Ordinal))
            return text;
        return $"{text[..(status.Index + status.Length)]} {ProviderMessageHidden}";
    }

    /// <summary>
    /// A hidden-mode message for a provider's HTTP exception: its status line, or — when the message has none —
    /// the status and error code the SDK parsed. Null for any other exception, whose message is redacted as text.
    /// </summary>
    private static string? ProviderErrorHeadline(Exception exception)
    {
        var (status, code) = exception switch
        {
            RequestFailedException azure  => (azure.Status, azure.ErrorCode),
            ClientResultException client  => (client.Status, (string?)null),
            _                             => (-1, null),
        };
        if (status < 0) return null;
        if (ProviderStatusLine.IsMatch(exception.Message)) return DropProviderMessage(exception.Message);
        var statusText = status > 0 ? $"HTTP {status}" : "no HTTP status";
        return $"{statusText}{(string.IsNullOrEmpty(code) ? "" : $" ({code})")} {ProviderMessageHidden}";
    }

    /// <summary>
    /// The configured model names spelled with any separators: <c>GLM-5.3-Flash</c> also matches
    /// <c>GLM-5.3 Flash</c>, <c>glm 5.3 flash</c> and <c>GLM5.3Flash</c>; a shorter prefix of two or more of its
    /// tokens (<c>GLM-5.3</c>) too. Built per '/' segment of the model and the override.
    /// </summary>
    private static IEnumerable<System.Text.RegularExpressions.Regex> ModelVariants(TravelProviderSettings settings, string? modelOverride)
    {
        foreach (var model in new[] { modelOverride, settings.Model }.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var segment in model!.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                // Cached: the redacting logger runs this on every verbose log line.
                if (VariantCache.GetOrAdd(segment, BuildVariant) is { } variant)
                    yield return variant;
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex?> VariantCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly char[] SegmentSeparators = ['-', '_', '.', ' ', ':', '@'];

    /// <summary>The variant pattern of one model segment, or null for a single-token segment (the exact terms cover it).</summary>
    private static System.Text.RegularExpressions.Regex? BuildVariant(string segment)
    {
        const string Separator = @"[^A-Za-z0-9\r\n]{0,3}";
        var tokens = segment.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return null;
        // t0 SEP t1 (?:SEP t2 (?:SEP t3)?)? — the first two tokens required, the rest a prefix-closed tail, built from the right.
        var tail = "";
        for (int i = tokens.Length - 1; i >= 2; i--)
            tail = $"(?:{Separator}{System.Text.RegularExpressions.Regex.Escape(tokens[i])}{tail})?";
        var pattern = System.Text.RegularExpressions.Regex.Escape(tokens[0]) + Separator
                    + System.Text.RegularExpressions.Regex.Escape(tokens[1]) + tail;
        return new System.Text.RegularExpressions.Regex(pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | System.Text.RegularExpressions.RegexOptions.NonBacktracking);
    }

    /// <summary>
    /// What names the host or the model, longest first: the endpoint (in full, its host and host:port, and the host's
    /// brand label — <c>bitdeer</c> in <c>api-inference.bitdeer.ai</c>), the provider's published endpoint too when
    /// another was configured, the model and the override (in full and by path segment — <c>zai-org</c>,
    /// <c>GLM-5.3-Flash</c>), the provider's own names, and <see cref="BrandFragments"/>.
    /// </summary>
    private static IEnumerable<string> IdentityTerms(TravelProviderSettings settings, string? modelOverride)
    {
        var exact   = new List<string?>();   // replaced whatever their length
        var derived = new List<string?>(BrandFragments);   // fragments: only when long enough not to hit ordinary words

        foreach (var endpoint in new[] { settings.Endpoint, PublishedEndpointOf(settings.ProviderTag) })
        {
            if (endpoint is null) continue;
            exact.Add(endpoint.AbsoluteUri);
            exact.Add(endpoint.AbsoluteUri.TrimEnd('/'));
            exact.Add(endpoint.Authority);   // host:port — a socket error names both ("127.0.0.1:18464")
            exact.Add(endpoint.Host);
            if (endpoint.HostNameType == UriHostNameType.Dns && endpoint.Host.Split('.') is { Length: >= 2 } labels)
                derived.Add(labels[^2]);
        }
        foreach (var model in new[] { modelOverride, settings.Model })
        {
            if (string.IsNullOrWhiteSpace(model)) continue;
            exact.Add(model);
            derived.AddRange(model.Split('/'));
        }
        if (settings.IsConfigured)
        {
            derived.Add(settings.DisplayName);
            derived.Add(settings.ProviderTag);
        }

        return exact.Where(t => !string.IsNullOrWhiteSpace(t))
            .Concat(derived.Where(t => t is { Length: >= 3 }))
            .Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => t.Length);
    }

    private static Uri? PublishedEndpointOf(string providerTag) => providerTag switch
    {
        "bitdeer" => new Uri(BitdeerDefaultEndpoint),
        "openai"  => new Uri(OpenAIDefaultEndpoint),
        _         => null,
    };

    private static string Describe(Exception exception, bool includeStackTrace, Func<string?, string> redact, bool hideIdentity)
    {
        // Hidden, a provider's HTTP exception is its status line (and error code) — never the host's own text.
        string MessageOf(Exception e) => (hideIdentity ? ProviderErrorHeadline(e) : null) ?? e.Message;

        var text  = new System.Text.StringBuilder(redact(MessageOf(exception)));
        var shown = new HashSet<string>(StringComparer.Ordinal) { exception.Message };
        int inner = 0;
        foreach (var e in InnerExceptionsOf(exception))
        {
            // An AggregateException repeats its inner messages, and SDK wrappers often copy their inner's: print each once.
            if (!shown.Add(e.Message)) continue;
            if (++inner > MaxInnerExceptions) { text.Append("\n     ↳ …"); break; }
            text.Append($"\n     ↳ {e.GetType().Name}: {redact(MessageOf(e))}");
        }
        if (includeStackTrace && !string.IsNullOrEmpty(exception.StackTrace))
            text.Append($"\n     Stack trace:\n{redact(exception.StackTrace)}");
        return text.ToString();
    }

    private static IEnumerable<Exception> InnerExceptionsOf(Exception exception)
    {
        IEnumerable<Exception> children = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is { } single ? [single] : [];
        foreach (var child in children)
        {
            yield return child;
            foreach (var descendant in InnerExceptionsOf(child))
                yield return descendant;
        }
    }

    /// <summary>
    /// Convenience setter for <see cref="ModelOverride"/>. Returns the previous
    /// override value so callers can restore it (useful inside a <c>try/finally</c>
    /// when only one demo should use a different model).
    /// </summary>
    public static string? UseModel(string? model)
    {
        var prev = ModelOverride;
        ModelOverride = model;
        return prev;
    }

    private enum Provider { None, AzureOpenAI, Bitdeer, OpenAI, Foundry, OpenAICompatible }

    // Core's auto-detection order: Azure, Bitdeer, OpenAI, Foundry, then the generic endpoint.
    private static readonly Provider[] AutoDetectOrder =
        [Provider.AzureOpenAI, Provider.Bitdeer, Provider.OpenAI, Provider.Foundry, Provider.OpenAICompatible];

    private static readonly Lazy<TravelProviderSettings> _settings = new(() => Resolve(Environment.GetEnvironmentVariable));

    /// <summary>The settings resolved from this process's environment, read once.</summary>
    public static TravelProviderSettings Settings => _settings.Value;

    /// <summary>True when a provider with complete credentials is selected.</summary>
    public static bool IsConfigured => Settings.IsConfigured;

    /// <summary>The provider's display name, for example "Bitdeer AI Model Studio".</summary>
    public static string ProviderName => Settings.DisplayName;

    /// <summary>Why no provider is configured, when none is; otherwise null.</summary>
    public static string? Diagnostic => Settings.Diagnostic;

    /// <summary>The selected provider's endpoint.</summary>
    public static Uri Endpoint => Settings.Endpoint ?? throw NotConfiguredException();

    /// <summary>
    /// Resolved model (Azure: deployment) name. Resolution order (first non-empty wins):
    /// <list type="number">
    ///   <item><see cref="ModelOverride"/> — programmatic / <c>--model</c> override.</item>
    ///   <item>The selected provider's model variable (<c>AZURE_OPENAI_DEPLOYMENT</c>, <c>BITDEER_MODEL</c>, <c>OPENAI_MODEL</c>, <c>FOUNDRY_MODEL</c>, <c>OPENAI_COMPATIBLE_MODEL</c>).</item>
    ///   <item>The provider's default — <see cref="PreferredDeployment"/> on Azure, <see cref="BitdeerDefaultModel"/> on Bitdeer, <see cref="OpenAIDefaultModel"/> on OpenAI.</item>
    /// </list>
    /// </summary>
    public static string Model =>
        !string.IsNullOrEmpty(ModelOverride)
            ? ModelOverride
            : Settings.Model ?? throw NotConfiguredException();

    /// <summary>The variable that holds the selected provider's key, for error hints.</summary>
    public static string ApiKeyVariable => KeyVariableOf(ParseTag(Settings.ProviderTag));

    /// <summary>The variable that names the selected provider's model, for error hints.</summary>
    public static string ModelVariable => ModelVariableOf(ParseTag(Settings.ProviderTag));

    /// <summary>
    /// Builds the chat client for the selected provider and <see cref="Model"/>. Azure OpenAI and a Foundry
    /// resource's OpenAI-compatible endpoint speak the Azure protocol; Bitdeer, OpenAI and any
    /// OpenAI-compatible host take the plain OpenAI client pointed at their base URL.
    /// </summary>
    public static IChatClient CreateChatClient()
    {
        var s = Settings;
        if (!s.IsConfigured) throw NotConfiguredException();

        if (s.UsesAzureProtocol)
        {
            return new AzureOpenAIClient(s.Endpoint!, new AzureKeyCredential(s.ApiKey!))
                .GetChatClient(Model)
                .AsIChatClient();
        }

        return new OpenAIClient(new ApiKeyCredential(s.ApiKey!), new OpenAIClientOptions { Endpoint = s.Endpoint })
            .GetChatClient(Model)
            .AsIChatClient();
    }

    // ── Resolution (a copy of Core's InferenceProviderEnvironment rules) ──────

    /// <summary>
    /// Resolves the provider from any variable source. <see cref="Settings"/> calls it once with the process
    /// environment; a caller can pass another source (a dictionary) to see what a given setup selects.
    /// </summary>
    public static TravelProviderSettings Resolve(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        string? Env(string name)
        {
            var v = getEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        var selector = Env(SelectorVariable);
        if (selector is not null)
        {
            // Explicit beats detected, and an explicit mistake fails closed.
            var chosen = ParseSelector(selector);
            if (chosen == Provider.None)
                return NotConfigured($"{SelectorVariable}='{selector}' is not one of azure | bitdeer | openai | foundry | openai-compatible.");

            var missing = MissingVariables(chosen, Env);
            if (missing.Length > 0)
                return NotConfigured($"{SelectorVariable}={TagOf(chosen)}, but it is missing: {string.Join(", ", missing)}. " +
                                     $"It needs {RequiredVariablesText(chosen)}.");

            return Describe(chosen, Env, isExplicit: true);
        }

        // Core's rules first (Azure WITH its deployment): the first provider with every variable Core requires
        // wins, as everywhere else in AgentEval — and fails closed on a bad endpoint, with no fall-through.
        foreach (var candidate in AutoDetectOrder)
        {
            if (MissingVariables(candidate, Env, coreRules: true).Length == 0)
                return Describe(candidate, Env, isExplicit: false);
        }

        // Only then the deliberate difference: Azure endpoint + key without a deployment runs on PreferredDeployment.
        // Placed after the loop so the default model never changes WHICH provider is selected.
        if (MissingVariables(Provider.AzureOpenAI, Env).Length == 0)
            return Describe(Provider.AzureOpenAI, Env, isExplicit: false);

        // SOME of a provider's variables but not all is a half-done setup: name exactly what it lacks. "Some" is
        // counted as Core counts it (so a deployment-only Azure setup is half-done here too); what is missing is
        // what THIS resolver needs.
        var partial = AutoDetectOrder
            .Where(p => MissingVariables(p, Env, coreRules: true).Length is var m && m > 0 && m < RequiredVariables(p, coreRules: true).Length
                        && MissingVariables(p, Env).Length > 0)
            .ToList();
        if (partial.Count > 0)
        {
            return NotConfigured(string.Join(" ", partial.Select(p =>
                $"{DisplayNameOf(p)} is partially configured — missing: {string.Join(", ", MissingVariables(p, Env))}.")));
        }

        return NotConfigured($"{SelectorVariable} is not set and no provider has credentials. Set one of: " +
            string.Join("; ", AutoDetectOrder.Select(p => $"{TagOf(p)} → {RequiredVariablesText(p)}")) + ".");
    }

    private static TravelProviderSettings Describe(Provider provider, Func<string, string?> env, bool isExplicit)
    {
        var (endpointVariable, endpointDefault) = provider switch
        {
            Provider.AzureOpenAI      => ("AZURE_OPENAI_ENDPOINT", (string?)null),
            Provider.Bitdeer          => ("BITDEER_ENDPOINT", BitdeerDefaultEndpoint),
            Provider.OpenAI           => ("OPENAI_BASE_URL", OpenAIDefaultEndpoint),
            Provider.Foundry          => ("FOUNDRY_ENDPOINT", null),
            Provider.OpenAICompatible => ("OPENAI_COMPATIBLE_ENDPOINT", null),
            _                         => ("", null),
        };
        var endpointValue = env(endpointVariable);
        if (!TryValidateEndpoint(endpointValue ?? endpointDefault, out var endpoint, out var why))
        {
            // Name the variable and the reason, never the value: an endpoint may carry user-info or a query.
            return NotConfigured($"{endpointVariable} {why}");
        }

        var modelVariable = ModelVariableOf(provider);
        var modelDefault  = provider switch
        {
            Provider.AzureOpenAI => PreferredDeployment,
            Provider.Bitdeer     => BitdeerDefaultModel,
            Provider.OpenAI      => OpenAIDefaultModel,
            _                    => null,   // Foundry and the generic endpoint require their model variable.
        };
        var envModel = env(modelVariable);

        // Keyless local hosts take no key; the OpenAI SDK still needs a non-empty credential string.
        var apiKey = env(KeyVariableOf(provider)) ?? (provider == Provider.OpenAICompatible ? NoKeyNeeded : null);

        return new TravelProviderSettings(
            ProviderTag:    TagOf(provider),
            DisplayName:    DisplayNameOf(provider),
            Endpoint:       endpoint,
            EndpointSource: endpointValue is null ? "default" : endpointVariable,
            ApiKey:         apiKey,
            Model:          envModel ?? modelDefault,
            ModelSource:    envModel is not null ? $"{modelVariable} (env)" : $"default ({modelDefault})",
            IsExplicit:     isExplicit,
            Diagnostic:     null);
    }

    private static TravelProviderSettings NotConfigured(string diagnostic) =>
        new("none", "no provider configured", null, null, null, null, null, false, diagnostic);

    private static InvalidOperationException NotConfiguredException() =>
        new(Settings.Diagnostic ?? "No model provider is configured.");

    // Same spellings Core accepts.
    private static Provider ParseSelector(string value) => value.Trim().ToLowerInvariant() switch
    {
        "azure" or "azure-openai" or "azureopenai"                                     => Provider.AzureOpenAI,
        "bitdeer"                                                                      => Provider.Bitdeer,
        "openai"                                                                       => Provider.OpenAI,
        "foundry" or "azure-foundry" or "azure-ai-foundry"                             => Provider.Foundry,
        "openai-compatible" or "openai_compatible" or "compatible" or "openai-compat"  => Provider.OpenAICompatible,
        _                                                                              => Provider.None,
    };

    private static Provider ParseTag(string tag) => tag == "none" ? Provider.None : ParseSelector(tag);

    private static string TagOf(Provider p) => p switch
    {
        Provider.AzureOpenAI      => "azure",
        Provider.Bitdeer          => "bitdeer",
        Provider.OpenAI           => "openai",
        Provider.Foundry          => "foundry",
        Provider.OpenAICompatible => "openai-compatible",
        _                         => "none",
    };

    private static string DisplayNameOf(Provider p) => p switch
    {
        Provider.AzureOpenAI      => "Azure OpenAI",
        Provider.Bitdeer          => "Bitdeer AI Model Studio",
        Provider.OpenAI           => "OpenAI",
        Provider.Foundry          => "Azure AI Foundry (OpenAI-compatible endpoint)",
        Provider.OpenAICompatible => "OpenAI-compatible endpoint",
        _                         => "no provider configured",
    };

    private static string KeyVariableOf(Provider p) => p switch
    {
        Provider.AzureOpenAI      => "AZURE_OPENAI_API_KEY",
        Provider.Bitdeer          => "BITDEER_API_KEY",
        Provider.OpenAI           => "OPENAI_API_KEY",
        Provider.Foundry          => "FOUNDRY_API_KEY",
        Provider.OpenAICompatible => "OPENAI_COMPATIBLE_API_KEY",
        _                         => "the provider's API key variable",
    };

    private static string ModelVariableOf(Provider p) => p switch
    {
        Provider.AzureOpenAI      => "AZURE_OPENAI_DEPLOYMENT",
        Provider.Bitdeer          => "BITDEER_MODEL",
        Provider.OpenAI           => "OPENAI_MODEL",
        Provider.Foundry          => "FOUNDRY_MODEL",
        Provider.OpenAICompatible => "OPENAI_COMPATIBLE_MODEL",
        _                         => "the provider's model variable",
    };

    // Azure differs from Core on purpose: the deployment is optional (PreferredDeployment). coreRules: true gives
    // Core's set, which auto-detection tries first so the default never changes which provider is selected.
    private static string[] RequiredVariables(Provider p, bool coreRules = false) => p switch
    {
        Provider.AzureOpenAI when coreRules => ["AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_DEPLOYMENT"],
        Provider.AzureOpenAI      => ["AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY"],
        Provider.Bitdeer          => ["BITDEER_API_KEY"],
        Provider.OpenAI           => ["OPENAI_API_KEY"],
        Provider.Foundry          => ["FOUNDRY_ENDPOINT", "FOUNDRY_API_KEY", "FOUNDRY_MODEL"],
        Provider.OpenAICompatible => ["OPENAI_COMPATIBLE_ENDPOINT", "OPENAI_COMPATIBLE_MODEL"],
        _                         => [],
    };

    private static string RequiredVariablesText(Provider p) => p switch
    {
        Provider.AzureOpenAI      => $"AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY (AZURE_OPENAI_DEPLOYMENT optional, default {PreferredDeployment})",
        Provider.Bitdeer          => "BITDEER_API_KEY (BITDEER_ENDPOINT and BITDEER_MODEL have defaults)",
        Provider.OpenAI           => "OPENAI_API_KEY (OPENAI_BASE_URL and OPENAI_MODEL have defaults)",
        Provider.Foundry          => "FOUNDRY_ENDPOINT + FOUNDRY_API_KEY + FOUNDRY_MODEL",
        Provider.OpenAICompatible => "OPENAI_COMPATIBLE_ENDPOINT + OPENAI_COMPATIBLE_MODEL (OPENAI_COMPATIBLE_API_KEY optional)",
        _                         => "",
    };

    private static string[] MissingVariables(Provider p, Func<string, string?> env, bool coreRules = false) =>
        [.. RequiredVariables(p, coreRules).Where(name => env(name) is null)];

    /// <summary>
    /// An endpoint that will carry an API key must be an absolute <c>https</c> URL, or a loopback
    /// <c>http</c> one for local servers. Anything else is refused with a reason (Core's rule).
    /// </summary>
    private static bool TryValidateEndpoint(string? value, out Uri? endpoint, out string? reason)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "is not set.";
            return false;
        }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            reason = "is not an absolute http(s) URL.";
            return false;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            reason = "uses plain http to a non-loopback host; an API key would travel in cleartext. Use https, or a loopback address for a local server.";
            return false;
        }
        endpoint = uri;
        reason = null;
        return true;
    }

    // ── Banners ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The endpoint as a banner may show it: a provider's published default URL (Bitdeer, OpenAI) is
    /// printed; an endpoint the operator supplied is reported as set, with the variable it came from.
    /// </summary>
    public static string DescribeEndpoint(TravelProviderSettings settings) =>
        settings.Endpoint is null       ? "(unset)"
        : settings.EndpointSource == "default" ? $"{settings.Endpoint.AbsoluteUri}  [default]"
        : $"(set, from {settings.EndpointSource})";

    /// <summary>
    /// Prints a "Model" header block to the console. By default it says only that a model is
    /// configured; with <see cref="ShowModel"/> it shows the selected provider and the resolved model,
    /// so the operator sees at a glance which provider and model are about to be charged.
    /// </summary>
    /// <remarks>
    /// ⚠ The API key is NOT printed at all — not in full, not as a fingerprint, not as a hash. An
    /// earlier version printed <c>first4…last4</c>, which is eight real characters of the secret, and
    /// this banner runs at the top of every demo and eval, so those characters reached every
    /// <c>--log</c> file, terminal scrollback and screenshot of a sample in a public repository. An
    /// endpoint the operator supplied (Azure, Foundry, a custom base URL) is reported as set rather than
    /// by URL, because such a URL names the resource; only a provider's published default URL is shown.
    /// The provider, model and endpoint are hidden unless <c>--show-model</c> / <c>TRAVELDEMO_SHOW_MODEL=1</c>:
    /// the demo is presented on stage, and <c>--log</c> files tee this same output.
    /// </remarks>
    public static void PrintTarget()
    {
        var s = Settings;

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Model ───────────────────────────────────────────────────────────");
        Console.ResetColor();
        if (!ShowModel)
        {
            // Nothing that identifies the host: not the provider, not the model, not the endpoint.
            Console.WriteLine($"  Model      : {(s.IsConfigured ? "configured" : "not configured")}  (details hidden; --show-model shows them)");
        }
        else if (!s.IsConfigured)
        {
            Console.WriteLine($"  Provider   : none — {s.Diagnostic}");
        }
        else
        {
            var selection   = s.IsExplicit ? $"{SelectorVariable}={s.ProviderTag}" : "auto-detected";
            var modelSource = !string.IsNullOrEmpty(ModelOverride) ? "--model / Config.ModelOverride (code)" : s.ModelSource;
            Console.WriteLine($"  Provider   : {s.DisplayName}  [{selection}]");
            Console.WriteLine($"  Endpoint   : {DescribeEndpoint(s)}");
            Console.WriteLine($"  Model      : {Model}  [source: {modelSource}]");
        }
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────");
        Console.ResetColor();
    }

    /// <summary>
    /// The guidance a demo or eval prints instead of running when no provider is configured:
    /// why (<see cref="Diagnostic"/>) and the variables each provider takes.
    /// </summary>
    /// <param name="skipping">What is being skipped, e.g. <c>Demo 01</c>.</param>
    public static void PrintMissingProvider(string skipping)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n  ⚠️  Skipping {skipping} — no model configured.");
        PrintMissingDetails();
        Console.ResetColor();
    }

    /// <summary>
    /// Why no provider is configured and what to set — with <see cref="ShowModel"/>. Hidden, the provider
    /// names and variables stay off the screen and a single line says how to see them.
    /// </summary>
    public static void PrintMissingDetails()
    {
        if (!ShowModel)
        {
            Console.WriteLine($"     Run with --show-model (or set {ShowModelVariable}=1) to see what is missing and which variables to set.\n");
            return;
        }
        if (Diagnostic is { } why)
            Console.WriteLine($"     ↳ {why}");
        PrintProviderHelp();
    }

    /// <summary>The selector and each provider's variables, for the menus and the skip messages.</summary>
    public static void PrintProviderHelp()
    {
        Console.WriteLine();
        Console.WriteLine($"     Choose a provider with {SelectorVariable} and set its variables:");
        Console.WriteLine($"       azure              AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY  (AZURE_OPENAI_DEPLOYMENT, default {PreferredDeployment})");
        Console.WriteLine( "       bitdeer            BITDEER_API_KEY  (BITDEER_ENDPOINT, BITDEER_MODEL optional)");
        Console.WriteLine( "       openai             OPENAI_API_KEY  (OPENAI_BASE_URL, OPENAI_MODEL optional)");
        Console.WriteLine( "       foundry            FOUNDRY_ENDPOINT + FOUNDRY_API_KEY + FOUNDRY_MODEL");
        Console.WriteLine( "       openai-compatible  OPENAI_COMPATIBLE_ENDPOINT + OPENAI_COMPATIBLE_MODEL  (OPENAI_COMPATIBLE_API_KEY optional)");
        Console.WriteLine( "     Unset, the first provider with credentials wins (azure, bitdeer, openai, foundry, openai-compatible);");
        Console.WriteLine( "     an azure setup without AZURE_OPENAI_DEPLOYMENT only when no other provider is configured.");
        Console.WriteLine($"     The console hides the provider, model and endpoint unless --show-model or {ShowModelVariable}=1");
        Console.WriteLine( "     (--hide-model keeps them hidden whatever the variable says).");
        Console.WriteLine();
    }
}
