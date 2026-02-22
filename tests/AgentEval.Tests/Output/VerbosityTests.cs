// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class VerbosityLevelTests
{
    [Fact]
    public void VerbosityLevel_HasCorrectValues()
    {
        // Verify enum values for consistency
        Assert.Equal(0, (int)VerbosityLevel.None);
        Assert.Equal(1, (int)VerbosityLevel.Summary);
        Assert.Equal(2, (int)VerbosityLevel.Detailed);
        Assert.Equal(3, (int)VerbosityLevel.Full);
    }

    [Fact]
    public void VerbosityLevel_CanBeCompared()
    {
        Assert.True(VerbosityLevel.None < VerbosityLevel.Summary);
        Assert.True(VerbosityLevel.Summary < VerbosityLevel.Detailed);
        Assert.True(VerbosityLevel.Detailed < VerbosityLevel.Full);
    }
}

public class VerbositySettingsTests
{
    [Fact]
    public void VerbositySettings_HasCorrectDefaults()
    {
        var settings = new VerbositySettings();

        Assert.Equal(VerbosityLevel.Detailed, settings.Level);
        Assert.True(settings.IncludeToolArguments);
        Assert.True(settings.IncludeToolResults);
        Assert.True(settings.IncludePerformanceMetrics);
        Assert.False(settings.IncludeConversationHistory);
        Assert.True(settings.SaveTraceFiles);
        Assert.Null(settings.TraceOutputDirectory);
    }

    [Fact]
    public void VerbositySettings_CanCustomize()
    {
        var settings = new VerbositySettings
        {
            Level = VerbosityLevel.Full,
            IncludeToolArguments = false,
            IncludeToolResults = false,
            IncludePerformanceMetrics = false,
            IncludeConversationHistory = true,
            SaveTraceFiles = false,
            TraceOutputDirectory = "/custom/path"
        };

        Assert.Equal(VerbosityLevel.Full, settings.Level);
        Assert.False(settings.IncludeToolArguments);
        Assert.False(settings.IncludeToolResults);
        Assert.False(settings.IncludePerformanceMetrics);
        Assert.True(settings.IncludeConversationHistory);
        Assert.False(settings.SaveTraceFiles);
        Assert.Equal("/custom/path", settings.TraceOutputDirectory);
    }
}

public class VerbosityConfigurationTests
{
    [Fact]
    public void VerbosityConfiguration_DefaultIsDetailed()
    {
        // Clear any override first
        VerbosityConfiguration.ClearOverride();
        
        // When no override or env var, default should be Detailed
        Assert.Equal(VerbosityLevel.Detailed, VerbosityConfiguration.Current);
    }

    [Fact]
    public void VerbosityConfiguration_OverrideTakesPrecedence()
    {
        try
        {
            VerbosityConfiguration.SetOverride(VerbosityLevel.Full);
            Assert.Equal(VerbosityLevel.Full, VerbosityConfiguration.Current);

            VerbosityConfiguration.SetOverride(VerbosityLevel.None);
            Assert.Equal(VerbosityLevel.None, VerbosityConfiguration.Current);
        }
        finally
        {
            VerbosityConfiguration.ClearOverride();
        }
    }

    [Fact]
    public void VerbosityConfiguration_ClearOverrideWorks()
    {
        VerbosityConfiguration.SetOverride(VerbosityLevel.Full);
        Assert.Equal(VerbosityLevel.Full, VerbosityConfiguration.Current);

        VerbosityConfiguration.ClearOverride();
        // Should return to default (Detailed) when no env var is set
        Assert.Equal(VerbosityLevel.Detailed, VerbosityConfiguration.Current);
    }

    [Fact]
    public void VerbosityConfiguration_SaveTraceArtifacts_TrueByDefault()
    {
        VerbosityConfiguration.ClearOverride();
        // When verbosity is not None, SaveTraceArtifacts should be true by default
        Assert.True(VerbosityConfiguration.SaveTraceArtifacts);
    }

    [Fact]
    public void VerbosityConfiguration_SaveTraceArtifacts_FalseWhenNone()
    {
        try
        {
            VerbosityConfiguration.SetOverride(VerbosityLevel.None);
            Assert.False(VerbosityConfiguration.SaveTraceArtifacts);
        }
        finally
        {
            VerbosityConfiguration.ClearOverride();
        }
    }

    [Fact]
    public void VerbosityConfiguration_TraceDirectory_HasDefaultValue()
    {
        var traceDir = VerbosityConfiguration.TraceDirectory;
        
        Assert.NotNull(traceDir);
        Assert.Contains("TestResults", traceDir);
        Assert.Contains("traces", traceDir);
    }
}

/// <summary>
/// Additional tests for VerbosityConfiguration environment variable handling.
/// </summary>
public class VerbosityConfigurationEnvironmentTests
{
    [Fact]
    public void SetOverride_MultipleTimesInSequence_TakesLastValue()
    {
        try
        {
            VerbosityConfiguration.SetOverride(VerbosityLevel.None);
            VerbosityConfiguration.SetOverride(VerbosityLevel.Summary);
            VerbosityConfiguration.SetOverride(VerbosityLevel.Full);
            
            Assert.Equal(VerbosityLevel.Full, VerbosityConfiguration.Current);
        }
        finally
        {
            VerbosityConfiguration.ClearOverride();
        }
    }

    [Fact]
    public void SetOverride_CanSwitchBetweenAllLevels()
    {
        try
        {
            foreach (var level in Enum.GetValues<VerbosityLevel>())
            {
                VerbosityConfiguration.SetOverride(level);
                Assert.Equal(level, VerbosityConfiguration.Current);
            }
        }
        finally
        {
            VerbosityConfiguration.ClearOverride();
        }
    }
}

/// <summary>
/// Tests for VerbositySettings edge cases.
/// </summary>
public class VerbositySettingsEdgeCaseTests
{
    [Fact]
    public void VerbositySettings_CanCombineAllOptions()
    {
        var settings = new VerbositySettings
        {
            Level = VerbosityLevel.Full,
            IncludeToolArguments = true,
            IncludeToolResults = true,
            IncludePerformanceMetrics = true,
            IncludeConversationHistory = true,
            SaveTraceFiles = true,
            TraceOutputDirectory = "/path/to/traces"
        };

        // All options should be set as specified
        Assert.True(settings.IncludeToolArguments);
        Assert.True(settings.IncludeToolResults);
        Assert.True(settings.IncludePerformanceMetrics);
        Assert.True(settings.IncludeConversationHistory);
        Assert.True(settings.SaveTraceFiles);
        Assert.Equal("/path/to/traces", settings.TraceOutputDirectory);
    }

    [Fact]
    public void VerbositySettings_CanDisableAllOptions()
    {
        var settings = new VerbositySettings
        {
            Level = VerbosityLevel.None,
            IncludeToolArguments = false,
            IncludeToolResults = false,
            IncludePerformanceMetrics = false,
            IncludeConversationHistory = false,
            SaveTraceFiles = false,
            TraceOutputDirectory = null
        };

        Assert.False(settings.IncludeToolArguments);
        Assert.False(settings.IncludeToolResults);
        Assert.False(settings.IncludePerformanceMetrics);
        Assert.False(settings.IncludeConversationHistory);
        Assert.False(settings.SaveTraceFiles);
        Assert.Null(settings.TraceOutputDirectory);
    }
}
