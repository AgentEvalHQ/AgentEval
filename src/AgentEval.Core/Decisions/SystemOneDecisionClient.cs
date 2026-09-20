// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentEval.Decisions;

/// <summary>
/// Where a System One decision model is reached and as what. Two providers speak this protocol today —
/// TypeSafe directly, and OpenRouter as a relay — and they differ only in base URL and model id, so
/// the same client serves both. Build with <see cref="ForTypeSafe"/> or <see cref="ForOpenRouter"/>,
/// or set the members yourself for a proxy.
/// </summary>
/// <remarks>
/// A class rather than a record on purpose: a record's generated <c>ToString()</c> would print the
/// API key into any log line that formats the options.
/// </remarks>
public sealed class SystemOneClientOptions
{
    /// <summary>TypeSafe's own endpoint (<c>POST</c>), per their API reference.</summary>
    public const string TypeSafeEndpoint = "https://api.typesafe.ai/v1/systemone";

    /// <summary>OpenRouter's System One relay endpoint (<c>POST</c>), per OpenRouter's TypeSafe SDK guide. OpenRouter also serves the same body at <c>/api/alpha/decisions</c>; this one is the path their SDK guide documents.</summary>
    public const string OpenRouterEndpoint = "https://openrouter.ai/api/v1/systemone";

    /// <summary>
    /// TypeSafe's documented flagship alias. An alias moves; the <see cref="DecisionResponse.Model"/> the
    /// provider echoes back is the resolved build and is what provenance records. Pin a versioned id
    /// (TypeSafe's reference shows the <c>jev-1.13.0</c> form) for a reproducible baseline.
    /// </summary>
    public const string TypeSafeDefaultModel = "jev-latest";

    /// <summary>The pinned Jev release on OpenRouter. <c>~typesafe/jev-latest</c> is the moving alias.</summary>
    public const string OpenRouterDefaultModel = "typesafe/jev-1.13";

    /// <summary>The full request URL, including the path.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>The bearer token. Never logged, never in an exception message.</summary>
    public required string ApiKey { get; init; }

    /// <summary>The model id sent when a <see cref="DecisionRequest.Model"/> does not override it.</summary>
    public required string Model { get; init; }

    /// <summary>A short provider label for logs and provenance notes, e.g. <c>typesafe</c> or <c>openrouter</c>.</summary>
    public string ProviderName { get; init; } = "system-one";

    /// <summary>Options for TypeSafe's own endpoint.</summary>
    /// <param name="apiKey">The TypeSafe API key.</param>
    /// <param name="model">The model id; defaults to <see cref="TypeSafeDefaultModel"/>.</param>
    public static SystemOneClientOptions ForTypeSafe(string apiKey, string model = TypeSafeDefaultModel) => new()
    {
        Endpoint = new Uri(TypeSafeEndpoint),
        ApiKey = apiKey,
        Model = model,
        ProviderName = "typesafe",
    };

    /// <summary>Options for OpenRouter's relay of the same protocol.</summary>
    /// <param name="apiKey">The OpenRouter API key.</param>
    /// <param name="model">The model id; defaults to <see cref="OpenRouterDefaultModel"/>.</param>
    public static SystemOneClientOptions ForOpenRouter(string apiKey, string model = OpenRouterDefaultModel) => new()
    {
        Endpoint = new Uri(OpenRouterEndpoint),
        ApiKey = apiKey,
        Model = model,
        ProviderName = "openrouter",
    };
}

/// <summary>
/// <see cref="IDecisionClient"/> over the System One HTTP protocol: one <c>POST</c> carrying
/// <c>{ model, state, questions }</c>, one reply carrying <c>{ model, answers, usage }</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strict on the way in.</b> Every question asked must come back with an answer of the matching
/// type and every probability must be a finite number in [0, 1]; anything else is a
/// <see cref="DecisionClientException"/> with <see cref="DecisionFailureKind.InvalidResponse"/>, never
/// a made-up value. The parser does not manufacture a confidence for a yes/no answer, because the
/// protocol does not carry one.
/// </para>
/// <para>
/// <b>No retries.</b> A 429 or 529 is thrown with <see cref="DecisionClientException.IsTransient"/>
/// set so the caller can back off deliberately and record that it did. A retry hidden in here would
/// silently double the latency and cost an eval reports.
/// </para>
/// <para>
/// <b>Dry runs.</b> <see cref="RenderRequest"/> returns the exact bytes a call would send, through the
/// same serializer the call uses, so a caller can print every payload before spending anything.
/// </para>
/// </remarks>
public sealed class SystemOneDecisionClient : IDecisionClient, IDisposable
{
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SystemOneClientOptions _options;

    /// <summary>Creates a client.</summary>
    /// <param name="options">Endpoint, key and default model.</param>
    /// <param name="httpClient">
    /// Optional caller-owned <see cref="HttpClient"/> (for a resilience pipeline, a test handler, or a
    /// shared pool). When <see langword="null"/> the client creates and owns one with a 60 s timeout.
    /// </param>
    public SystemOneDecisionClient(SystemOneClientOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Endpoint is null || !options.Endpoint.IsAbsoluteUri)
            throw new ArgumentException("Endpoint must be an absolute URL.", nameof(options));
        if (options.Endpoint.Scheme != Uri.UriSchemeHttps && !options.Endpoint.IsLoopback)
            throw new ArgumentException("Endpoint must use https (a loopback http address is allowed for local testing).", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("ApiKey is missing.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is missing.", nameof(options));

        _options = options;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = s_defaultTimeout };
    }

    /// <summary>The request URL this client posts to.</summary>
    public Uri Endpoint => _options.Endpoint;

    /// <summary>The model id sent when a request does not override it.</summary>
    public string Model => _options.Model;

    /// <summary>The provider label from the options.</summary>
    public string ProviderName => _options.ProviderName;

    /// <summary>
    /// The exact JSON a <see cref="DecideAsync"/> call would send for <paramref name="request"/>,
    /// produced by the same serializer. Print this in a dry run before the first paid call.
    /// </summary>
    /// <param name="request">The request to render.</param>
    /// <param name="defaultModel">The model to write when the request does not name one.</param>
    public static string RenderRequest(DecisionRequest request, string defaultModel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModel);
        return SystemOneProtocol.SerializeRequest(request, defaultModel);
    }

    /// <inheritdoc/>
    public async Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var json = SystemOneProtocol.SerializeRequest(request, _options.Model);
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var host = _options.Endpoint.Host;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // the caller's cancellation, not a provider failure
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient signals its own timeout as a cancellation the caller did not request.
            throw new DecisionClientException(
                DecisionFailureKind.ProviderUnavailable,
                $"The decision call to {host} timed out after {_http.Timeout.TotalSeconds:F0}s without a response.",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DecisionClientException(
                DecisionFailureKind.Unknown,
                $"The decision call to {host} failed before a response: {ex.Message}",
                innerException: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // A provider that echoes the request (some 4xx bodies quote the headers) would otherwise
            // put the bearer into our own exception message. Only error bodies are excerpted, so only
            // they are redacted: redacting a success body would corrupt it when the key is a short
            // string that also occurs inside field names (a "k" key turns "input_tokens" into
            // "input_to[redacted]ens"). A success body is parsed verbatim and never quoted.
            if (!response.IsSuccessStatusCode)
                throw SystemOneProtocol.ClassifyFailure((int)response.StatusCode, body.Replace(_options.ApiKey, "[redacted]", StringComparison.Ordinal), host);

            return SystemOneProtocol.ParseResponse(body, request.Questions, host);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

/// <summary>
/// The wire shape, in one place: request serialisation, response parsing, failure classification.
/// Internal so the tests can pin the protocol without a network; public entry is
/// <see cref="SystemOneDecisionClient.RenderRequest"/>.
/// </summary>
internal static class SystemOneProtocol
{
    private const int BodyExcerptLength = 500;

    /// <summary>The most options a System One choice question accepts (a provider limit, not a contract one).</summary>
    internal const int MaxChoiceOptions = 255;

    /// <summary>The most levels a System One score question accepts (a provider limit, not a contract one).</summary>
    internal const int MaxScoreLevels = 10;

    private static readonly JsonSerializerOptions s_stateOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string SerializeRequest(DecisionRequest request, string defaultModel)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Model ?? defaultModel);

            writer.WritePropertyName("state");
            WriteState(writer, request.State);

            writer.WritePropertyName("questions");
            writer.WriteStartObject();
            foreach (var (id, question) in request.Questions)
            {
                writer.WritePropertyName(id);
                WriteQuestion(writer, question);
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteState(Utf8JsonWriter writer, object state)
    {
        switch (state)
        {
            case string s:
                writer.WriteStringValue(s);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                JsonSerializer.SerializeToElement(state, state.GetType(), s_stateOptions).WriteTo(writer);
                break;
        }
    }

    private static void WriteQuestion(Utf8JsonWriter writer, DecisionQuestion question)
    {
        writer.WriteStartObject();
        switch (question)
        {
            case BinaryQuestion noul:
                writer.WriteString("type", "noul");
                writer.WriteString("instructions", noul.Instructions);
                if (noul.TrueCriteria is not null || noul.FalseCriteria is not null)
                {
                    writer.WritePropertyName("criteria");
                    writer.WriteStartObject();
                    if (noul.TrueCriteria is not null) writer.WriteString("true", noul.TrueCriteria);
                    if (noul.FalseCriteria is not null) writer.WriteString("false", noul.FalseCriteria);
                    writer.WriteEndObject();
                }
                break;

            case ChoiceQuestion choice:
                if (choice.Criteria.Count > MaxChoiceOptions)
                    throw new DecisionClientException(DecisionFailureKind.InvalidRequest, $"A System One choice question accepts at most {MaxChoiceOptions} options; {choice.Criteria.Count} were supplied.");
                writer.WriteString("type", "choice");
                writer.WriteString("instructions", choice.Instructions);
                writer.WritePropertyName("criteria");
                writer.WriteStartObject();
                foreach (var (option, description) in choice.Criteria)
                    writer.WriteString(option, description);
                writer.WriteEndObject();
                break;

            case ScoreQuestion score:
                if (score.Criteria.Count > MaxScoreLevels)
                    throw new DecisionClientException(DecisionFailureKind.InvalidRequest, $"A System One score question accepts at most {MaxScoreLevels} levels; {score.Criteria.Count} were supplied.");
                writer.WriteString("type", "score");
                writer.WriteString("instructions", score.Instructions);
                writer.WritePropertyName("criteria");
                writer.WriteStartArray();
                foreach (var level in score.Criteria)
                    writer.WriteStringValue(level);
                writer.WriteEndArray();
                break;

            default:
                throw new NotSupportedException($"Unknown question shape {question.GetType().Name}.");
        }
        writer.WriteEndObject();
    }

    internal static DecisionClientException ClassifyFailure(int statusCode, string body, string host)
    {
        var kind = statusCode switch
        {
            401 or 403 => DecisionFailureKind.Authentication,
            400 or 422 => DecisionFailureKind.InvalidRequest,
            429 => DecisionFailureKind.RateLimited,
            529 => DecisionFailureKind.Overloaded,
            >= 500 and <= 599 => DecisionFailureKind.ProviderUnavailable,
            _ => DecisionFailureKind.Unknown,
        };
        return new DecisionClientException(kind, $"{host} answered HTTP {statusCode} ({kind}): {Excerpt(body)}", statusCode);
    }

    internal static DecisionResponse ParseResponse(string body, IReadOnlyDictionary<string, DecisionQuestion> asked, string host)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw Invalid($"{host} answered 2xx with a body that is not JSON: {Excerpt(body)}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid($"{host} answered with a JSON {root.ValueKind}, not an object.");

            if (!root.TryGetProperty("model", out var modelEl) || modelEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(modelEl.GetString()))
                throw Invalid($"{host} answered without a 'model' field naming what answered.");
            var model = modelEl.GetString()!;

            if (!root.TryGetProperty("answers", out var answersEl) || answersEl.ValueKind != JsonValueKind.Object)
                throw Invalid($"{host} answered without an 'answers' object.");

            var answers = new Dictionary<string, DecisionAnswer>(asked.Count, StringComparer.Ordinal);
            foreach (var (id, question) in asked)
            {
                if (!answersEl.TryGetProperty(id, out var answerEl) || answerEl.ValueKind != JsonValueKind.Object)
                    throw Invalid($"{host} returned no answer for question '{id}'.");
                answers[id] = ParseAnswer(id, question, answerEl, host);
            }

            DecisionUsage? usage = null;
            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                var input = ReadLong(usageEl, "input_tokens", id: "usage", host);
                var output = ReadLong(usageEl, "output_tokens", id: "usage", host);
                double? cost = usageEl.TryGetProperty("cost", out var costEl) && costEl.ValueKind == JsonValueKind.Number
                    ? costEl.GetDouble()
                    : null;
                try
                {
                    usage = new DecisionUsage(input, output, cost);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    throw Invalid($"{host} reported usage outside the valid range: {ex.Message}", ex);
                }
            }

            string? responseId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            return new DecisionResponse(model, answers, usage, responseId);
        }
    }

    private static DecisionAnswer ParseAnswer(string id, DecisionQuestion question, JsonElement el, string host)
    {
        var expected = question switch
        {
            BinaryQuestion => "noul",
            ChoiceQuestion => "choice",
            ScoreQuestion => "score",
            _ => throw new NotSupportedException($"Unknown question shape {question.GetType().Name}."),
        };

        if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            throw Invalid($"{host}: answer '{id}' has no 'type'.");
        var type = typeEl.GetString();
        if (!string.Equals(type, expected, StringComparison.Ordinal))
            throw Invalid($"{host}: answer '{id}' is a '{type}' answer to a '{expected}' question.");

        try
        {
            switch (question)
            {
                case BinaryQuestion:
                    return new BinaryAnswer(ReadDouble(el, "noul", id, host));

                case ChoiceQuestion choiceQuestion:
                {
                    var choice = ReadString(el, "choice", id, host);
                    var probabilities = ReadDistribution(el, "probabilities", id, host);

                    // The contract promises a probability for every option asked and a selection from that set.
                    // A provider that invents an option, or drops one, has not answered the question that was asked.
                    if (!choiceQuestion.Criteria.ContainsKey(choice))
                        throw Invalid($"{host}: answer '{id}' selected '{choice}', which is not one of the requested options.");
                    foreach (var option in choiceQuestion.Criteria.Keys)
                    {
                        if (!probabilities.ContainsKey(option))
                            throw Invalid($"{host}: answer '{id}' has no probability for option '{option}'.");
                    }

                    return new ChoiceAnswer(choice, probabilities, ReadDouble(el, "confidence", id, host));
                }

                case ScoreQuestion scoreQuestion:
                {
                    var probabilities = ReadDistribution(el, "probabilities", id, host);

                    // Levels are 0-indexed on the wire; every requested level must be present and nothing else.
                    foreach (var key in probabilities.Keys)
                    {
                        if (!int.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index) || index < 0 || index >= scoreQuestion.Criteria.Count)
                            throw Invalid($"{host}: answer '{id}' has a probability for unknown level '{key}'.");
                    }
                    for (var i = 0; i < scoreQuestion.Criteria.Count; i++)
                    {
                        if (!probabilities.ContainsKey(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                            throw Invalid($"{host}: answer '{id}' has no probability for level {i}.");
                    }

                    return new ScoreAnswer(
                        ReadDouble(el, "score", id, host),
                        probabilities,
                        ReadLegend(el),
                        ReadDouble(el, "confidence", id, host));
                }

                default:
                    throw new NotSupportedException();
            }
        }
        catch (ArgumentException ex)   // the record guards: out-of-range probability, blank choice
        {
            throw Invalid($"{host}: answer '{id}' is out of range: {ex.Message}", ex);
        }
    }

    private static double ReadDouble(JsonElement el, string name, string id, string host)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            throw Invalid($"{host}: answer '{id}' has no numeric '{name}'.");
        return v.GetDouble();
    }

    private static long ReadLong(JsonElement el, string name, string id, string host)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            throw Invalid($"{host}: '{id}' has no numeric '{name}'.");
        return v.TryGetInt64(out var l) ? l : (long)v.GetDouble();
    }

    private static string ReadString(JsonElement el, string name, string id, string host)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw Invalid($"{host}: answer '{id}' has no string '{name}'.");
        return v.GetString()!;
    }

    private static IReadOnlyDictionary<string, double> ReadDistribution(JsonElement el, string name, string id, string host)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object)
            throw Invalid($"{host}: answer '{id}' has no '{name}' object.");
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Number)
                throw Invalid($"{host}: answer '{id}' has a non-numeric probability for '{p.Name}'.");
            map[p.Name] = p.Value.GetDouble();
        }
        return map;
    }

    private static IReadOnlyDictionary<string, string>? ReadLegend(JsonElement el)
    {
        if (!el.TryGetProperty("legend", out var v) || v.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
            map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
        return map;
    }

    private static DecisionClientException Invalid(string message, Exception? inner = null)
        => new(DecisionFailureKind.InvalidResponse, message, innerException: inner);

    private static string Excerpt(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "<empty body>";
        var flat = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= BodyExcerptLength ? flat : flat[..BodyExcerptLength] + "…";
    }
}
