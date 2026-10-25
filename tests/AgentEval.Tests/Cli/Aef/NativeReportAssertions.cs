// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Results.Adapters.Reading;

namespace AgentEval.Tests.Cli.Aef;

internal static class NativeReportAssertions
{
    internal static void MatchesProducer(string directory, string source, bool capture)
    {
        var native = new AefRunEvidenceReader().Read(directory);
        var expected = File.ReadAllBytes(Path.Combine(source, "report-native.json"));
        if (capture) Assert.Equal(expected, native.CapturedContent("E-report-native-json")?.ToArray());
        else Assert.Null(native.CapturedContent("E-report-native-json"));
#if NET10_0_OR_GREATER
        var artifacts = AgentEval.MissionControl.Services.WorkspaceArtifactProjector.Artifacts(new("native-report-proof", native, []));
        if (capture) Assert.True(Assert.Single(artifacts, a => a.EvidenceId == "E-report-native-json").Captured);
        else Assert.DoesNotContain(artifacts, a => a.EvidenceId == "E-report-native-json");
#endif
    }

}
