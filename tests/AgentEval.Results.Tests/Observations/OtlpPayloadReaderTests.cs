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
