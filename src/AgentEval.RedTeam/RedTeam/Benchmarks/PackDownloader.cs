// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam.Importers;

namespace AgentEval.RedTeam.Benchmarks;

/// <summary>
/// Thrown when a benchmark pack download is attempted without the caller accepting the pack's license
/// (CLI <c>--accept-license</c>). The gate runs <b>before</b> any network call — no harmful data is fetched until the
/// user has explicitly opted in.
/// </summary>
public sealed class LicenseNotAcceptedException : Exception
{
    /// <summary>The pack whose license was not accepted.</summary>
    public BenchmarkPack Pack { get; }

    /// <summary>Creates the exception for <paramref name="pack"/>.</summary>
    public LicenseNotAcceptedException(BenchmarkPack pack)
        : base($"Pack '{pack.Name}' is distributed under license '{pack.License}' ({pack.LicenseUrl}) and contains " +
               "external (often harmful) content. Re-run with --accept-license to download it. AgentEval bundles no data.")
        => Pack = pack;
}

/// <summary>
/// Downloads an external benchmark pack and runs it through the existing seed-prompt importer seam
/// (<see cref="IProbeDatasetImporter"/>) so it executes alongside the built-in attacks (NextWave #10 / Wave-F).
/// </summary>
/// <remarks>
/// <para><b>Safety:</b> nothing is bundled and nothing is fetched until the caller accepts the pack's license — the
/// <c>--accept-license</c> gate (<see cref="LicenseNotAcceptedException"/>) is checked before the HTTP request.</para>
/// <para><b>Honesty:</b> a registry/download outage or a non-2xx response surfaces as an exception (the caller reports
/// it) rather than silently yielding zero probes; malformed pack data surfaces as <see cref="FormatException"/> from
/// the importer. Imported probes without an expected-token oracle are Inconclusive unless an LLM judge is configured.</para>
/// <para>The transport (<see cref="HttpClient"/>) and the <see cref="IProbeDatasetImporter"/> are injectable so the
/// download/import pipeline is fully offline-deterministic in tests.</para>
/// </remarks>
public sealed class PackDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IProbeDatasetImporter _importer;

    /// <summary>Creates a downloader. A null <paramref name="httpClient"/> creates (and disposes) a real client;
    /// an injected one is borrowed and left for the caller to dispose. A null importer uses the JSON importer.</summary>
    public PackDownloader(HttpClient? httpClient = null, IProbeDatasetImporter? importer = null, TimeSpan? timeout = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        _importer = importer ?? new JsonProbeDatasetImporter();
    }

    /// <summary>
    /// Downloads <paramref name="pack"/> (gated on <paramref name="licenseAccepted"/>) and returns it as an
    /// <see cref="ImportedProbeAttack"/> ready to run alongside the built-ins. Throws
    /// <see cref="LicenseNotAcceptedException"/> before any network call when the license is required and not accepted,
    /// <see cref="HttpRequestException"/> on a failed download, and <see cref="FormatException"/> on malformed pack data.
    /// </summary>
    public async Task<ImportedProbeAttack> DownloadAsync(BenchmarkPack pack, bool licenseAccepted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);

        if (pack.RequiresLicenseAcceptance && !licenseAccepted)
            throw new LicenseNotAcceptedException(pack);

        using var request = new HttpRequestMessage(HttpMethod.Get, pack.DataUrl);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Reuse the existing importer (throws FormatException on bad data — surfaced honestly, never a silent empty set).
        var probes = _importer.Import(content, pack.Name);
        return new ImportedProbeAttack(pack.Name, probes, pack.OwaspLlmId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
