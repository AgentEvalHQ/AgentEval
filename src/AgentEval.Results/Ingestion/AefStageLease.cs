// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Runs;

namespace AgentEval.Results.Ingestion;

// Shared by upload, finalization and recovery. FileShare.None is intentional: on Unix, delete sharing
// does not provide the same exclusive lease. Archival must coordinate lease release with workspace ownership.
internal static class AefStageLease
{
    internal static FileStream Acquire(AefIngestionStage stage)
    {
        var path = Path.Combine(stage.Directory, "finalize.lock");
        if (Path.Exists(path) && AefFolder.KindOf(path) != AefEntryKind.File)
            throw new AefFinalizationException("path", "non-regular stage lease");
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
