// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Assertions;

/// <summary>
/// Unit tests for Behavioral Policy assertions (NeverCallTool, NeverPassArgumentMatching, MustConfirmBefore).
/// </summary>
public class BehavioralPolicyAssertionsTests
{
    #region NeverCallTool Tests
    
    [Fact]
    public void NeverCallTool_WhenToolNotCalled_DoesNotThrow()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "SafeTool", CallId = "call-1", Order = 1 });
        
        // Act
        var exception = Record.Exception(() => 
            report.Should().NeverCallTool("DeleteDatabase", because: "production data protection"));
        
        // Assert
        Assert.Null(exception);
    }
    
    [Fact]
    public void NeverCallTool_WhenToolCalled_ThrowsBehavioralPolicyViolationException()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "DeleteDatabase", CallId = "call-1", Order = 1 });
        
        // Act
        var exception = Assert.Throws<BehavioralPolicyViolationException>(() => 
            report.Should().NeverCallTool("DeleteDatabase", because: "production data protection"));
        
        // Assert
        Assert.Equal("NeverCallTool(DeleteDatabase)", exception.PolicyName);
        Assert.Equal("ForbiddenTool", exception.ViolationType);
        Assert.Equal("DeleteDatabase", exception.ForbiddenToolName);
        Assert.Contains("production data protection", exception.Because);
    }
    
    [Fact]
    public void NeverCallTool_WhenToolCalledMultipleTimes_ReportsAllCalls()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "DeleteDatabase", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "SafeTool", CallId = "call-2", Order = 2 });
        report.AddCall(new ToolCallRecord { Name = "DeleteDatabase", CallId = "call-3", Order = 3 });
        
        // Act
        var exception = Assert.Throws<BehavioralPolicyViolationException>(() => 
            report.Should().NeverCallTool("DeleteDatabase", because: "never delete"));
        
        // Assert
        Assert.Contains("2 time(s)", exception.ViolatingAction);
    }
    
    [Fact]
    public void NeverCallTool_CanBeChained()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "SafeTool", CallId = "call-1", Order = 1 });
        
        // Act - chain multiple NeverCallTool assertions
        var exception = Record.Exception(() => 
            report.Should()
                .NeverCallTool("DeleteDatabase", because: "data protection")
                .NeverCallTool("ExecuteTrade", because: "trades require approval")
                .NeverCallTool("SendEmail", because: "emails need review"));
        
        // Assert
        Assert.Null(exception);
    }
    
    #endregion
    
    #region NeverPassArgumentMatching Tests
    
    [Fact]
    public void NeverPassArgumentMatching_WhenNoMatchingPatterns_DoesNotThrow()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "SendMessage", 
            CallId = "call-1", 
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["content"] = "Hello, World!" }
        });
        
        // Act
        var exception = Record.Exception(() => 
            report.Should().NeverPassArgumentMatching(
                @"\b\d{3}-\d{2}-\d{4}\b", 
                because: "SSN must never be passed"));
        
        // Assert
        Assert.Null(exception);
    }
    
    [Fact]
    public void NeverPassArgumentMatching_WhenSSNDetected_ThrowsException()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "SendMessage", 
            CallId = "call-1", 
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["content"] = "User SSN is 123-45-6789" }
        });
        
        // Act
        var exception = Assert.Throws<BehavioralPolicyViolationException>(() => 
            report.Should().NeverPassArgumentMatching(
                @"\b\d{3}-\d{2}-\d{4}\b", 
                because: "SSN must never be passed"));
        
        // Assert
        Assert.Equal("SensitiveData", exception.ViolationType);
        Assert.Equal("SendMessage", exception.ToolName);
        Assert.Equal("content", exception.ArgumentName);
        Assert.Contains(@"\b\d{3}-\d{2}-\d{4}\b", exception.MatchedPattern);
        Assert.NotNull(exception.RedactedValue);
    }
    
    [Fact]
    public void NeverPassArgumentMatching_WhenEmailDetected_ThrowsException()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "LogData", 
            CallId = "call-1", 
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["data"] = "Contact: john.doe@example.com" }
        });
        
        // Act
        var exception = Assert.Throws<BehavioralPolicyViolationException>(() => 
            report.Should().NeverPassArgumentMatching(
                @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", 
                because: "email addresses must be anonymized"));
        
        // Assert
        Assert.Equal("SensitiveData", exception.ViolationType);
        Assert.Contains("email addresses must be anonymized", exception.Because);
    }
    
    [Fact]
    public void NeverPassArgumentMatching_RedactsMatchedValue()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "StoreData", 
            CallId = "call-1", 
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["ssn"] = "123-45-6789" }
        });
        
        // Act
        var exception = Assert.Throws<BehavioralPolicyViolationException>(() => 
            report.Should().NeverPassArgumentMatching(
                @"\d{3}-\d{2}-\d{4}", 
                because: "SSN protection"));
        
        // Assert
        // Redacted value should mask middle characters
        Assert.NotNull(exception.RedactedValue);
        Assert.DoesNotContain("45", exception.RedactedValue);
    }
    
    [Fact]
    public void NeverPassArgumentMatching_WithRegexOptions_RespectsOptions()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "SendMessage", 
            CallId = "call-1", 
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["content"] = "FORBIDDEN_KEYWORD" }
        });
        
        // Act - case insensitive should match
        var exception = Assert.Throws<BehavioralPolicyViolationException>(() => 
            report.Should().NeverPassArgumentMatching(
                "forbidden_keyword", 
                because: "keyword forbidden",
                options: System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        
        // Assert
        Assert.NotNull(exception);
    }
    
    #endregion
    
    #region MustConfirmBefore Tests
    
    [Fact]
    public void MustConfirmBefore_WhenToolNotCalled_DoesNotThrow()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "ReadData", CallId = "call-1", Order = 1 });
        
        // Act - tool that requires confirmation isn't called, so policy doesn't apply
        var exception = Record.Exception(() => 
            report.Should().MustConfirmBefore("TransferFunds", because: "financial approval required"));
        
        // Assert
        Assert.Null(exception);
    }
    
    [Fact]
    public void MustConfirmBefore_WhenConfirmationPrecedes_DoesNotThrow()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Confirm", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "TransferFunds", CallId = "call-2", Order = 2 });
        
        // Act
        var exception = Record.Exception(() => 
            report.Should().MustConfirmBefore("TransferFunds", because: "financial approval required"));
        
        // Assert
        Assert.Null(exception);
    }
    
    [Fact]
    public void MustConfirmBefore_WhenNoConfirmation_ThrowsException()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "TransferFunds", CallId = "call-1", Order = 1 });
        
        // Act
        var exception = Assert.Throws<BehavioralPolicyViolationException>(() => 
            report.Should().MustConfirmBefore("TransferFunds", because: "financial approval required"));
        
        // Assert
        Assert.Equal("MustConfirmBefore(TransferFunds)", exception.PolicyName);
        Assert.Equal("MissingConfirmation", exception.ViolationType);
        Assert.Contains("financial approval required", exception.Because);
    }
    
    [Fact]
    public void MustConfirmBefore_WhenConfirmationAfter_ThrowsException()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "TransferFunds", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Confirm", CallId = "call-2", Order = 2 }); // Too late!
        
        // Act
        var exception = Assert.Throws<BehavioralPolicyViolationException>(() => 
            report.Should().MustConfirmBefore("TransferFunds", because: "approval first"));
        
        // Assert
        Assert.Equal("MissingConfirmation", exception.ViolationType);
    }
    
    [Fact]
    public void MustConfirmBefore_WithCustomConfirmationTool_RecognizesIt()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "RequestUserApproval", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "DeleteRecord", CallId = "call-2", Order = 2 });
        
        // Act
        var exception = Record.Exception(() => 
            report.Should().MustConfirmBefore(
                "DeleteRecord", 
                because: "deletion requires approval",
                confirmationToolName: "RequestUserApproval"));
        
        // Assert
        Assert.Null(exception);
    }
    
    [Fact]
    public void MustConfirmBefore_RecognizesMultipleDefaultConfirmationTools()
    {
        // Arrange - test different default confirmation tool names
        var confirmTools = new[] { "Confirm", "RequestConfirmation", "AskForConfirmation", "GetUserApproval", "ConfirmAction" };
        
        foreach (var confirmTool in confirmTools)
        {
            var report = new ToolUsageReport();
            report.AddCall(new ToolCallRecord { Name = confirmTool, CallId = "call-1", Order = 1 });
            report.AddCall(new ToolCallRecord { Name = "DangerousAction", CallId = "call-2", Order = 2 });
            
            // Act
            var exception = Record.Exception(() => 
                report.Should().MustConfirmBefore("DangerousAction", because: "safety"));
            
            // Assert
            Assert.Null(exception);
        }
    }
    
    [Fact]
    public void MustConfirmBefore_FailsForEachUnconfirmedCall()
    {
        // Arrange - tool called twice, but only one confirmation before first call
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Confirm", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "TransferFunds", CallId = "call-2", Order = 2 }); // OK
        report.AddCall(new ToolCallRecord { Name = "TransferFunds", CallId = "call-3", Order = 3 }); // Also OK (confirmation still precedes)
        
        // Act
        var exception = Record.Exception(() => 
            report.Should().MustConfirmBefore("TransferFunds", because: "approval needed"));
        
        // Assert - both calls have confirmation before them
        Assert.Null(exception);
    }
    
    #endregion
    
    #region BehavioralPolicyViolationException Tests
    
    [Fact]
    public void BehavioralPolicyViolationException_Create_FormatsMessageCorrectly()
    {
        // Act
        var exception = BehavioralPolicyViolationException.Create(
            message: "Test violation",
            policyName: "TestPolicy",
            violationType: "TestType",
            violatingAction: "Did something bad",
            because: "testing purposes",
            suggestions: new[] { "Fix it", "Don't do that" });
        
        // Assert
        Assert.Contains("Behavioral Policy Violation", exception.Message);
        Assert.Contains("TestPolicy", exception.Message);
        Assert.Contains("TestType", exception.Message);
        Assert.Contains("Did something bad", exception.Message);
        Assert.Contains("testing purposes", exception.Message);
        Assert.Contains("Fix it", exception.Message);
    }
    
    [Fact]
    public void BehavioralPolicyViolationException_RedactSensitiveData_MasksMiddleCharacters()
    {
        // Test cases for redaction
        var result1 = BehavioralPolicyViolationException.RedactSensitiveData("123-45-6789", 0, 11);
        Assert.StartsWith("1", result1);
        Assert.EndsWith("9", result1);
        Assert.Contains("***", result1);
        
        // Short values get fully masked
        var result2 = BehavioralPolicyViolationException.RedactSensitiveData("ab", 0, 2);
        Assert.Equal("****", result2);
    }
    
    #endregion
    
    #region Combined Policy Assertions Tests
    
    [Fact]
    public void CombinedPolicies_AllPass_DoesNotThrow()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "Confirm", 
            CallId = "call-1", 
            Order = 1 
        });
        report.AddCall(new ToolCallRecord 
        { 
            Name = "TransferFunds", 
            CallId = "call-2", 
            Order = 2,
            Arguments = new Dictionary<string, object?> { ["amount"] = "100", ["to"] = "account123" }
        });
        
        // Act
        var exception = Record.Exception(() => 
            report.Should()
                .NeverCallTool("DeleteDatabase", because: "data protection")
                .NeverPassArgumentMatching(@"\b\d{3}-\d{2}-\d{4}\b", because: "no SSN")
                .MustConfirmBefore("TransferFunds", because: "financial safety")
                .HaveNoErrors());
        
        // Assert
        Assert.Null(exception);
    }
    
    [Fact]
    public void CombinedPolicies_FirstFailure_ThrowsImmediately()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "DeleteDatabase", CallId = "call-1", Order = 1 });
        
        // Act - NeverCallTool should fail first
        var exception = Assert.Throws<BehavioralPolicyViolationException>(() => 
            report.Should()
                .NeverCallTool("DeleteDatabase", because: "forbidden")
                .NeverCallTool("OtherTool", because: "also forbidden"));
        
        // Assert
        Assert.Contains("DeleteDatabase", exception.PolicyName);
    }
    
    #endregion
}
