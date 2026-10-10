// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentEval.Results.Integrity;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters;

/// <summary>
/// What a conversion into AEF wrote: an imported run ([RUN-15], contracts/aef/1/spec/03-run.md) whose
/// <c>producer</c> is the converter and whose <c>imported</c> names the source.
/// </summary>
/// <param name="Directory">The AEF run folder.</param>
/// <param name="RunId">Its <c>runId</c>.</param>
/// <param name="From">run.json <c>imported.from</c>: the source tool and version.</param>
/// <param name="Asserted">run.json <c>imported.asserted</c>: the fields the converter supplied rather than read.</param>
/// <param name="Notes">What the source does not record, and what the converter did about it, in plain words.</param>
/// <param name="Verification">The run verifier's report on the folder as left: <c>unsealed</c>, or <c>intact</c> once sealed.</param>
/// <param name="Seal">The seal, when the conversion sealed the run.</param>
public sealed record AefConversion(
    string Directory,
    string RunId,
    string From,
    IReadOnlyList<string> Asserted,
    IReadOnlyList<string> Notes,
    AefRunVerification Verification,
    AefSealResult? Seal);

/// <summary>How a conversion writes the AEF run, and whether it seals it.</summary>
public abstract record AefConversionOptions
{
    /// <summary>The converter, as run.json's <c>producer</c> ([RUN-15]). Default: <c>agenteval-cli</c> and AgentEval's version.</summary>
    public AefProducer? Producer { get; init; }

    /// <summary>
    /// What text the AEF run keeps ([RUN-11]). <see cref="AefContentCapture.On"/> (the default) carries the source's
    /// prompts, responses, transcripts and judge reasoning into blobs; <see cref="AefContentCapture.Off"/> writes none of
    /// them, and no digest of one. Neither source records a capture policy, so the value is always the converter's
    /// (listed in <c>imported.asserted</c>).
    /// </summary>
    public AefContentCapture ContentCapture { get; init; } = AefContentCapture.On;

    /// <summary>
    /// Whether to seal the run once written (default true), with <c>sealedBy: ingest</c>: the converter takes custody of
    /// output another tool (or an earlier AgentEval) wrote, so the seal shows the conversion did not change it afterwards,
    /// not that the original was unchanged before (§7.5).
    /// </summary>
    public bool Seal { get; init; } = true;

    /// <summary>A signer for <c>attestation.dsse.json</c> when sealing ([SIG-1]), or null to seal unsigned.</summary>
    public IAefSigner? Signer { get; init; }

    /// <summary>The clock the seal's <c>sealedAt</c> is read from; the system's when null.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>What the converters share: the producer, typed references, digests, text limits, closing and sealing.</summary>
internal static partial class AefConverter
{
    /// <summary>run.json <c>producer.name</c> of a converted run.</summary>
    public const string ProducerName = "agenteval-cli";

    // The writer schemas' shapes the converters must meet before the writer sees a value (common.schema.json).
    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,128}\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[!-~]{1,128}\z")]
    private static partial Regex ExactVersionPattern();

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}\z")]
    private static partial Regex Sha256UriPattern();

    // common#/$defs/ref's kind ([ENC-13]): a lower-case letter, then lower-case letters, digits and '-', 32 at most.
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z")]
    private static partial Regex RefKindPattern();

    // run.schema.json deployment.endpoint: scheme, host and path only (RUN-10).
    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://[!""$-.0->A-~]+(/[!""$->@-~]*)?\z")]
    private static partial Regex EndpointPattern();

    /// <summary>AgentEval's version, as run.json <c>producer.version</c> writes it: the release version, without build metadata.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>The default converter: <c>agenteval-cli</c> at <see cref="Version"/>.</summary>
    public static AefProducer DefaultProducer { get; } = new() { Name = ProducerName, Version = Version };

    /// <summary>A <c>common#/$defs/id</c>: 1–128 letters, digits and <c>. _ : -</c>.</summary>
    public static bool IsId(string? text) => text is not null && IdPattern().IsMatch(text);

    /// <summary>An exact version ([ENC-10]): printable ASCII without spaces, at most 128 characters, never <c>latest</c>.</summary>
    public static bool IsExactVersion(string? text) =>
        text is not null && ExactVersionPattern().IsMatch(text) && !text.Equals("latest", StringComparison.OrdinalIgnoreCase);

    /// <summary>A digest as AEF writes it, <c>sha256:</c> and 64 lower-case hex; a bare 64-hex digest gets the prefix; anything else is null.</summary>
    public static string? Sha256Uri(string? digest)
    {
        if (digest is null)
        {
            return null;
        }

        var candidate = digest.Length == 64 ? "sha256:" + digest.ToLowerInvariant() : digest;
        return Sha256UriPattern().IsMatch(candidate) ? candidate : null;
    }

    /// <summary>The SHA-256 of <paramref name="bytes"/> as <c>sha256:</c> and 64 lower-case hex.</summary>
    public static string Sha256Of(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// A typed reference <c>kind:name</c> (common#/$defs/ref: a kind of 1 to 32 characters, then printable ASCII without
    /// spaces, at most 256 characters after the kind), its name derived from free text as [ENC-13] says: a byte outside
    /// <c>!</c>–<c>~</c>, and <c>%</c>, is percent-encoded (UTF-8, upper-case hex); an empty name is <c>-</c>, and a name
    /// that is exactly <c>-</c> is <c>%2D</c>, so the two stay apart; a name still longer than 256 is cut to its first 239
    /// characters and ends with <c>~</c> and 16 hex characters of the SHA-256 of its UTF-8 bytes, so two long names stay
    /// apart.
    /// </summary>
    /// <exception cref="ArgumentException">The kind is not a lower-case letter and then at most 31 lower-case letters, digits and <c>-</c>.</exception>
    public static string TypedRef(string kind, string name)
    {
        if (!RefKindPattern().IsMatch(kind))
        {
            throw new ArgumentException($"'{kind}' is not a ref's kind: a lower-case letter, then lower-case letters, digits and '-', 32 characters at most ([ENC-13]).", nameof(kind));
        }

        var text = new StringBuilder(name.Length);
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            if (b is >= 0x21 and <= 0x7E && b != (byte)'%')
            {
                text.Append((char)b);
            }
            else
            {
                text.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        var encoded = text.ToString() switch
        {
            "" => "-",
            "-" => "%2D",   // [ENC-13]: so a name of "-" and an empty name stay apart
            var written => written,
        };
        if (encoded.Length > 256)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0, 8).ToLowerInvariant();
            encoded = encoded[..(256 - 17)] + "~" + hash;
        }

        return $"{kind}:{encoded}";
    }

    /// <summary>
    /// <paramref name="url"/> as run.json <c>deployment.endpoint</c> may hold it (scheme, host and path; [RUN-10]), or
    /// null when it holds user information, a query or a fragment, where credentials hide, or is not such a URL.
    /// </summary>
    public static string? Endpoint(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && url.Length <= 2048 && EndpointPattern().IsMatch(url)
            ? url
            : null;

    /// <summary>Whether <paramref name="url"/> is an absolute http(s) URL.</summary>
    public static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The URL without user information, query and fragment (for a typed reference, which is kept).</summary>
    public static string WithoutCredentials(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped) : url;

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="max"/> code points (JSON Schema counts code points), ending
    /// with <c>…</c> when cut; null for null or blank text.
    /// </summary>
    public static string? Text(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var runes = text.EnumerateRunes().ToList();
        if (runes.Count <= max)
        {
            return text;
        }

        var cut = new StringBuilder(text.Length);
        foreach (var rune in runes.Take(max - 1))
        {
            cut.Append(rune.ToString());
        }

        return cut.Append('…').ToString();
    }

    /// <summary>Whether <paramref name="text"/> is a <c>caseId</c> or <c>path</c> ([RES-4]): no control character (C0, DEL, C1).</summary>
    public static bool IsResultText(string text, int max) =>
        text.Length > 0 && Text(text, max) == text && !text.Any(c => c < 0x20 || c is >= '\u007f' and <= '\u009f');

    /// <summary>
    /// Closes the run (computing summary.json, verified <c>unsealed</c> with no problem), and seals it as a host taking
    /// custody when <see cref="AefConversionOptions.Seal"/> is set.
    /// </summary>
    public static AefConversion Finish(
        AefRunWriter writer, AefConversionOptions options, string from, IReadOnlyList<string> asserted, IReadOnlyList<string> notes,
        AefRunStatus status, AefTime endedAt, string? abortReason)
    {
        var verification = writer.Close(status, endedAt, abortReason);
        AefSealResult? seal = null;
        if (options.Seal)
        {
            seal = AefSealer.Seal(writer.Directory, new AefSealOptions
            {
                SealedBy = AefSealedBy.Ingest,
                Signer = options.Signer,
                TimeProvider = options.TimeProvider,
            });
            verification = seal.Verification;
        }

        return new AefConversion(writer.Directory, writer.RunId, from, asserted, notes, verification, seal);
    }

    /// <summary>
    /// Removes what a failed conversion wrote: the folder, or, when it existed (empty) before, its contents. A half-written
    /// run is never left behind to be mistaken for a converted one.
    /// </summary>
    public static void Discard(string directory, bool existed)
    {
        try
        {
            if (!System.IO.Directory.Exists(directory))
            {
                return;
            }

            if (!existed)
            {
                System.IO.Directory.Delete(directory, recursive: true);
                return;
            }

            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo folder)
                {
                    folder.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the conversion's own error is the one to report.
        }
    }

    private static string ReadVersion()
    {
        var assembly = typeof(AefConverter).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (version is not null && version.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0)
        {
            version = version[..plus];
        }

        return IsExactVersion(version) ? version! : assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
