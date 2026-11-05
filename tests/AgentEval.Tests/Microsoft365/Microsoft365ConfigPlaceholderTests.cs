// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365;
using Xunit;

namespace AgentEval.Tests.Microsoft365;

/// <summary>
/// Phase-2 open issue: <see cref="Microsoft365Config.Validate"/> rejects the config template's placeholders
/// (<c>REPLACE-WITH-…</c>) with a message naming the field, so an untouched template fails at the config stage — with
/// nothing sent — instead of at sign-in with AADSTS90002.
/// </summary>
public sealed class Microsoft365ConfigPlaceholderTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string App = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Validate_TenantPlaceholder_ThrowsNamingTheField()
    {
        var config = new Microsoft365Config { TenantId = "REPLACE-WITH-ENTRA-TENANT-GUID", AppClientId = App };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);

        Assert.Contains("field 'tenantId'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("template placeholder 'REPLACE-WITH-ENTRA-TENANT-GUID'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Directory (tenant) ID", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was sent", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("appClientId")]
    [InlineData("agentId")]
    [InlineData("agentName")]
    [InlineData("timeZone")]
    [InlineData("diagnosticProfile")]
    [InlineData("scope")]
    [InlineData("trustedHosts")]
    public void Validate_PlaceholderInAnyField_ThrowsNamingThatField(string field)
    {
        const string placeholder = "replace-with-something";   // case-insensitive, like a hand-edited template
        var config = field switch
        {
            "appClientId" => new Microsoft365Config { TenantId = Tenant, AppClientId = placeholder },
            "agentId" => new Microsoft365Config { TenantId = Tenant, AppClientId = App, AgentId = placeholder },
            "agentName" => new Microsoft365Config { TenantId = Tenant, AppClientId = App, AgentName = placeholder },
            "timeZone" => new Microsoft365Config { TenantId = Tenant, AppClientId = App, TimeZone = placeholder },
            "diagnosticProfile" => new Microsoft365Config { TenantId = Tenant, AppClientId = App, DiagnosticProfile = placeholder },
            "scope" => new Microsoft365Config { TenantId = Tenant, AppClientId = App, Scope = placeholder },
            _ => new Microsoft365Config { TenantId = Tenant, AppClientId = App, TrustedHosts = [Microsoft365Config.DefaultTrustedHost, placeholder] },
        };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);

        Assert.Contains($"field '{field}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("template placeholder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_TheShippedTemplateUntouched_FailsAtTheConfigNamingTenantId()
    {
        var template = FindTemplate();

        var ex = Assert.Throws<InvalidOperationException>(() => Microsoft365Config.Load(template));

        Assert.Contains("field 'tenantId'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Microsoft365Config.TemplatePlaceholderPrefix, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RealValues_PassAndResolveToTheseValues()
    {
        // "Replace me later" is prose, not the REPLACE-WITH- prefix: it must survive validation unchanged.
        var config = new Microsoft365Config { TenantId = Tenant, AppClientId = App, AgentName = "Replace me later (not a placeholder)" };

        config.Validate();

        Assert.Equal("Replace me later (not a placeholder)", config.DisplayName);
        Assert.Equal(Tenant, config.TenantId);
        Assert.Equal(App, config.AppClientId);
        Assert.Equal(Microsoft365Config.DefaultScope, config.Scope);
        Assert.Equal(DiagnosticProfile.PublicAnswerOnly, config.ResolveProfile());
        Assert.Equal(new Uri("https://workiq.svc.cloud.microsoft/a2a/.well-known/agent-card.json"), config.ResolveCardUri());
        Assert.Equal([Microsoft365Config.DefaultTrustedHost], config.TrustedHosts);
    }

    /// <summary>The shipped template, found by walking up from the test binaries to the repository root.</summary>
    private static FileInfo FindTemplate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = new FileInfo(Path.Combine(directory.FullName, "samples", "datasets", "m365-config.template.json"));
            if (candidate.Exists)
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("samples/datasets/m365-config.template.json was not found above " + AppContext.BaseDirectory);
    }
}
