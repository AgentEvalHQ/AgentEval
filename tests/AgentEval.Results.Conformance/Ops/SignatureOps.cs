// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Conformance.Ops;

/// <summary>
/// <c>signature ENVELOPE FILE POLICY --payload-type TYPE</c> (spec 04 §4.4; spec 09 §9.3, where the type, the one [SIG-1]
/// gives the file, is required): verifies a DSSE envelope against the file it signs under a trust policy, and writes
/// <c>{"envelopeResult": null | "malformed" | "payload-mismatch", "signatures": [{"keyid", "result", "identity"?}, …],
/// "verifiesFor": [identity, …]}</c>. A trust policy [SIG-3] refuses is an input error (exit code 2).
/// </summary>
internal static class SignatureOps
{
    private const string Usage = "usage: signature ENVELOPE FILE POLICY --payload-type TYPE";

    /// <summary>The operation.</summary>
    public static int Signature(string[] args, TextWriter stdout)
    {
        var paths = new List<string>(3);
        string? payloadType = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--payload-type")
            {
                if (payloadType is not null || i + 1 >= args.Length)
                {
                    throw new UsageException(Usage);
                }
                payloadType = args[++i];
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"unknown option {args[i]}; {Usage}");
            }
            else
            {
                paths.Add(args[i]);
            }
        }
        if (paths.Count != 3 || payloadType is null)
        {
            throw new UsageException(Usage);
        }

        var envelope = DriverIO.Bytes(paths[0]);
        var file = DriverIO.Bytes(paths[1]);
        TrustPolicy policy;
        try
        {
            policy = TrustPolicy.Parse(DriverIO.Bytes(paths[2]));
        }
        catch (TrustPolicyException e)
        {
            throw new UsageException($"{paths[2]}: {e.Message}");
        }

        var result = DsseVerifier.Verify(envelope, file, payloadType, policy);
        var signatures = new JsonArray();
        foreach (var check in result.Signatures)
        {
            var item = new JsonObject
            {
                ["keyid"] = check.KeyId,
                ["result"] = DsseVerifier.Name(check.Result),
            };
            if (check.Identity is not null)
            {
                item["identity"] = check.Identity;
            }
            signatures.Add(item);
        }
        var verifiesFor = new JsonArray();
        foreach (var identity in result.VerifiesFor)
        {
            verifiesFor.Add(identity);
        }
        var output = new JsonObject
        {
            ["envelopeResult"] = result.EnvelopeResult is { } problem ? DsseVerifier.Name(problem) : null,
            ["signatures"] = signatures,
            ["verifiesFor"] = verifiesFor,
        };
        return DriverIO.Print(stdout, output);
    }
}
