// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO.Compression;

namespace AgentEval.Results.Observations;

// RFC 1952 framing around BCL raw DEFLATE. GZipStream can accept truncated trailers
// unless the hosting process enabled a global compatibility switch before first use.
internal static class OtlpGzipDecoder
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal static byte[] Decode(ReadOnlyMemory<byte> wire, int maximum, CancellationToken token)
    {
        using var input = new MemberInput(wire, token);
        using var output = new MemoryStream();
        var buffer = new byte[64 << 10];
        if (wire.IsEmpty) throw Invalid();
        while (input.Offset < wire.Length)
        {
            token.ThrowIfCancellationRequested();
            var start = input.Offset;
            var header = input.Take(10).Span;
            if (header[0] != 0x1f || header[1] != 0x8b || header[2] != 8 || (header[3] & 0xe0) != 0)
                throw Invalid();
            var flags = header[3];
            if ((flags & 4) != 0)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(input.Take(2).Span);
                input.Take(length);
            }
            if ((flags & 8) != 0) input.SkipTerminated();
            if ((flags & 16) != 0) input.SkipTerminated();
            if ((flags & 2) != 0)
            {
                var checksum = (ushort)~AppendCrc(uint.MaxValue, wire.Span[start..input.Offset]);
                if (BinaryPrimitives.ReadUInt16LittleEndian(input.Take(2).Span) != checksum) throw Invalid();
            }

            uint crc = uint.MaxValue;
            var memberStart = output.Length;
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var remaining = maximum - (int)output.Length;
                    var count = deflate.Read(buffer, 0, Math.Min(buffer.Length, remaining + 1));
                    if (count == 0) break;
                    if (count > remaining)
                        throw new OtlpIngestionException("limit", "decompressed byte budget exceeded");
                    crc = AppendCrc(crc, buffer.AsSpan(0, count));
                    output.Write(buffer, 0, count);
                }
            }
            var trailer = input.Take(8).Span;
            if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != ~crc ||
                BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != (uint)(output.Length - memberStart))
                throw Invalid();
        }
        token.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static uint AppendCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xedb88320u);
            table[i] = value;
        }
        return table;
    }

    private static InvalidDataException Invalid() => new("Invalid or incomplete gzip member.");

    // Raw DeflateStream does not expose unused read-ahead bytes. Feed one byte from
    // bounded in-memory transport at a time so its end marker leaves the trailer
    // untouched. Exhaustion throws: raw inflation must not silently accept EOF.
    private sealed class MemberInput(ReadOnlyMemory<byte> wire, CancellationToken token) : Stream
    {
        internal int Offset { get; private set; }
        internal ReadOnlyMemory<byte> Take(int count)
        {
            token.ThrowIfCancellationRequested();
            if (count > wire.Length - Offset) throw Invalid();
            var result = wire.Slice(Offset, count);
            Offset += count;
            return result;
        }
        internal void SkipTerminated()
        {
            while (Take(1).Span[0] != 0) { }
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            buffer[0] = Take(1).Span[0];
            return 1;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
