// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// How the model should read a refusal (Phase 4, P4-2) — a field on the <see cref="GatekeeperRefusalContract"/>
/// envelope, derived from the blocking gate's CLASS (never its policy name or reason). It tells a good agent what
/// to do next without helping an attacker probe: whether to give up, back off, retry, or stop entirely.
/// </summary>
public enum RefusalDisposition
{
    /// <summary>The action is not permitted; retrying the same thing will not help.</summary>
    Denied,

    /// <summary>A budget / rate / quota limit was hit; a smaller or later request might be admitted.</summary>
    Quota,

    /// <summary>The gate could not evaluate (failed closed); the same request may succeed on retry.</summary>
    Transient,

    /// <summary>A security trap fired (canary / quarantine); the agent should STOP, not retry or rephrase.</summary>
    Escalate,
}

/// <summary>
/// The versioned, structured model-facing refusal envelope (Phase 4, P4-1) — the default projection of an F-C
/// <see cref="GateEvidence"/> block. Camouflaged presentation deliberately substitutes validated generic text
/// while retaining the same evidence. This contract replaces the old bare
/// <c>{"error":"ACTION_NOT_AUTHORIZED",…}</c> (whose top-level <c>error</c> collided with a tool's OWN error shape,
/// so a model couldn't tell a gate refusal from a tool failure) with a NAMESPACED, versioned object under
/// <c>_gatekeeper</c>. It still leaks nothing (#12): only an opaque reference id, the schema, a coarse
/// <see cref="RefusalDisposition"/>, and — when known — how many equivalent attempts have been denied
/// (P4-3). The full policy name + reason stay in the audit trace, keyed by the same reference id.
/// </summary>
public static class GatekeeperRefusalContract
{
    /// <summary>The schema tag a consumer version-checks.</summary>
    public const string Schema = "gatekeeper.refusal/1";

    /// <summary>The single top-level key that unambiguously marks a Gatekeeper refusal (vs. a tool's own error).</summary>
    public const string EnvelopeKey = "_gatekeeper";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Builds the model-facing refusal body for <paramref name="referenceId"/>.</summary>
    /// <param name="referenceId">The opaque, correlatable reference id.</param>
    /// <param name="disposition">How the model should treat the refusal (P4-2).</param>
    /// <param name="attempts">Equivalent attempts denied so far, if tracked (P4-3); omitted when null.</param>
    public static string Build(string referenceId, RefusalDisposition disposition = RefusalDisposition.Denied, int? attempts = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["schema"] = Schema,
            ["block"] = true,
            ["referenceId"] = referenceId,
            ["disposition"] = disposition.ToString().ToLowerInvariant(),
        };
        if (attempts is { } n)
        {
            envelope["attempts"] = n;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?> { [EnvelopeKey] = envelope }, JsonOptions);
    }

    /// <summary>
    /// Parses a tool-result / response body and reports whether it is a Gatekeeper refusal (the "a consumer can
    /// distinguish a gate refusal from a tool error" capability, P4-1). Returns false for a tool's own
    /// <c>{"error":…}</c> or any non-envelope body.
    /// </summary>
    public static bool TryParse(string? body, out string referenceId, out RefusalDisposition disposition, out int? attempts)
    {
        referenceId = string.Empty;
        disposition = RefusalDisposition.Denied;
        attempts = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            // Defensive on EVERY field's ValueKind (review #1): a hostile but valid-JSON body — e.g.
            // {"_gatekeeper":{"schema":123}} or {..,"attempts":1.5} — must return false, never throw. Reading
            // .GetString() on a non-string or .GetInt32() on a fractional/overflow number would otherwise throw.
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(EnvelopeKey, out var env)
                || env.ValueKind != JsonValueKind.Object
                || !env.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.String
                || schema.GetString() != Schema)
            {
                return false;
            }

            if (env.TryGetProperty("referenceId", out var r) && r.ValueKind == JsonValueKind.String)
            {
                referenceId = r.GetString() ?? string.Empty;
            }

            if (env.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.String
                && Enum.TryParse<RefusalDisposition>(d.GetString(), ignoreCase: true, out var parsed))
            {
                disposition = parsed;
            }

            if (env.TryGetProperty("attempts", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var n))
            {
                attempts = n;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Maps a blocking gate to a <see cref="RefusalDisposition"/> (Phase 4, P4-2) by its CLASS — budgets/rate limits
/// are <see cref="RefusalDisposition.Quota"/>; a canary/quarantine trap is <see cref="RefusalDisposition.Escalate"/>;
/// a gate that failed closed because it could not evaluate is <see cref="RefusalDisposition.Transient"/>; anything
/// else is a plain <see cref="RefusalDisposition.Denied"/>. Never exposes the policy name or reason to the model.
/// </summary>
public static class RefusalDispositionClassifier
{
    /// <summary>Classifies a refusal by the blocking policy's name; <paramref name="failedClosedOnThrow"/> forces Transient.</summary>
    public static RefusalDisposition Classify(string? policyName, bool failedClosedOnThrow = false)
    {
        if (failedClosedOnThrow)
        {
            return RefusalDisposition.Transient;
        }

        var policy = policyName ?? string.Empty;

        // "RateLimit", not "Rate" (review #3): a bare "Rate" substring mis-maps ModerateContentGate / AggregateGate
        // etc. to Quota, telling the model "retry later might work" for an action that is actually a hard Denied.
        if (ContainsAny(policy, "Budget", "RateLimit", "Monetary", "PerToolCall", "Quota"))
        {
            return RefusalDisposition.Quota;
        }

        // A quarantined session should knowingly stop → Escalate. A canary/honeypot deliberately does NOT (review
        // #5): surfacing Escalate would tip off an attacker-controlled model that it tripped a trap, defeating the
        // trap's whole purpose (silent detection). A canary block therefore blends in as an ordinary Denied.
        if (policy.Contains("Quarantine", StringComparison.OrdinalIgnoreCase))
        {
            return RefusalDisposition.Escalate;
        }

        return RefusalDisposition.Denied;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
