// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Creates deterministic, domain-separated UTF-8 bytes for containment-release signatures.</summary>
public static class ContainmentReleaseAuthorizationCanonicalizer
{
    /// <summary>The signed-payload domain and schema identifier.</summary>
    public const string Domain = "agenteval.gatekeeper.containment-release/1";

    /// <summary>Creates the canonical payload for a received authorization.</summary>
    public static byte[] CreatePayload(ContainmentReleaseAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return CreatePayloadCore(
            authorization.Target,
            authorization.ExpectedVersion,
            authorization.OperatorId,
            authorization.IssuedAtUtc,
            authorization.ExpiresAtUtc,
            authorization.Nonce,
            authorization.Algorithm,
            authorization.AlgorithmVersion,
            authorization.KeyId);
    }

    /// <summary>
    /// Creates canonical bytes from unsigned fields so an operator can sign them before constructing the final
    /// <see cref="ContainmentReleaseAuthorization"/>.
    /// </summary>
    public static byte[] CreatePayload(
        ContainmentTarget target,
        long expectedVersion,
        string operatorId,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        string nonce,
        string algorithm,
        int algorithmVersion,
        string keyId)
    {
        // Reuse the public model's complete validation without inventing a second set of acceptance rules.
        var validated = new ContainmentReleaseAuthorization(
            target,
            expectedVersion,
            operatorId,
            issuedAtUtc,
            expiresAtUtc,
            nonce,
            algorithm,
            algorithmVersion,
            keyId,
            signature: "0000000000000000");

        return CreatePayload(validated);
    }

    private static byte[] CreatePayloadCore(
        ContainmentTarget target,
        long expectedVersion,
        string operatorId,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        string nonce,
        string algorithm,
        int algorithmVersion,
        string keyId)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("domain", Domain);
            writer.WriteString("tenant", target.Tenant);
            writer.WriteNumber("targetKind", (int)target.Kind);
            writer.WriteString("targetIdentifier", target.Identifier);
            writer.WriteNumber("expectedVersion", expectedVersion);
            writer.WriteString("operatorId", operatorId);
            writer.WriteString("issuedAtUtc", issuedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("expiresAtUtc", expiresAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("nonce", nonce);
            writer.WriteString("algorithm", algorithm);
            writer.WriteNumber("algorithmVersion", algorithmVersion);
            writer.WriteString("keyId", keyId);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
