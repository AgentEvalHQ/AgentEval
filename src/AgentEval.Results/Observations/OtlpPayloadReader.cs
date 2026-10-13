// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.IO.Compression;

namespace AgentEval.Results.Observations;

/// <summary>
/// Bounds bytes before and after gzip decoding. Does not own concurrency, timeout or persistence;
/// the receiver must hold its request slot through decoding and durable acceptance.
/// </summary>
public sealed class OtlpPayloadReader : IOtlpPayloadReader
{
    private readonly OtlpIngestionLimits _limits;

    /// <summary>Creates a reader with validated immutable limits.</summary>
    public OtlpPayloadReader(OtlpIngestionLimits? limits = null)
    {
        _limits = limits ?? new();
        _limits.Validate();
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadAsync(Stream input, OtlpCompression compression, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new ArgumentException("Input must be readable.", nameof(input));
        if (compression is not (OtlpCompression.None or OtlpCompression.Gzip))
            throw new OtlpIngestionException("encoding", "unsupported compression");
        cancellationToken.ThrowIfCancellationRequested();
        using var bounded = new TransportStream(input, _limits.MaxRequestBytes);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            if (compression == OtlpCompression.Gzip)
            {
                using var gzip = new GZipStream(bounded, CompressionMode.Decompress, leaveOpen: true);
                await CopyDecoded(gzip).ConfigureAwait(false);
                // GZipStream may stop before the transport does. Trailing bytes still consume the request budget.
                while (await bounded.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
                if (bounded.BytesRead < 18) throw new OtlpIngestionException("malformed", "incomplete gzip envelope");
            }
            else await CopyDecoded(bounded).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw new OtlpIngestionException("malformed", "invalid gzip data");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();

        async Task CopyDecoded(Stream decoded)
        {
            while (true)
            {
                var remaining = _limits.MaxDecodedBytes - (int)output.Length;
                var count = await decoded.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (count > remaining) throw new OtlpIngestionException("limit", "decompressed byte budget exceeded");
                output.Write(buffer, 0, count);
            }
        }
    }

    // Never asks an unknown-length input for more than the budget plus one lookahead byte.
    private sealed class TransportStream(Stream input, int limit) : Stream
    {
        private int _read;
        internal int BytesRead => _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = input.Read(buffer, offset, Math.Min(count, limit - _read + 1));
            Count(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await input.ReadAsync(buffer[..Math.Min(buffer.Length, limit - _read + 1)], cancellationToken).ConfigureAwait(false);
            Count(read);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private void Count(int count)
        {
            _read += count;
            if (_read > limit) throw new OtlpIngestionException("limit", "transport byte budget exceeded");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
