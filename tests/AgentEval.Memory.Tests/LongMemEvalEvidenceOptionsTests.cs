// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;
using Xunit;

namespace AgentEval.Memory.Tests;

public sealed class LongMemEvalEvidenceOptionsTests
{
    [Fact]
    public void Defaults_EvidenceCapture_IsOffAndDoesNotPersistContent()
    {
        var options = new ExternalBenchmarkOptions();

        Assert.Equal(EvidenceCaptureMode.None, options.EvidenceCaptureMode);
        Assert.False(options.PersistsEvidenceContent);
        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_EvidenceTopKOutsideBounds_Throws(int topK)
    {
        var options = new ExternalBenchmarkOptions { EvidenceTopK = topK };

        var error = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Equal(nameof(ExternalBenchmarkOptions.EvidenceTopK), error.ParamName);
    }

    [Fact]
    public void Validate_UnknownEvidenceCaptureMode_Throws()
    {
        var options = new ExternalBenchmarkOptions
        {
            EvidenceCaptureMode = (EvidenceCaptureMode)int.MaxValue
        };

        var error = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Equal(nameof(ExternalBenchmarkOptions.EvidenceCaptureMode), error.ParamName);
    }

    [Fact]
    public void FullMode_ExplicitlySignalsContentPersistence()
    {
        var options = new ExternalBenchmarkOptions
        {
            EvidenceCaptureMode = EvidenceCaptureMode.Full
        };

        options.Validate();

        Assert.True(options.PersistsEvidenceContent);
    }
}
