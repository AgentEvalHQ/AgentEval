// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using AgentEval.Cli.Commands;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Locks down the MC1.7.4 contract: <see cref="McDoctorCommand"/> reports
/// 0 (success) when the MC bundle is intact + 2 (errors) when key
/// artefacts are missing. Uses the internal <c>RunAsync(probeDirOverride)</c>
/// overload to point at synthetic temp directories.
/// </summary>
public class McDoctorCommandTests : IDisposable
{
    private readonly string _probeDir;

    public McDoctorCommandTests()
    {
        _probeDir = Path.Combine(Path.GetTempPath(),
            "agenteval-mc-doctor-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_probeDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_probeDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task McDoctor_HappyPath_ReturnsZero()
    {
        // Stage a fully-formed bundle: MC.dll + (.exe on Windows) + wwwroot
        // with index.html + assets/*.js + assets/*.css + manifest.
        await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.dll"), "stub");
        if (OperatingSystem.IsWindows())
            await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.exe"), "stub");

        var wwwroot = Path.Combine(_probeDir, "wwwroot");
        Directory.CreateDirectory(Path.Combine(wwwroot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "index.html"), "<html/>");
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "assets", "index-abc.js"), "{}");
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "assets", "index-abc.css"), "body{}");
        await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.staticwebassets.endpoints.json"), "{}");

        var exit = await McDoctorCommand.RunAsync(_probeDir);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task McDoctor_MissingDll_ReturnsErrorExit()
    {
        // Stage everything EXCEPT the MC dll.
        var wwwroot = Path.Combine(_probeDir, "wwwroot");
        Directory.CreateDirectory(Path.Combine(wwwroot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "index.html"), "<html/>");
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "assets", "x.js"), "{}");
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "assets", "x.css"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.staticwebassets.endpoints.json"), "{}");

        var exit = await McDoctorCommand.RunAsync(_probeDir);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task McDoctor_MissingWwwroot_ReturnsErrorExit()
    {
        await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.dll"), "stub");
        if (OperatingSystem.IsWindows())
            await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.exe"), "stub");

        // No wwwroot/ at all.
        var exit = await McDoctorCommand.RunAsync(_probeDir);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task McDoctor_RuntimeManifestAlone_IsAccepted()
    {
        // Debug builds emit `…runtime.json` only (no endpoints.json). Either
        // is sufficient for MapStaticAssets — the doctor must not flag this
        // case.
        await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.dll"), "stub");
        if (OperatingSystem.IsWindows())
            await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.exe"), "stub");

        var wwwroot = Path.Combine(_probeDir, "wwwroot");
        Directory.CreateDirectory(Path.Combine(wwwroot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "index.html"), "<html/>");
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "assets", "x.js"), "{}");
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "assets", "x.css"), "{}");
        // Only runtime.json — no endpoints.json.
        await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.staticwebassets.runtime.json"), "{}");

        var exit = await McDoctorCommand.RunAsync(_probeDir);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task McDoctor_AssetsWithoutCss_ReturnsErrorExit()
    {
        // The bundle must have at least one .js AND one .css.
        await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.dll"), "stub");
        if (OperatingSystem.IsWindows())
            await File.WriteAllTextAsync(Path.Combine(_probeDir, "AgentEval.MissionControl.exe"), "stub");

        var wwwroot = Path.Combine(_probeDir, "wwwroot");
        Directory.CreateDirectory(Path.Combine(wwwroot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "index.html"), "<html/>");
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "assets", "x.js"), "{}");
        // No CSS file.

        var exit = await McDoctorCommand.RunAsync(_probeDir);
        Assert.Equal(2, exit);
    }
}

#endif
