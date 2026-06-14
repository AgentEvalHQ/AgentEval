// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// NextWave #10: benchmark PackDownloader + license gate. Nothing is bundled; nothing is fetched until --accept-license.
// Transport is injected so the download→import pipeline is fully offline-deterministic.
using System.Net;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Benchmarks;

namespace AgentEval.Tests.RedTeam.Benchmarks;

public class PackDownloaderTests
{
    private const string ValidSeedJson = """
        [
          { "prompt": "harmful behavior one", "id": "H-1", "technique": "jailbreak", "expectedTokens": ["PWNED"] },
          { "prompt": "harmful behavior two", "id": "H-2" }
        ]
        """;

    private static BenchmarkPack TestPack(bool requiresLicense = true) => new()
    {
        Name = "TestPack",
        Description = "unit-test pack",
        License = "MIT",
        LicenseUrl = "https://example.test/license",
        HomeUrl = "https://example.test/",
        DataUrl = "https://example.test/seed.json",
        OwaspLlmId = "LLM02",
        RequiresLicenseAcceptance = requiresLicense,
    };

    // Returns a canned response (status + optional body) regardless of URL.
    private sealed class StubHandler(HttpStatusCode status, string? body = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(body ?? "") });
    }

    // Throws if any request is sent — proves the license gate short-circuits before the network.
    private sealed class ThrowIfCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("network must not be touched");
    }

    private static PackDownloader Downloader(HttpMessageHandler handler)
        => new(new HttpClient(handler));

    // ── license gate ──────────────────────────────────────────────────────────

    [Fact]
    public async Task LicenseNotAccepted_Throws_BeforeAnyNetworkCall()
    {
        using var d = Downloader(new ThrowIfCalledHandler());   // would throw a DIFFERENT exception if HTTP ran
        await Assert.ThrowsAsync<LicenseNotAcceptedException>(
            () => d.DownloadAsync(TestPack(), licenseAccepted: false));
    }

    [Fact]
    public async Task LicenseNotRequired_Downloads_WithoutAcceptance()
    {
        using var d = Downloader(new StubHandler(HttpStatusCode.OK, ValidSeedJson));
        var attack = await d.DownloadAsync(TestPack(requiresLicense: false), licenseAccepted: false);
        Assert.Equal(2, attack.GetProbes(Intensity.Comprehensive).Count);
    }

    // ── happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Accepted_DownloadsAndImports_AsImportedProbeAttack()
    {
        using var d = Downloader(new StubHandler(HttpStatusCode.OK, ValidSeedJson));
        var attack = await d.DownloadAsync(TestPack(), licenseAccepted: true);

        Assert.Equal("TestPack", attack.Name);
        Assert.Equal("LLM02", attack.OwaspLlmId);
        Assert.Equal(2, attack.GetProbes(Intensity.Comprehensive).Count);
    }

    // ── honest failure surfaces ─────────────────────────────────────────────

    [Fact]
    public async Task MalformedPackData_Surfaces_FormatException()
    {
        using var d = Downloader(new StubHandler(HttpStatusCode.OK, "this is not json"));
        await Assert.ThrowsAsync<FormatException>(
            () => d.DownloadAsync(TestPack(), licenseAccepted: true));
    }

    [Fact]
    public async Task FailedDownload_Surfaces_HttpRequestException()
    {
        using var d = Downloader(new StubHandler(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => d.DownloadAsync(TestPack(), licenseAccepted: true));
    }

    // ── catalog ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("HarmBench")]
    [InlineData("jailbreakbench")]   // case-insensitive
    [InlineData("CyberSecEval")]
    public void Catalog_Find_ResolvesKnownPacks(string name)
        => Assert.NotNull(PackCatalog.Find(name));

    [Fact]
    public void Catalog_Find_UnknownPack_IsNull()
        => Assert.Null(PackCatalog.Find("NoSuchPack"));

    [Fact]
    public void Catalog_All_ContainsThreePacks_WithLicenseGate()
    {
        Assert.Equal(3, PackCatalog.All.Count);
        Assert.All(PackCatalog.All, p =>
        {
            Assert.True(p.RequiresLicenseAcceptance);    // harmful-content packs are gated by default
            Assert.False(string.IsNullOrWhiteSpace(p.License));
            Assert.False(string.IsNullOrWhiteSpace(p.DataUrl));
            Assert.False(string.IsNullOrWhiteSpace(p.PromptField));
            Assert.True(p.Format is "json" or "csv");
        });
    }

    [Fact]
    public void Catalog_RealFormatsAndColumns_AreSet()
    {
        var harm = PackCatalog.Find("HarmBench")!;
        Assert.Equal("csv", harm.Format);
        Assert.Equal("Behavior", harm.PromptField);
        var jbb = PackCatalog.Find("JailbreakBench")!;
        Assert.Equal("csv", jbb.Format);
        Assert.Equal("Goal", jbb.PromptField);
        var cse = PackCatalog.Find("CyberSecEval")!;
        Assert.Equal("json", cse.Format);
        Assert.Equal("test_case_prompt", cse.PromptField);
    }

    // ── format dispatch (csv vs json) ─────────────────────────────────────────

    [Fact]
    public void ImporterFor_SelectsByFormat()
    {
        Assert.Equal("csv", PackDownloader.ImporterFor(PackCatalog.Find("HarmBench")!).Format);
        Assert.Equal("json", PackDownloader.ImporterFor(PackCatalog.Find("CyberSecEval")!).Format);
    }

    [Fact]
    public async Task Downloads_CsvPack_AndParsesConfiguredColumn()
    {
        const string csv = "Behavior,FunctionalCategory\nharmful one,standard\nharmful two,standard\n";
        using var d = Downloader(new StubHandler(HttpStatusCode.OK, csv));
        var pack = TestPack() with { Format = "csv", PromptField = "Behavior" };
        var attack = await d.DownloadAsync(pack, licenseAccepted: true);
        Assert.Equal(2, attack.GetProbes(Intensity.Comprehensive).Count);
    }

    [Fact]
    public async Task UnsupportedFormat_Surfaces_FormatException()
    {
        using var d = Downloader(new StubHandler(HttpStatusCode.OK, "x"));
        var pack = TestPack() with { Format = "parquet" };
        await Assert.ThrowsAsync<FormatException>(() => d.DownloadAsync(pack, licenseAccepted: true));
    }

    // ── --pack <url> ad-hoc pack ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.test/seed.json", "json")]
    [InlineData("https://example.test/data.csv", "csv")]
    [InlineData("https://example.test/harmful-behaviors.csv?ref=main", "csv")]   // L15: query must not defeat .csv
    [InlineData("https://example.test/data.csv#frag", "csv")]                    // L15: fragment too
    public void ForUrl_InfersFormat_AndIsNotLicenseGated(string url, string expectedFormat)
    {
        var pack = PackCatalog.ForUrl(url);
        Assert.Equal(expectedFormat, pack.Format);
        Assert.False(pack.RequiresLicenseAcceptance);   // user-supplied source → no gate
        Assert.Equal(url, pack.DataUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForUrl_EmptyOrWhitespace_Throws(string url)
        => Assert.Throws<ArgumentException>(() => PackCatalog.ForUrl(url));

    [Fact]
    public async Task HtmlResponse_Surfaces_ClearFormatException()
    {
        // A gated/login HTML page returned with a 200 must be named clearly, not a confusing parse error.
        using var d = Downloader(new StubHandler(HttpStatusCode.OK, "<!DOCTYPE html><html><body>login</body></html>"));
        var ex = await Assert.ThrowsAsync<FormatException>(() => d.DownloadAsync(TestPack(), licenseAccepted: true));
        Assert.Contains("HTML", ex.Message);
    }

    [Fact] // L14: a CSV pack that comes back as a JSON ERROR ENVELOPE (login wall / rate-limit) names the cause.
    public async Task CsvPack_ReturnsJsonEnvelope_Surfaces_ClearFormatException()
    {
        using var d = Downloader(new StubHandler(HttpStatusCode.OK, """{"error":"rate limited"}"""));
        var pack = TestPack() with { Format = "csv", PromptField = "Behavior" };
        var ex = await Assert.ThrowsAsync<FormatException>(() => d.DownloadAsync(pack, licenseAccepted: true));
        Assert.Contains("JSON error envelope", ex.Message);
    }

    [Fact] // Jun14-L14: a LEGITIMATE CSV whose first cell begins with '[' (or '{') must NOT be false-rejected.
    public async Task CsvPack_StartingWithBracket_IsNotFalseRejected()
    {
        const string csv = "Behavior,Category\n\"[INST] do the bad thing [/INST]\",jailbreak\n\"{role:user}\",chat\n";
        using var d = Downloader(new StubHandler(HttpStatusCode.OK, csv));
        var pack = TestPack() with { Format = "csv", PromptField = "Behavior" };

        var attack = await d.DownloadAsync(pack, licenseAccepted: true);

        Assert.Equal(2, attack.GetProbes(Intensity.Comprehensive).Count);
    }
}
