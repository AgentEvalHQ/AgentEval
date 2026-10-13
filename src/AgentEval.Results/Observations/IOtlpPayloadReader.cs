// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Observations;

/// <summary>Supported OTLP transport compression, separate from JSON/protobuf serialization.</summary>
public enum OtlpCompression
{
    /// <summary>No compression.</summary>
    None,
    /// <summary>Gzip compression.</summary>
    Gzip
}

/// <summary>Reads bounded OTLP payload bytes without decoding messages, persisting content or acknowledging ingestion.</summary>
public interface IOtlpPayloadReader
{
    /// <summary>
    /// Reads through end-of-input under independent transport and decompressed byte limits.
    /// The caller owns the input stream and returned array. Cancellation is passed to every read.
    /// </summary>
    Task<byte[]> ReadAsync(Stream input, OtlpCompression compression, CancellationToken cancellationToken = default);
}
