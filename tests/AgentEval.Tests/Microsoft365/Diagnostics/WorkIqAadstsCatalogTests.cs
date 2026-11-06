// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Diagnostics;

/// <summary>
/// <see cref="WorkIqAadstsCatalog"/>: the codes that matter are catalogued with a cause and a fix, the regex finds
/// a code in any message form, the normaliser accepts every spelling, the unknown-code fallback keeps the raw code
/// and points at the public reference, and MSAL errors without a code get a plain-language cause.
/// </summary>
public sealed class WorkIqAadstsCatalogTests
{
    private static readonly string[] CodesThatMatter =
    [
        "AADSTS65001",
        "AADSTS700016",
        "AADSTS50020",
        "AADSTS7000218",
        "AADSTS50076",
        "AADSTS50079",
        "AADSTS50058",
        "AADSTS70011",
        "AADSTS65002",
        "AADSTS50105",
        "AADSTS530003",
        "AADSTS53003",
        "AADSTS900144",
        "AADSTS90002",
        "AADSTS50059",
        "AADSTS90019",
        "AADSTS65004",
        "AADSTS650052",
        "AADSTS90094",
        "AADSTS7000215",
    ];

    [Theory]
    [InlineData("AADSTS65001", true)]
    [InlineData("AADSTS65004", true)]
    [InlineData("AADSTS650052", true)]
    [InlineData("AADSTS90094", true)]
    [InlineData("AADSTS53003", true)]
    [InlineData("AADSTS7000215", true)]
    [InlineData("aadsts65001", true)]
    [InlineData("65001", true)]
    [InlineData("AADSTS700016", false)]
    [InlineData("AADSTS50076", false)]
    [InlineData("AADSTS99999", false)]
    [InlineData(null, false)]
    public void IsPermissionRefusal_IsAFlagOnTheCatalogEntries(string? code, bool expected) =>
        Assert.Equal(expected, WorkIqAadstsCatalog.IsPermissionRefusal(code));

    [Fact]
    public void WorkIqScope_IsTheConfigDefaultScope() =>
        Assert.Equal(Microsoft365Config.DefaultScope, WorkIqAadstsCatalog.WorkIqScope);

    [Fact]
    public void ReadinessCodes_AreCataloguedWithTheirCause()
    {
        Assert.Contains("tenant", WorkIqAadstsCatalog.Entries[WorkIqAadstsCatalog.TenantNotFoundCode].Cause, StringComparison.Ordinal);
        Assert.Contains("application was not found", WorkIqAadstsCatalog.Entries[WorkIqAadstsCatalog.ApplicationNotFoundCode].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_Always_ContainEveryCodeThatMattersWithACauseAndAFix()
    {
        foreach (var code in CodesThatMatter)
        {
            Assert.True(WorkIqAadstsCatalog.Entries.ContainsKey(code), code);
            var entry = WorkIqAadstsCatalog.Entries[code];
            Assert.Equal(code, entry.Code);
            Assert.False(string.IsNullOrWhiteSpace(entry.Cause), code);
            Assert.False(string.IsNullOrWhiteSpace(entry.Fix), code);
            Assert.StartsWith(code + ": ", entry.Describe(), StringComparison.Ordinal);
            Assert.Contains(" → ", entry.Describe(), StringComparison.Ordinal);
        }

        Assert.Equal(CodesThatMatter.Length, WorkIqAadstsCatalog.Entries.Count);
    }

    [Theory]
    [InlineData("AADSTS90002", "tenantId")]
    [InlineData("AADSTS50059", "tenantId")]
    [InlineData("AADSTS90019", "tenantId")]
    public void TryGet_TenantNotFoundCodes_NameTheTenantIdAsTheFix(string code, string field)
    {
        // the placeholder-tenant case, the most predictable first failure, gets a cause and a fix.
        Assert.True(WorkIqAadstsCatalog.TryGet(code, out var entry));
        Assert.Contains(field, entry.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain("not in this catalog", WorkIqAadstsCatalog.Describe(code), StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_Always_LookUpCaseInsensitively()
    {
        Assert.True(WorkIqAadstsCatalog.Entries.ContainsKey("aadsts65001"));
    }

    [Theory]
    [InlineData("AADSTS65001: The user or administrator has not consented", "AADSTS65001")]
    [InlineData("error_description: aadsts700016 Application with identifier was not found", "AADSTS700016")]
    [InlineData("first AADSTS50058 then AADSTS65001", "AADSTS50058")]
    [InlineData("AADSTS7000218", "AADSTS7000218")]
    [InlineData("no code here", null)]
    [InlineData("AADSTS without digits", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractCode_Variants_FindsTheFirstCodeUpperCased(string? text, string? expected) =>
        Assert.Equal(expected, WorkIqAadstsCatalog.ExtractCode(text));

    [Theory]
    [InlineData("65001", "AADSTS65001")]
    [InlineData("AADSTS65001", "AADSTS65001")]
    [InlineData("aadsts65001", "AADSTS65001")]
    [InlineData("  AADSTS65001  ", "AADSTS65001")]
    [InlineData("AADSTS", null)]
    [InlineData("abc", null)]
    [InlineData("65001x", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Normalize_Forms_AcceptsEverySpellingAndRejectsNonCodes(string? code, string? expected) =>
        Assert.Equal(expected, WorkIqAadstsCatalog.Normalize(code));

    [Fact]
    public void TryGet_KnownAndUnknown_ReportsCorrectly()
    {
        Assert.True(WorkIqAadstsCatalog.TryGet("65001", out var entry));
        Assert.Equal("AADSTS65001", entry!.Code);
        Assert.True(WorkIqAadstsCatalog.TryGet("aadsts50076", out _));
        Assert.False(WorkIqAadstsCatalog.TryGet("AADSTS999999", out var missing));
        Assert.Null(missing);
        Assert.False(WorkIqAadstsCatalog.TryGet(null, out _));
        Assert.False(WorkIqAadstsCatalog.TryGet("nonsense", out _));
    }

    [Theory]
    [InlineData("AADSTS65001", "consent", "Grant admin consent")]
    [InlineData("AADSTS700016", "not found in the tenant", "appClientId")]
    [InlineData("AADSTS50020", "does not exist in the tenant", "tenantId")]
    [InlineData("AADSTS7000218", "public client flows are disabled", "Allow public client flows")]
    [InlineData("AADSTS50076", "multi-factor authentication is required", "complete MFA")]
    [InlineData("AADSTS50079", "enrol", "enrol for MFA")]
    [InlineData("AADSTS50058", "silent sign-in", "device-code sign-in")]
    [InlineData("AADSTS70011", "scope is invalid", WorkIqAadstsCatalog.WorkIqScope)]
    [InlineData("AADSTS65002", "first-party", "your own app registration")]
    [InlineData("AADSTS50105", "not assigned to the application", "assigns the user")]
    [InlineData("AADSTS530003", "managed or compliant", "compliant device")]
    [InlineData("AADSTS53003", "conditional access policy", "reviews the conditional access policies")]
    [InlineData("AADSTS900144", "required parameter is missing", "tenantId, appClientId and the scope")]
    public void Describe_KnownCode_GivesThePlainLanguageCauseAndTheFix(string code, string causeFragment, string fixFragment)
    {
        var described = WorkIqAadstsCatalog.Describe(code);

        Assert.StartsWith(code + ": ", described, StringComparison.Ordinal);
        Assert.Contains(causeFragment, described, StringComparison.Ordinal);
        Assert.Contains(fixFragment, described, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_UnknownCode_FallbackKeepsTheRawCodeAndPointsAtTheReference()
    {
        var described = WorkIqAadstsCatalog.Describe("AADSTS999999");

        Assert.StartsWith("AADSTS999999: ", described, StringComparison.Ordinal);
        Assert.Contains("not in this catalog", described, StringComparison.Ordinal);
        Assert.Contains(WorkIqAadstsCatalog.ReferenceUrl, described, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a code")]
    public void Describe_NoCode_SaysSoAndPointsAtTheReference(string? code)
    {
        var described = WorkIqAadstsCatalog.Describe(code);

        Assert.Contains("no AADSTS code", described, StringComparison.Ordinal);
        Assert.Contains(WorkIqAadstsCatalog.ReferenceUrl, described, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_Always_AcceptsEveryNormalizedSpelling()
    {
        var expected = WorkIqAadstsCatalog.Describe("AADSTS65001");

        Assert.Equal(expected, WorkIqAadstsCatalog.Describe("65001"));
        Assert.Equal(expected, WorkIqAadstsCatalog.Describe(" aadsts65001 "));
    }

    [Theory]
    [InlineData("code_expired", false, "device code expired")]
    [InlineData("authentication_canceled", false, "cancelled")]
    [InlineData("invalid_grant", false, "no longer valid")]
    [InlineData("user_null", true, "no account is signed in")]
    [InlineData("invalid_scope", false, WorkIqAadstsCatalog.WorkIqScope)]
    [InlineData("unauthorized_client", false, "Allow public client flows")]
    public void DescribeMsalError_KnownCode_GivesTheCause(string errorCode, bool interactionRequired, string fragment)
    {
        var described = WorkIqAadstsCatalog.DescribeMsalError(errorCode, interactionRequired);

        Assert.StartsWith(errorCode + ": ", described, StringComparison.Ordinal);
        Assert.Contains(fragment, described, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeMsalError_UnknownCodeWithInteractionRequired_DeviceCodeHint()
    {
        var described = WorkIqAadstsCatalog.DescribeMsalError("something_new", interactionRequired: true);

        Assert.StartsWith("something_new: ", described, StringComparison.Ordinal);
        Assert.Contains("device-code sign-in", described, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeMsalError_UnknownCode_KeepsTheCodeAndPointsAtTheMsalReference()
    {
        var described = WorkIqAadstsCatalog.DescribeMsalError("something_new", interactionRequired: false);

        Assert.StartsWith("something_new: ", described, StringComparison.Ordinal);
        Assert.Contains(WorkIqAadstsCatalog.MsalReferenceUrl, described, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void DescribeMsalError_NoCode_SaysSo(string? errorCode)
    {
        Assert.Contains("without an error code", WorkIqAadstsCatalog.DescribeMsalError(errorCode, interactionRequired: false), StringComparison.Ordinal);
        Assert.Contains("interaction_required", WorkIqAadstsCatalog.DescribeMsalError(errorCode, interactionRequired: true), StringComparison.Ordinal);
    }
}
