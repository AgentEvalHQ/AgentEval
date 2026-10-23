// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;

namespace AgentEval.Results.Adapters.Reading;

// Check recorded required fields before record constructors can turn missing values into defaults.
// Unknown optional fields are retained in the original document. This layer does not interpret rich report schemas.
internal static class ComplianceArtifactShape
{
    internal static void Validate(JsonElement evidence)
    {
        if (Text(evidence, "schemaVersion") != "1.0") throw Bad("schemaVersion");
        Text(evidence, "regulation");
        Text(evidence, "generatedAt");
        var subject = Member(evidence, "subject", JsonValueKind.Object);
        if (Text(subject, "kind") is not ("agent" or "workflow")) throw Bad("subject.kind");
        Text(subject, "name");
        var source = Member(evidence, "sourceRun", JsonValueKind.Object);
        Text(source, "runId");
        Text(source, "manifestHash");
        var summary = Member(evidence, "summary", JsonValueKind.Object);
        foreach (var name in new[] { "controlsTotal", "passed", "warnings", "failed" })
            if (!Member(summary, name, JsonValueKind.Number).TryGetInt32(out var count) || count < 0) throw Bad("summary." + name);
        Text(summary, "overallStatus");
        foreach (var control in Member(evidence, "controls", JsonValueKind.Array).EnumerateArray())
        {
            Text(control, "id"); Text(control, "title"); Text(control, "status");
            if (!control.TryGetProperty("passRate", out var rate)
                || (rate.ValueKind != JsonValueKind.Null && (rate.ValueKind != JsonValueKind.Number
                    || !rate.TryGetDouble(out var score) || !double.IsFinite(score) || score is < 0 or > 1))) throw Bad("controls.passRate");
            foreach (var reference in Member(control, "scenarioRefs", JsonValueKind.Array).EnumerateArray())
                if (reference.ValueKind != JsonValueKind.String) throw Bad("controls.scenarioRefs");
        }
        var attestation = Member(evidence, "attestation", JsonValueKind.Object);
        Text(attestation, "agentEvalVersion"); Text(attestation, "evaluator"); Text(attestation, "evaluatorModel");
    }

    private static JsonElement Member(JsonElement value, string name, JsonValueKind kind) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) && member.ValueKind == kind
            ? member : throw Bad(name);
    private static string Text(JsonElement value, string name) => Member(value, name, JsonValueKind.String).GetString()!;
    private static InvalidDataException Bad(string field) => new("Expected: a recorded compliance field with its declared type and range. Actual: missing or invalid " + field +
        ". Suggestions: use the original complete evidence. Because: missing values cannot be reconstructed as zero or a default identity.");
}
