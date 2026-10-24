// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using AgentEval.Evals;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefNativeReportJsonTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("native-report-json");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() => AefTestRuns.Delete(_out);
    private string Write(ReadOnlyMemory<byte>? report, AefContentCapture capture) => AefCompositeRunWriter.Write(_out,
        new EvalResult(new("case", "Case", "test", "1.0"), new(1, null, "pass", true, .5, "none", null),
            new(null, null, null, null, null), new("code", null, null, null, null, 0, false), Start),
        AefCompositeRunWriter.Undescribed, new()
        {
            SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "test:report",
            ContentCapture = capture, NativeReportJson = report,
        }, Start, Start.AddSeconds(1)).Directory;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_OriginalJsonSnapshot_PreservesExactBytesUnderCapturePolicy(bool capture)
    {
        var bytes = Encoding.UTF8.GetBytes("{\r\n  \"FutureProperty\": \"original report text\", \"value\": 0\r\n}\n");
        var directory = Write(bytes, capture ? AefContentCapture.On : AefContentCapture.Off);
        var evidence = new AefRunEvidenceReader().Read(directory);
        Assert.Single(evidence.Results);
        Assert.False(evidence.Header.TryGetProperty("ext", out var ext) && ext.TryGetProperty("agenteval.report", out _));
        AefTestRuns.Delete(_out);
        if (capture)
        {
            Assert.Equal(bytes, evidence.CapturedContent("E-report-native-json")!.Value.ToArray());
            var document = Assert.Single(evidence.Evidence);
            Assert.Equal("document", document.GetProperty("kind").GetString());
            Assert.False(document.TryGetProperty("ext", out _));
        }
        else Assert.Null(evidence.CapturedContent("E-report-native-json"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("utf8")]
    [InlineData("array")]
    public void Write_InvalidCapturedJson_RefusesBeforeCreatingRun(string invalid)
    {
        var bytes = invalid switch
        {
            "empty" => Array.Empty<byte>(),
            "malformed" => Encoding.UTF8.GetBytes("{"),
            "duplicate" => Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}"),
            "utf8" => new byte[] { 0xff },
            _ => Encoding.UTF8.GetBytes("[]"),
        };
        Assert.Throws<ArgumentException>(() => Write(bytes, AefContentCapture.On));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public void Write_OversizedCapturedJson_RefusesBeforeParsingOrCreatingRun()
    {
        var error = Assert.Throws<ArgumentException>(() => Write(new byte[(64 << 20) + 1], AefContentCapture.On));
        Assert.Contains("64 MiB", error.Message);
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public void Write_CaptureOff_DoesNotInspectOrRetainProvidedPayload()
    {
        var directory = Write(new byte[] { 0xff }, AefContentCapture.Off);
        Assert.Null(new AefRunEvidenceReader().Read(directory).CapturedContent("E-report-native-json"));
    }

    [Fact]
    public void Write_AbsentSnapshot_DoesNotInventADocument()
    {
        var directory = Write(null, AefContentCapture.On);
        Assert.Null(new AefRunEvidenceReader().Read(directory).CapturedContent("E-report-native-json"));
    }
}
