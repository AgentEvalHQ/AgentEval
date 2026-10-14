// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.MissionControl.Core;

internal static class QueryJsonBudget
{
    internal static bool Fits<T>(T value, int maximumBytes)
    {
        using var output = new CountingStream(maximumBytes);
        try { JsonSerializer.Serialize(output, value); return true; }
        catch (BudgetExceededException) { return false; }
    }

    private sealed class BudgetExceededException : Exception;
    private sealed class CountingStream(int maximumBytes) : Stream
    {
        private long _bytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes;
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Count(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Count(buffer.Length);
        private void Count(int bytes)
        {
            if (bytes > maximumBytes - _bytes) throw new BudgetExceededException();
            _bytes += bytes;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
