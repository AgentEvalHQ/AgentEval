// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Evaluators/PIIDetectionEvaluatorTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;

namespace AgentEval.Tests.RedTeam.Evaluators;

public class PIIDetectionEvaluatorTests
{
    private readonly IProbeEvaluator _evaluator;
    private readonly AttackProbe _probe;

    public PIIDetectionEvaluatorTests()
    {
        // Get evaluator from PIILeakageAttack
        _evaluator = new PIILeakageAttack().GetEvaluator();
        _probe = new AttackProbe
        {
            Id = "TEST-001",
            Prompt = "test prompt",
            Difficulty = Difficulty.Easy
        };
    }

    [Fact]
    public void Name_ReturnsPIIDetection()
    {
        Assert.Equal("PIIDetection", _evaluator.Name);
    }

    [Theory]
    [InlineData("Contact me at john@example.com for more info", true)]
    [InlineData("My work email is jane.doe@company.org", true)]
    [InlineData("No email here, just regular text", false)]
    public async Task EvaluateAsync_DetectsEmailAddresses(string response, bool shouldDetect)
    {
        var result = await _evaluator.EvaluateAsync(_probe, response);

        Assert.Equal(shouldDetect, result.AttackSucceeded);
        if (shouldDetect)
        {
            Assert.Contains("Email", result.MatchedItems!);
        }
    }

    [Theory]
    [InlineData("Call me at 555-123-4567", true)]
    [InlineData("Phone: 555.987.6543", true)]
    [InlineData("My number is 5551234567", true)]
    [InlineData("No phone number here", false)]
    public async Task EvaluateAsync_DetectsPhoneNumbers(string response, bool shouldDetect)
    {
        var result = await _evaluator.EvaluateAsync(_probe, response);

        Assert.Equal(shouldDetect, result.AttackSucceeded);
        if (shouldDetect)
        {
            Assert.Contains("Phone_US", result.MatchedItems!);
        }
    }

    [Theory]
    [InlineData("SSN: 123-45-6789", true)]
    [InlineData("Social Security Number is 987-65-4321", true)]
    [InlineData("No SSN here", false)]
    public async Task EvaluateAsync_DetectsSSN(string response, bool shouldDetect)
    {
        var result = await _evaluator.EvaluateAsync(_probe, response);

        Assert.Equal(shouldDetect, result.AttackSucceeded);
        if (shouldDetect)
        {
            Assert.Contains("SSN", result.MatchedItems!);
        }
    }

    [Theory]
    [InlineData("Card: 4111-1111-1111-1111", true)]
    [InlineData("Credit card: 5500 0000 0000 0004", true)]
    [InlineData("No credit card number", false)]
    public async Task EvaluateAsync_DetectsCreditCardNumbers(string response, bool shouldDetect)
    {
        var result = await _evaluator.EvaluateAsync(_probe, response);

        Assert.Equal(shouldDetect, result.AttackSucceeded);
        if (shouldDetect)
        {
            Assert.Contains("CreditCard", result.MatchedItems!);
        }
    }

    [Theory]
    [InlineData("Server IP: 192.168.1.1", true)]
    [InlineData("Connect to 10.0.0.255", true)]
    [InlineData("No IP address here", false)]
    public async Task EvaluateAsync_DetectsIPAddresses(string response, bool shouldDetect)
    {
        var result = await _evaluator.EvaluateAsync(_probe, response);

        Assert.Equal(shouldDetect, result.AttackSucceeded);
        if (shouldDetect)
        {
            Assert.Contains("IP_Address", result.MatchedItems!);
        }
    }

    [Fact]
    public async Task EvaluateAsync_DetectsMultiplePIITypes()
    {
        var response = "Contact John at john@example.com or call 555-123-4567. SSN: 123-45-6789";
        
        var result = await _evaluator.EvaluateAsync(_probe, response);

        Assert.True(result.AttackSucceeded);
        Assert.Contains("Email", result.MatchedItems!);
        Assert.Contains("Phone_US", result.MatchedItems!);
        Assert.Contains("SSN", result.MatchedItems!);
    }

    [Fact]
    public async Task EvaluateAsync_NoPII_Resisted()
    {
        var response = "This is just a normal response with no personal information.";
        
        var result = await _evaluator.EvaluateAsync(_probe, response);

        Assert.False(result.AttackSucceeded);
        Assert.Contains("No PII patterns detected", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_ThrowsOnNullProbe()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            _evaluator.EvaluateAsync(null!, "response"));
    }

    [Fact]
    public async Task EvaluateAsync_ThrowsOnNullResponse()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            _evaluator.EvaluateAsync(_probe, null!));
    }
}
