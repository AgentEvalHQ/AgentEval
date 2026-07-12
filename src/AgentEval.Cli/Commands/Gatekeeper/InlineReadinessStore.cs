// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Guardrails.Judges;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>
/// The calibration-certificate cache — the CLI's honest backing for <c>inlineReady</c> (which has no source in the
/// codebase: it exists only as the output of a live <c>CalibrateAsync</c> run). <c>calibrate --certify</c> writes a
/// certificate keyed on <c>(axis, modelFingerprint)</c>; <c>inspect</c> reads one. A judge is inline-ready only when
/// a matching certificate says so — otherwise the guard refuses (exit 7) unless <c>--allow-uncalibrated</c>.
/// <para><b>Not a crypto barrier</b> (design §7.3): the certificate is an unsigned local JSON file. The guard prevents
/// <i>accidental</i> promotion of an un-calibrated judge, not a malicious local operator.</para>
/// </summary>
internal static class InlineReadinessStore
{
    private static readonly Regex Unsafe = new("[^a-zA-Z0-9._-]", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>Default cert directory under the workspace: <c>.agenteval/gatekeeper/certs</c>.</summary>
    public static string DefaultDir => Path.Combine(".agenteval", "gatekeeper", "certs");

    /// <summary>
    /// The certificate path for an <c>(axis, fingerprint)</c> pair. The fingerprint is <b>hashed</b> into the
    /// filename so a long user-supplied model/deployment name can't exceed the filesystem path limit — the full
    /// fingerprint is still stored inside the cert and verified on load.
    /// </summary>
    public static string PathFor(string axis, string fingerprint, string? dir = null) =>
        Path.Combine(dir ?? DefaultDir, $"{Safe(axis)}@{FingerprintHash(fingerprint)}.json");

    private static string FingerprintHash(string fingerprint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..16].ToLowerInvariant();

    /// <summary>
    /// A stable hash of a gold set's actual <b>content</b> (axis + each case's label + text) so a certificate is tied
    /// to the corpus it was scored on — changing the gold-set texts (even at the same counts) invalidates a stale cert.
    /// </summary>
    public static string GoldSetHash(JudgeGoldSet gold)
    {
        var sb = new StringBuilder(gold.Axis).Append('\n');
        foreach (var c in gold.Cases)
        {
            sb.Append(c.ShouldBlock ? '1' : '0').Append('|').Append(c.Text).Append('\n');
        }

        // Full digest (not truncated): this value is surfaced verbatim in the certificate/provenance, so the "sha256:"
        // label must be honest. Unlike the fingerprint FILENAME hash it isn't path-length bounded, so there's no reason
        // to shorten it — the full digest also removes any (already-tiny) truncated-collision risk.
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>Load the certificate matching <c>(axis, fingerprint)</c>, or null if none / unreadable.</summary>
    public static CalibrationCertificate? TryLoad(string axis, string fingerprint, string? dir = null)
    {
        var path = PathFor(axis, fingerprint, dir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var cert = JsonSerializer.Deserialize<CalibrationCertificate>(File.ReadAllText(path), Json);
            // guard against a mismatched/hand-edited cert being used for the wrong axis/model
            return cert is not null && cert.Axis == axis && cert.ModelFingerprint == fingerprint ? cert : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Write a certificate. Returns the path written.</summary>
    public static string Save(CalibrationCertificate cert, string? path = null, string? dir = null)
    {
        path ??= PathFor(cert.Axis, cert.ModelFingerprint, dir);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(cert, Json));
        return path;
    }

    private static string Safe(string s) => Unsafe.Replace(s, "_");
}
