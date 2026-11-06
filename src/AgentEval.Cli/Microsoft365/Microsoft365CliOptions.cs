// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Microsoft365;

namespace AgentEval.Cli.Microsoft365;

/// <summary>
/// The <c>--m365-*</c> options shared by <c>--sut microsoft365</c> (<see cref="Microsoft365SutTarget"/>) and
/// <c>m365 doctor</c> (<see cref="M365DoctorCommand"/>), and the one parser of <c>--m365-profile</c>. Each call returns a
/// new option: an option instance belongs to one command.
/// </summary>
internal static class Microsoft365CliOptions
{
    /// <summary>The <c>--m365-profile</c> value for <see cref="DiagnosticProfile.PublicAnswerOnly"/> (the default).</summary>
    public const string DefaultProfile = "public";

    /// <summary>The <c>--m365-profile</c> value for <see cref="DiagnosticProfile.Native118Diagnostics"/> (experimental).</summary>
    public const string Native118Profile = "native118";

    /// <summary>The fields of the <c>--m365-config</c> file, as the usage errors name them.</summary>
    public const string ConfigFields = "tenantId, appClientId; optional endpoint, agentId, scope, agentName, diagnosticProfile, timeZone, trustedHosts";

    /// <summary><c>--m365-config</c>: the Work IQ connection JSON.</summary>
    /// <param name="requirement">The sentence that says when the option is required.</param>
    /// <param name="required">Whether the parser itself requires it.</param>
    public static Option<FileInfo?> Config(string requirement, bool required = false) => new("--m365-config")
    {
        Description = "JSON file with the Work IQ connection: tenantId, appClientId; optional endpoint, agentId (opaque; null = the default Work IQ agent), scope, agentName, diagnosticProfile (PublicAnswerOnly|Native118Diagnostics), timeZone (IANA), trustedHosts. No secret is stored; the token is acquired at run time via MSAL device-code auth + an OS-encrypted cache. " + requirement,
        Required = required,
    };

    /// <summary><c>--m365-profile</c>: <c>public</c> or <c>native118</c>, default <see cref="DefaultProfile"/>; read with <see cref="ParseProfile"/>.</summary>
    public static Option<string> Profile() => new("--m365-profile")
    {
        DefaultValueFactory = _ => DefaultProfile,
        Description = "public = answer only (text/plain); native118 = EXPERIMENTAL, also requests the two diagnostic output modes observed in Microsoft's native evaluator 1.18.0 (tool and retrieval info); not a documented contract",
    };

    /// <summary><c>--m365-capture-raw</c>: the directory raw A2A bytes are retained under (opt-in).</summary>
    public static Option<DirectoryInfo?> CaptureRaw() => new("--m365-capture-raw")
    {
        Description = "Retain the raw A2A request/response bytes and stream frames under this directory (opt-in; may contain tenant data; keep it out of source control).",
    };

    /// <summary><c>--m365-verbose</c>: print every provider trace event to stderr.</summary>
    /// <param name="pathsPrinted">When the command prints the trace and issue file paths, and what else applies to it.</param>
    /// <param name="onByDefault">Whether the option is on when not given.</param>
    public static Option<bool> Verbose(string pathsPrinted, bool onByDefault = false)
    {
        var option = new Option<bool>("--m365-verbose")
        {
            Description = "Print every provider trace event to stderr (stage, code, ids, HTTP status, JSON-RPC code, A2A state, elapsed). The JSONL trace and issue files are always written; " + pathsPrinted,
        };
        if (onByDefault)
        {
            option.DefaultValueFactory = _ => true;
        }

        return option;
    }

    /// <summary><c>--m365-trace</c>: the JSONL trace file.</summary>
    /// <param name="defaultLocation">Where the trace goes when the option is not given.</param>
    public static Option<FileInfo?> Trace(string defaultLocation) => new("--m365-trace")
    {
        Description = "Write the JSONL trace to this file instead of the default " + defaultLocation + ".",
    };

    /// <summary>Maps a <c>--m365-profile</c> value (case-insensitive, trimmed) to the profile, or throws naming the valid values.</summary>
    /// <param name="value">The flag value.</param>
    /// <exception cref="InvalidOperationException">The value is not <c>public</c> or <c>native118</c>.</exception>
    public static DiagnosticProfile ParseProfile(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            DefaultProfile => DiagnosticProfile.PublicAnswerOnly,
            Native118Profile => DiagnosticProfile.Native118Diagnostics,
            _ => throw new InvalidOperationException(
                $"--m365-profile must be '{DefaultProfile}' (answer only) or '{Native118Profile}' (EXPERIMENTAL diagnostics, " +
                $"observed in the native evaluator 1.18.0, not a documented contract); got: '{value}'."),
        };
    }
}
