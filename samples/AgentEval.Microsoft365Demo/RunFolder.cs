// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The run folder of one demo run and every path in it. Created whatever the screen verbosity: <c>console.log</c>,
/// <c>trace.jsonl</c>, <c>issues.jsonl</c>, <c>readiness.md</c>, <c>readiness.json</c>, <c>turns.jsonl</c>,
/// <c>evidence/&lt;caseId&gt;.json</c>, <c>results.md</c>, <c>results.json</c>, <c>summary.md</c>, <c>raw/</c> with
/// <c>--capture-raw</c>, and <c>&lt;folder&gt;.zip</c> beside it with <c>--zip</c>.
/// </summary>
internal sealed class RunFolder
{
    /// <summary>Everything printed, plus every trace event in full.</summary>
    public const string ConsoleLogName = "console.log";

    /// <summary>The provider's JSONL trace (readiness and the agent's run).</summary>
    public const string TraceName = "trace.jsonl";

    /// <summary>The provider's JSONL issue register (readiness and the agent's run).</summary>
    public const string IssuesName = "issues.jsonl";

    /// <summary>The readiness report (Markdown).</summary>
    public const string ReadinessMarkdownName = "readiness.md";

    /// <summary>The readiness report (JSON, schema <c>agenteval.m365.readiness/v1</c>).</summary>
    public const string ReadinessJsonName = "readiness.json";

    /// <summary>One line per agent turn (metadata only, no answer text).</summary>
    public const string TurnsName = "turns.jsonl";

    /// <summary>The folder of the per-turn evidence documents.</summary>
    public const string EvidenceFolderName = "evidence";

    /// <summary>The per-case results (Markdown).</summary>
    public const string ResultsMarkdownName = "results.md";

    /// <summary>The per-case results (JSON).</summary>
    public const string ResultsJsonName = "results.json";

    /// <summary>The summary to read first and send back.</summary>
    public const string SummaryName = "summary.md";

    /// <summary>The raw A2A captures (<c>--capture-raw</c>).</summary>
    public const string RawFolderName = "raw";

    /// <summary>The JSON options of every JSON file the demo writes (camel case, indented, enums as names, nulls kept).</summary>
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions(indented: true);

    /// <summary>The JSON options of a JSONL line (the same, on one line).</summary>
    public static readonly JsonSerializerOptions JsonLineOptions = CreateJsonOptions(indented: false);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly HashSet<string> _evidenceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _writeErrors = [];

    private RunFolder(DirectoryInfo root, string runId)
    {
        Root = root;
        RunId = runId;
    }

    /// <summary>The folder.</summary>
    public DirectoryInfo Root { get; }

    /// <summary>The demo's run id: <c>m365-demo-&lt;yyyyMMdd-HHmmss&gt;Z</c> (also the agent run's id in the trace and the issues).</summary>
    public string RunId { get; }

    /// <summary>console.log.</summary>
    public FileInfo ConsoleLog => File(ConsoleLogName);

    /// <summary>trace.jsonl.</summary>
    public FileInfo Trace => File(TraceName);

    /// <summary>issues.jsonl.</summary>
    public FileInfo Issues => File(IssuesName);

    /// <summary>readiness.md.</summary>
    public FileInfo ReadinessMarkdown => File(ReadinessMarkdownName);

    /// <summary>readiness.json.</summary>
    public FileInfo ReadinessJson => File(ReadinessJsonName);

    /// <summary>turns.jsonl.</summary>
    public FileInfo Turns => File(TurnsName);

    /// <summary>evidence/.</summary>
    public DirectoryInfo EvidenceFolder => new(Path.Combine(Root.FullName, EvidenceFolderName));

    /// <summary>results.md.</summary>
    public FileInfo ResultsMarkdown => File(ResultsMarkdownName);

    /// <summary>results.json.</summary>
    public FileInfo ResultsJson => File(ResultsJsonName);

    /// <summary>summary.md.</summary>
    public FileInfo Summary => File(SummaryName);

    /// <summary>raw/.</summary>
    public DirectoryInfo RawFolder => new(Path.Combine(Root.FullName, RawFolderName));

    /// <summary>The zip beside the folder: <c>&lt;folder&gt;.zip</c>.</summary>
    public FileInfo ZipFile => new(Root.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip");

    /// <summary>Every write that failed (relative name and reason), in order (a snapshot).</summary>
    public IReadOnlyList<string> WriteErrors
    {
        get
        {
            lock (_gate)
            {
                return _writeErrors.ToArray();
            }
        }
    }

    /// <summary>
    /// Creates the run folder: <paramref name="explicitFolder"/> when given (new or empty, checked by the options), else
    /// <c>&lt;runsRoot&gt;/&lt;yyyyMMdd-HHmmss&gt;Z</c> with <c>-2</c>, <c>-3</c>… when that name is taken.
    /// </summary>
    /// <param name="explicitFolder"><c>--out</c>.</param>
    /// <param name="runsRoot">Where default run folders go; null means <c>./m365-demo-runs</c>.</param>
    /// <param name="now">The run's start.</param>
    /// <exception cref="IOException">The folder could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder could not be created.</exception>
    public static RunFolder Create(DirectoryInfo? explicitFolder, DirectoryInfo? runsRoot, DateTimeOffset now)
    {
        var stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z";
        var runId = "m365-demo-" + stamp;
        DirectoryInfo folder;
        if (explicitFolder is not null)
        {
            folder = explicitFolder;
        }
        else
        {
            var root = runsRoot?.FullName ?? Path.Combine(Directory.GetCurrentDirectory(), DemoCommandLine.DefaultRunsFolderName);
            folder = new DirectoryInfo(Path.Combine(root, stamp));
            for (var n = 2; folder.Exists || System.IO.File.Exists(folder.FullName); n++)
            {
                folder = new DirectoryInfo(Path.Combine(root, stamp + "-" + n.ToString(CultureInfo.InvariantCulture)));
            }
        }

        folder.Create();
        folder.Refresh();
        // turns.jsonl and evidence/ are created by the first turn: a run that sent no prompt has neither.
        return new RunFolder(folder, runId);
    }

    /// <summary>Records a write that failed; the run then exits 3 and the summary lists it.</summary>
    /// <param name="error">What could not be written, and why.</param>
    public void RecordError(string error)
    {
        lock (_gate)
        {
            _writeErrors.Add(error);
        }
    }

    /// <summary>The path of <paramref name="relativeName"/> inside the folder.</summary>
    /// <param name="relativeName">The relative file name.</param>
    public FileInfo File(string relativeName) => new(Path.Combine(Root.FullName, relativeName));

    /// <summary>The path relative to the run folder, with forward slashes (for reports).</summary>
    /// <param name="path">A full path inside the folder.</param>
    public string Relative(string path) => Path.GetRelativePath(Root.FullName, path).Replace('\\', '/');

    /// <summary>Writes a text file (UTF-8, no BOM); a failure is recorded in <see cref="WriteErrors"/> and returns false.</summary>
    /// <param name="file">The file.</param>
    /// <param name="text">The text.</param>
    public bool TryWriteText(FileInfo file, string text)
    {
        try
        {
            file.Directory?.Create();
            System.IO.File.WriteAllText(file.FullName, text, Utf8NoBom);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordError($"{Relative(file.FullName)}: {ex.GetType().Name}: {ex.Message}");

            return false;
        }
    }

    /// <summary>Writes an object as indented JSON; a failure is recorded and returns false.</summary>
    /// <typeparam name="T">The object type.</typeparam>
    /// <param name="file">The file.</param>
    /// <param name="value">The object.</param>
    public bool TryWriteJson<T>(FileInfo file, T value) =>
        TryWriteText(file, JsonSerializer.Serialize(value, JsonOptions) + "\n");

    /// <summary>Appends one JSON line; a failure is recorded and returns false.</summary>
    /// <typeparam name="T">The object type.</typeparam>
    /// <param name="file">The JSONL file.</param>
    /// <param name="value">The object.</param>
    public bool TryAppendJsonLine<T>(FileInfo file, T value)
    {
        try
        {
            var line = JsonSerializer.Serialize(value, JsonLineOptions) + "\n";
            lock (_gate)
            {
                System.IO.File.AppendAllText(file.FullName, line, Utf8NoBom);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            RecordError($"{Relative(file.FullName)}: {ex.GetType().Name}: {ex.Message}");

            return false;
        }
    }

    /// <summary>
    /// The evidence file of a case: <c>evidence/&lt;caseId&gt;.json</c>, or <c>&lt;caseId&gt;-2.json</c>… when the case
    /// already has one (a case id is unique per run, so this only happens when one case sends twice).
    /// </summary>
    /// <param name="caseId">The case id (a dataset id or <c>prompt</c>; anything outside letters, digits, '.', '_' and '-' becomes '_').</param>
    public FileInfo EvidenceFileFor(string caseId)
    {
        var safe = new string((caseId ?? "case").Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        if (safe.Length == 0 || safe.Trim('.').Length == 0)
        {
            safe = "case";
        }

        lock (_gate)
        {
            var name = safe;
            for (var n = 2; !_evidenceNames.Add(name); n++)
            {
                name = safe + "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            return new FileInfo(Path.Combine(EvidenceFolder.FullName, name + ".json"));
        }
    }

    /// <summary>
    /// Moves the readiness check's issue register into <c>issues.jsonl</c>: the check writes it as
    /// <c>m365-issues.jsonl</c> in its diagnostics folder (the run folder), and the live agent then appends to
    /// <c>issues.jsonl</c>, so the run has one register. Returns the error when the move failed (the file is then kept
    /// where the check wrote it).
    /// </summary>
    /// <param name="readinessIssueFile">The path the readiness report names.</param>
    public string? AdoptReadinessIssues(string? readinessIssueFile)
    {
        if (string.IsNullOrWhiteSpace(readinessIssueFile))
        {
            return null;
        }

        var source = new FileInfo(readinessIssueFile);
        if (!source.Exists || string.Equals(source.FullName, Issues.FullName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            if (Issues.Exists)
            {
                System.IO.File.AppendAllText(Issues.FullName, System.IO.File.ReadAllText(source.FullName, Encoding.UTF8), Utf8NoBom);
                source.Delete();
            }
            else
            {
                source.MoveTo(Issues.FullName);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = $"{source.Name} could not be moved to {IssuesName}: {ex.Message} (the readiness issues stay in {source.Name})";
            RecordError(error);

            return error;
        }
    }

    /// <summary>Zips the folder into <see cref="ZipFile"/> (replacing an older zip of the same name).</summary>
    /// <exception cref="IOException">The zip could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The zip could not be written.</exception>
    public FileInfo WriteZip()
    {
        var zip = ZipFile;
        if (zip.Exists)
        {
            zip.Delete();
        }

        System.IO.Compression.ZipFile.CreateFromDirectory(Root.FullName, zip.FullName, CompressionLevel.Optimal, includeBaseDirectory: true);
        zip.Refresh();
        return zip;
    }

    private static JsonSerializerOptions CreateJsonOptions(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
