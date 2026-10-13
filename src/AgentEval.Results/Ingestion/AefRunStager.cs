// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Runs;

namespace AgentEval.Results.Ingestion;

/// <summary>
/// Streams files into stage/content with control state in stage/state.json. Failed or interrupted stages
/// remain isolated and count toward workspace capacity. Only an entirely consumed upload becomes staged.
/// </summary>
public sealed class AefRunStager : IAefRunStager
{
    private readonly IAefIngestionWorkspace _workspace;
    private readonly AefStagingOptions _options;
    private int _active;

    /// <summary>Creates the stager with explicit workspace ownership and validated immutable bounds.</summary>
    public AefRunStager(IAefIngestionWorkspace workspace, AefStagingOptions? options = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = options ?? new();
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<AefStagedRun> StageAsync(IAsyncEnumerable<AefUploadFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _active) > _options.MaxConcurrentUploads)
        {
            Interlocked.Decrement(ref _active);
            throw new AefStagingException("busy", "concurrent upload capacity reached");
        }
        AefIngestionStage? stage = null;
        FileStream? stageLease = null;
        long bytes = 0;
        var count = 0;
        try
        {
            stage = _workspace.BeginStage(cancellationToken);
            stageLease = AefStageLease.Acquire(stage);
            var content = Path.Combine(stage.Directory, "content");
            Directory.CreateDirectory(content);
            AefStageState.Write(stage, "receiving", count, bytes);
            var paths = new Dictionary<string, (string Spelling, bool File)>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[64 * 1024];
            await foreach (var file in files.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count == _options.MaxFiles) throw new AefStagingException("limit", "file count exceeded");
                if (file is null || string.IsNullOrEmpty(file.Path) || !AefPaths.IsValid(file.Path)
                    || file.Content is null || !file.Content.CanRead)
                    throw new AefStagingException("path", "invalid path or unreadable input stream");
                ReservePath(file.Path, paths);
                var destination = Path.Combine(content, file.Path.Replace('/', Path.DirectorySeparatorChar));
                // No caller-controlled links can enter a newly reserved stage; local ownership is required.
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                long fileBytes = 0;
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (true)
                    {
                        var remaining = Math.Min(_options.MaxFileBytes - fileBytes, _options.MaxBytes - bytes);
                        var read = await file.Content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining + 1)), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        if (read > remaining) throw new AefStagingException("limit", "file or aggregate byte limit exceeded");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        fileBytes += read;
                        bytes += read;
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                count++;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) throw new AefStagingException("empty", "no files supplied");
            AefStageState.Write(stage, "staged", count, bytes);
            return new(stage.Id, content, count, bytes);
        }
        catch (Exception error)
        {
            if (stage is not null && stageLease is not null)
            {
                // If state persistence fails, retain receiving/absent state. Recovery must treat it as incomplete.
                try { AefStageState.Write(stage, error is OperationCanceledException ? "interrupted" : "failed", count, bytes); }
                catch (Exception stateError) when (stateError is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            stageLease?.Dispose();
            Interlocked.Decrement(ref _active);
        }
    }

    private static void ReservePath(string path, Dictionary<string, (string Spelling, bool File)> paths)
    {
        var segments = path.Split('/');
        var prefix = "";
        for (var i = 0; i < segments.Length; i++)
        {
            prefix = i == 0 ? segments[i] : prefix + "/" + segments[i];
            var file = i == segments.Length - 1;
            if (paths.TryGetValue(prefix, out var previous))
            {
                if (previous.Spelling != prefix || previous.File || file)
                    throw new AefStagingException("conflict", "duplicate, case-clashing, or file/directory-conflicting paths");
            }
            else paths.Add(prefix, (prefix, file));
        }
    }

}
