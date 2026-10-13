// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using AgentEval.MissionControl.Hosting;
using AgentEval.Results.Ingestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentEval.Tests.MissionControl;

public sealed class IngestionHostingTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-ingestion-" + Guid.NewGuid().ToString("N"));

    private ServiceProvider Provider(bool enabled, string? root, int capacity = 64)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentEval:Root"] = Path.Combine(_base, "viewer"),
            ["AgentEval:Ingestion:Enabled"] = enabled.ToString(),
            ["AgentEval:Ingestion:Root"] = root,
            ["AgentEval:Ingestion:MaxPendingStages"] = capacity.ToString()
        });
        IngestionHosting.Configure(builder);
        // Use only ingestion registrations so the test does not start an HTTP listener.
        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(IAefIngestionWorkspace)
            || d.ImplementationType?.Name == "IngestionLifetime"))
            services.Add(descriptor);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Configure_Disabled_RegistersNoWriterAndCreatesNoDirectory()
    {
        using var provider = Provider(false, Path.Combine(_base, "ingestion"));
        Assert.Null(provider.GetService<IAefIngestionWorkspace>());
        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.False(Directory.Exists(_base));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative")]
    public void Startup_EnabledWithoutAbsoluteRoot_RefusesWithoutWriting(string? root)
    {
        using var provider = Provider(true, root);
        Assert.Throws<InvalidOperationException>(() => provider.GetServices<IHostedService>().ToArray());
        Assert.False(Directory.Exists(_base));
    }

    [Theory]
    [InlineData("viewer/.agenteval")]
    [InlineData("viewer/.agenteval/imports")]
    [InlineData("viewer")]
    public void Startup_OverlappingViewerRoot_RefusesWithoutWriting(string relative)
    {
        using var provider = Provider(true, Path.Combine(_base, relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Throws<InvalidOperationException>(() => provider.GetServices<IHostedService>().ToArray());
        Assert.False(Directory.Exists(_base));
    }

    [Fact]
    public async Task Startup_SeparateRoot_OwnsLeaseUntilContainerDisposal()
    {
        var root = Path.Combine(_base, "viewer", ".agenteval-imports");
        using (var provider = Provider(true, root, 1))
        {
            Assert.False(Directory.Exists(_base));
            var hosted = Assert.Single(provider.GetServices<IHostedService>());
            await hosted.StartAsync(default);
            var workspace = provider.GetRequiredService<IAefIngestionWorkspace>();
            Assert.Equal(root, workspace.Root);
            workspace.BeginStage();
            Assert.Throws<IOException>(() => workspace.BeginStage());
            Assert.Throws<IOException>(() => new AefIngestionWorkspace(new() { Root = root }));
            Assert.False(Directory.Exists(Path.Combine(_base, "viewer", ".agenteval")));
            await hosted.StopAsync(default);
        }
        using var reopened = new AefIngestionWorkspace(new() { Root = root });
        Assert.True(Directory.Exists(reopened.RunsDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }
}
#endif
