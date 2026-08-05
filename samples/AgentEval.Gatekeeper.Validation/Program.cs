// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Samples;

if (args.Length != 1)
{
    PrintUsage();
    return 2;
}

switch (args[0].ToLowerInvariant())
{
    case "calibrate":
        await GatekeeperA2ACalibration.RunAsync();
        return 0;
    case "remote":
        await GatekeeperA2ABoundary.RunAsync();
        return 0;
    case "mock-tools":
        await GatekeeperMockedDangerousTools.RunAsync();
        return 0;
    case "memory-security":
        await MemorySecurityReleaseValidation.RunAsync();
        return 0;
    default:
        PrintUsage();
        return 2;
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        Gatekeeper validation samples

          calibrate   Live Azure calibration of both Phase-4 A2A boundary judges
          remote      Calibrate, then execute one consent-gated real A2A endpoint call
          mock-tools       Offline Phase-7 SQL/browser/cloud/package-manager simulation
          memory-security  Offline memory tool/MCP/context-provider release validation
        """);
}
