// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;

namespace AgentEval.Decisions;

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
            if (request.Model is not null && string.IsNullOrWhiteSpace(request.Model))
                throw new DecisionClientException(DecisionFailureKind.InvalidRequest, "A model override must not be blank; pass null to use the transport's default.");
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
                    foreach (var key in probabilities.Keys)
                    {
                        if (!choiceQuestion.Criteria.ContainsKey(key))
                            throw Invalid($"{host}: answer '{id}' has a probability for '{key}', which was not one of the requested options.");
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

                    var score = ReadDouble(el, "score", id, host);
                    if (double.IsNaN(score) || score < 0 || score > scoreQuestion.Criteria.Count - 1)
                        throw Invalid($"{host}: answer '{id}' has score {score}, outside the requested scale 0..{scoreQuestion.Criteria.Count - 1}.");

                    return new ScoreAnswer(
                        score,
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
        // A token count is an integer. A fractional or out-of-range value is a protocol violation,
        // not something to round: a truncated count under-reports cost.
        if (!v.TryGetInt64(out var l))
            throw Invalid($"{host}: '{id}.{name}' is not an integer token count ({v.GetRawText()}).");
        return l;
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
