// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.IO.Compression;
using AgentEval.Results.Observations;

namespace AgentEval.Results.Tests.Observations;

public sealed class OtlpPayloadReaderTests
{
    private static byte[] Gzip(byte[] value)
    {
        using var output = new MemoryStream();
        using (var compressor = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(value);
        return output.ToArray();
    }

    [Theory]
    [InlineData(OtlpCompression.None)]
    [InlineData(OtlpCompression.Gzip)]
    public async Task ReadAsync_FragmentedExactLimits_PreservesBytesAndInputOwnership(OtlpCompression compression)
    {
        var payload = "{\"resourceSpans\":[]}"u8.ToArray();
        var wire = compression == OtlpCompression.None ? payload : Gzip(payload);
        using var input = new FragmentedInput(wire, 1);
        var reader = new OtlpPayloadReader(new() { MaxRequestBytes = wire.Length, MaxDecodedBytes = payload.Length });
        Assert.Equal(payload, await reader.ReadAsync(input, compression));
        Assert.Equal(wire.Length, input.BytesRead);
        Assert.False(input.WasDisposed);
    }

    [Fact]
    public async Task ReadAsync_GzipExpansion_RefusesAtDecodedBudget()
    {
        using var input = new FragmentedInput(Gzip(new byte[200_000]), 7);
        var reader = new OtlpPayloadReader(new() { MaxRequestBytes = 4096, MaxDecodedBytes = 64 });
        var error = await Assert.ThrowsAsync<OtlpIngestionException>(() => reader.ReadAsync(input, OtlpCompression.Gzip));
        Assert.Equal("limit", error.Code);
        Assert.False(input.WasDisposed);
    }

    [Theory]
    [InlineData(OtlpCompression.None)]
    [InlineData(OtlpCompression.Gzip)]
    public async Task ReadAsync_UnknownLengthTransportTooLarge_ReadsAtMostBudgetPlusOne(OtlpCompression compression)
    {
        var payload = "{\"resourceSpans\":[]}"u8.ToArray();
        var wire = compression == OtlpCompression.None ? payload : Gzip(payload);
        using var input = new FragmentedInput(wire, 100);
        var reader = new OtlpPayloadReader(new() { MaxRequestBytes = 10 });
        var error = await Assert.ThrowsAsync<OtlpIngestionException>(() => reader.ReadAsync(input, compression));
        Assert.Equal("limit", error.Code);
        Assert.Equal(11, input.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_GzipWithOversizedTrailingBytes_CountsEntireTransport()
    {
        var gzip = Gzip("{}"u8.ToArray());
        using var input = new FragmentedInput([.. gzip, .. new byte[1000]], 1);
        var reader = new OtlpPayloadReader(new() { MaxRequestBytes = gzip.Length + 10 });
        var error = await Assert.ThrowsAsync<OtlpIngestionException>(() => reader.ReadAsync(input, OtlpCompression.Gzip));
        Assert.Equal("limit", error.Code);
        Assert.True(input.BytesRead <= gzip.Length + 11);
    }

    [Fact]
    public async Task ReadAsync_ConcatenatedGzipMembers_AppliesOneDecodedBudget()
    {
        using var input = new FragmentedInput([.. Gzip("abc"u8.ToArray()), .. Gzip("def"u8.ToArray())], 1);
        Assert.Equal("abcdef"u8.ToArray(), await new OtlpPayloadReader().ReadAsync(input, OtlpCompression.Gzip));
        using var over = new FragmentedInput([.. Gzip("abc"u8.ToArray()), .. Gzip("def"u8.ToArray())], 1);
        var error = await Assert.ThrowsAsync<OtlpIngestionException>(() => new OtlpPayloadReader(new() { MaxDecodedBytes = 5 })
            .ReadAsync(over, OtlpCompression.Gzip));
        Assert.Equal("limit", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    public async Task ReadAsync_InvalidGzip_RefusesWithoutExposingInput(int length)
    {
        using var input = new FragmentedInput(Enumerable.Repeat((byte)'x', length).ToArray(), 1);
        var error = await Assert.ThrowsAsync<OtlpIngestionException>(() => new OtlpPayloadReader().ReadAsync(input, OtlpCompression.Gzip));
        Assert.Equal("malformed", error.Code);
        Assert.DoesNotContain(new string('x', 10), error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task ReadAsync_TruncatedGzipTrailer_RefusesCompleteLookingPayload(int missingBytes)
    {
        var compressed = Gzip("{\"resourceSpans\":[]}"u8.ToArray());
        using var input = new FragmentedInput(compressed[..^missingBytes], 1);
        var error = await Assert.ThrowsAsync<OtlpIngestionException>(() =>
            new OtlpPayloadReader().ReadAsync(input, OtlpCompression.Gzip));
        Assert.Equal("malformed", error.Code);
        Assert.False(input.WasDisposed);
    }

    // Independently generated with Python gzip/zlib, including all optional RFC 1952 fields.
    private static byte[] OptionalHeaderMember() => Convert.FromHexString(
        "1f8b081f0000000000ff0300657874666978747572652e6a736f6e006f7074696f6e616c2068656164657200b0a4ab562a4a2dce2f2d4a4e0d2e48cc2b56b28a8ead050095e3b04c14000000"); // DevSkim: ignore DS173237 - public gzip fixture bytes with optional headers

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_EveryIncompleteMemberPrefix_RefusesEvenAfterValidMember(bool concatenated)
    {
        var member = OptionalHeaderMember();
        var prefix = concatenated ? Gzip("first"u8.ToArray()) : Array.Empty<byte>();
        // A boundary between complete members is valid; every nonempty incomplete suffix is not.
        for (var length = 1; length < member.Length; length++)
        {
            using var input = new FragmentedInput([.. prefix, .. member[..length]], 7);
            var error = await Assert.ThrowsAsync<OtlpIngestionException>(() =>
                new OtlpPayloadReader().ReadAsync(input, OtlpCompression.Gzip));
            Assert.Equal("malformed", error.Code);
        }
    }

    [Fact]
    public async Task ReadAsync_OptionalHeadersAndEmptyMembers_PreservesCombinedPayload()
    {
        using var input = new FragmentedInput([.. Gzip([]), .. OptionalHeaderMember(), .. Gzip([])], 11);
        Assert.Equal("{\"resourceSpans\":[]}"u8.ToArray(),
            await new OtlpPayloadReader().ReadAsync(input, OtlpCompression.Gzip));
    }

    [Theory]
    [InlineData(2)] // Compression method.
    [InlineData(3)] // Reserved flags.
    [InlineData(44)] // Header checksum.
    [InlineData(-8)] // Payload checksum.
    [InlineData(-4)] // Decoded size.
    public async Task ReadAsync_CorruptMemberMetadata_Refuses(int offset)
    {
        var member = OptionalHeaderMember();
        member[offset < 0 ? member.Length + offset : offset] ^= 0x20;
        using var input = new FragmentedInput(member, 13);
        var error = await Assert.ThrowsAsync<OtlpIngestionException>(() =>
            new OtlpPayloadReader().ReadAsync(input, OtlpCompression.Gzip));
        Assert.Equal("malformed", error.Code);
    }

    [Fact]
    public async Task ReadAsync_GarbageAfterCompleteMember_RefusesInsteadOfIgnoringSuffix()
    {
        using var input = new FragmentedInput([.. Gzip("{}"u8.ToArray()), 0, 1, 2], 1);
        var error = await Assert.ThrowsAsync<OtlpIngestionException>(() =>
            new OtlpPayloadReader().ReadAsync(input, OtlpCompression.Gzip));
        Assert.Equal("malformed", error.Code);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression, false)]
    [InlineData(CompressionLevel.Fastest, false)]
    [InlineData(CompressionLevel.SmallestSize, false)]
    [InlineData(CompressionLevel.NoCompression, true)]
    [InlineData(CompressionLevel.Fastest, true)]
    [InlineData(CompressionLevel.SmallestSize, true)]
    public async Task ReadAsync_LargeMemberAcrossCompressionLevels_PreservesExactBytes(CompressionLevel level, bool compressible)
    {
        var payload = new byte[128 * 1024];
        if (compressible)
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 19);
        else new Random(42).NextBytes(payload); // DevSkim: ignore DS148264 — reproducible compression test bytes, not a secret
        using var wire = new MemoryStream();
        using (var compressor = new GZipStream(wire, level, leaveOpen: true)) compressor.Write(payload);
        using var input = new FragmentedInput(wire.ToArray(), 8192);
        Assert.Equal(payload, await new OtlpPayloadReader().ReadAsync(input, OtlpCompression.Gzip));
    }

    [Fact]
    public async Task ReadAsync_CancelledBeforeReading_DoesNotConsumeInput()
    {
        using var input = new FragmentedInput("{}"u8.ToArray(), 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OtlpPayloadReader().ReadAsync(input, OtlpCompression.None, new(true)));
        Assert.Equal(0, input.BytesRead);
        Assert.False(input.WasDisposed);
    }

    [Theory]
    [InlineData(OtlpCompression.None)]
    [InlineData(OtlpCompression.Gzip)]
    public async Task ReadAsync_CancellationDuringInput_DoesNotReturnPartialPayload(OtlpCompression compression)
    {
        using var cancellation = new CancellationTokenSource();
        var value = new byte[1024];
        using var input = new FragmentedInput(compression == OtlpCompression.None ? value : Gzip(value), 1)
        { AfterRead = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OtlpPayloadReader()
            .ReadAsync(input, compression, cancellation.Token));
        Assert.Equal(1, input.BytesRead);
        Assert.False(input.WasDisposed);
    }

    [Fact]
    public async Task ReadAsync_UnsupportedCompression_DoesNotConsumeInput()
    {
        using var input = new FragmentedInput("{}"u8.ToArray(), 1);
        var error = await Assert.ThrowsAsync<OtlpIngestionException>(() => new OtlpPayloadReader().ReadAsync(input, (OtlpCompression)42));
        Assert.Equal("encoding", error.Code);
        Assert.Equal(0, input.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_EmptyUncompressed_ReturnsEmptyPayloadForProtobufDecoder()
    {
        using var input = new FragmentedInput([], 1);
        Assert.Empty(await new OtlpPayloadReader().ReadAsync(input, OtlpCompression.None));
    }

    private sealed class FragmentedInput(byte[] data, int fragment) : Stream
    {
        internal Action? AfterRead { get; init; }
        internal int BytesRead { get; private set; }
        internal bool WasDisposed { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, fragment), data.Length - BytesRead);
            data.AsSpan(BytesRead, read).CopyTo(buffer.AsSpan(offset, read));
            BytesRead += read;
            AfterRead?.Invoke();
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(Math.Min(buffer.Length, fragment), data.Length - BytesRead);
            data.AsMemory(BytesRead, read).CopyTo(buffer);
            BytesRead += read;
            AfterRead?.Invoke();
            return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
