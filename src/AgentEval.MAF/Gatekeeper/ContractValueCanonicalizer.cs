// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Produces a bounded SHA-256 hash over a deterministic, type-tagged JSON value representation. Object property
/// order is ignored, array order is retained, and equivalent JSON number spellings normalize to one value.
/// </summary>
internal static class ContractValueCanonicalizer
{
    internal const int MaxSerializedBytes = ArgumentCanonicalizer.DefaultMaxLength;
    private const int MaxDepth = 32;
    private const int MaxExponentDigits = 9;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions { MaxDepth = MaxDepth };
        options.Converters.Add(new StrictStringJsonConverter());
        return options;
    }

    internal static bool TryHash(object value, out byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            using var stream = new BoundedWriteStream(MaxSerializedBytes);
            JsonSerializer.Serialize(stream, value, value.GetType(), SerializerOptions);
            using var document = JsonDocument.Parse(
                stream.WrittenMemory,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxDepth,
                });
            using var accumulator = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (!AppendElement(accumulator, document.RootElement, depth: 0))
            {
                hash = [];
                return false;
            }

            hash = accumulator.GetHashAndReset();
            return true;
        }
        catch (Exception exception) when
            (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            hash = [];
            return false;
        }
    }

    private static bool AppendElement(IncrementalHash accumulator, JsonElement element, int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return AppendObject(accumulator, element, depth);
            case JsonValueKind.Array:
                return AppendArray(accumulator, element, depth);
            case JsonValueKind.String:
                return AppendString(accumulator, "s", element.GetString()!);
            case JsonValueKind.Number:
                if (!TryNormalizeNumber(element.GetRawText(), out var number))
                {
                    return false;
                }

                return AppendString(accumulator, "n", number);
            case JsonValueKind.True:
                AppendAscii(accumulator, "b1;");
                return true;
            case JsonValueKind.False:
                AppendAscii(accumulator, "b0;");
                return true;
            case JsonValueKind.Null:
                AppendAscii(accumulator, "z;");
                return true;
            default:
                return false;
        }
    }

    private static bool AppendObject(IncrementalHash accumulator, JsonElement element, int depth)
    {
        var properties = element.EnumerateObject().ToArray();
        Array.Sort(properties, static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        for (var index = 1; index < properties.Length; index++)
        {
            if (string.Equals(properties[index - 1].Name, properties[index].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        AppendCount(accumulator, "o", properties.Length);
        foreach (var property in properties)
        {
            if (!AppendString(accumulator, "p", property.Name) ||
                !AppendElement(accumulator, property.Value, depth + 1))
            {
                return false;
            }
        }

        AppendAscii(accumulator, ";");
        return true;
    }

    private static bool AppendArray(IncrementalHash accumulator, JsonElement element, int depth)
    {
        var length = element.GetArrayLength();
        AppendCount(accumulator, "a", length);
        foreach (var item in element.EnumerateArray())
        {
            if (!AppendElement(accumulator, item, depth + 1))
            {
                return false;
            }
        }

        AppendAscii(accumulator, ";");
        return true;
    }

    private static bool AppendString(IncrementalHash accumulator, string tag, string value)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        AppendAscii(accumulator, tag);
        AppendAscii(accumulator, bytes.Length.ToString(CultureInfo.InvariantCulture));
        AppendAscii(accumulator, ":");
        accumulator.AppendData(bytes);
        AppendAscii(accumulator, ";");
        return true;
    }

    private static void AppendCount(IncrementalHash accumulator, string tag, int count)
    {
        AppendAscii(accumulator, tag);
        AppendAscii(accumulator, count.ToString(CultureInfo.InvariantCulture));
        AppendAscii(accumulator, ":");
    }

    private static void AppendAscii(IncrementalHash accumulator, string value)
        => accumulator.AppendData(Encoding.ASCII.GetBytes(value));

    private static bool TryNormalizeNumber(string raw, out string normalized)
    {
        var span = raw.AsSpan();
        var negative = span.Length > 0 && span[0] == '-';
        if (negative)
        {
            span = span[1..];
        }

        var exponentIndex = span.IndexOfAny('e', 'E');
        var mantissa = exponentIndex >= 0 ? span[..exponentIndex] : span;
        var exponentText = exponentIndex >= 0 ? span[(exponentIndex + 1)..] : ReadOnlySpan<char>.Empty;
        var dotIndex = mantissa.IndexOf('.');
        var integer = dotIndex >= 0 ? mantissa[..dotIndex] : mantissa;
        var fraction = dotIndex >= 0 ? mantissa[(dotIndex + 1)..] : ReadOnlySpan<char>.Empty;

        if (integer.Length == 0 || !AllAsciiDigits(integer) || !AllAsciiDigits(fraction))
        {
            normalized = string.Empty;
            return false;
        }

        var exponent = 0;
        if (!exponentText.IsEmpty && !TryParseExponent(exponentText, out exponent))
        {
            normalized = string.Empty;
            return false;
        }

        var digits = string.Concat(integer, fraction).TrimStart('0');
        if (digits.Length == 0)
        {
            normalized = "0";
            return true;
        }

        exponent -= fraction.Length;
        var trailingZeros = 0;
        while (trailingZeros < digits.Length && digits[digits.Length - 1 - trailingZeros] == '0')
        {
            trailingZeros++;
        }

        if (trailingZeros > 0)
        {
            digits = digits[..^trailingZeros];
            exponent += trailingZeros;
        }

        normalized = string.Concat(
            negative ? "-" : string.Empty,
            digits,
            "e",
            exponent.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private static bool TryParseExponent(ReadOnlySpan<char> value, out int exponent)
    {
        var negative = value.Length > 0 && value[0] == '-';
        if (negative || (value.Length > 0 && value[0] == '+'))
        {
            value = value[1..];
        }

        if (value.Length is 0 or > MaxExponentDigits || !AllAsciiDigits(value) ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out exponent))
        {
            exponent = 0;
            return false;
        }

        if (negative)
        {
            exponent = -exponent;
        }

        return true;
    }

    private static bool AllAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private sealed class StrictStringJsonConverter : JsonConverter<string>
    {
        public override string? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is not null)
            {
                StrictUtf8.GetByteCount(value);
            }

            return value;
        }

        public override void Write(
            Utf8JsonWriter writer,
            string value,
            JsonSerializerOptions options)
        {
            StrictUtf8.GetByteCount(value);
            writer.WriteStringValue(value);
        }

        public override void WriteAsPropertyName(
            Utf8JsonWriter writer,
            string value,
            JsonSerializerOptions options)
        {
            StrictUtf8.GetByteCount(value);
            writer.WritePropertyName(value);
        }
    }

    private sealed class BoundedWriteStream(int maxBytes) : Stream
    {
        private readonly MemoryStream _inner = new(Math.Min(maxBytes, 4096));

        internal ReadOnlyMemory<byte> WrittenMemory =>
            new(_inner.GetBuffer(), 0, checked((int)_inner.Length));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_inner.Length + buffer.Length > maxBytes)
            {
                throw new BoundedValueException();
            }

            _inner.Write(buffer);
        }
    }

    private sealed class BoundedValueException : Exception;
}
