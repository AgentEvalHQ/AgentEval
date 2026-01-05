// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for RetryPolicy functionality.
/// </summary>
public class RetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_SuccessOnFirstTry_ReturnsResult()
    {
        // Arrange
        var policy = RetryPolicy.Default;
        var callCount = 0;
        
        // Act
        var result = await policy.ExecuteAsync(async () =>
        {
            callCount++;
            await Task.Delay(1);
            return "success";
        });
        
        // Assert
        Assert.Equal("success", result);
        Assert.Equal(1, callCount);
    }
    
    [Fact]
    public async Task ExecuteAsync_SuccessAfterRetries_ReturnsResult()
    {
        // Arrange
        var policy = new RetryPolicy { MaxRetries = 3, InitialDelayMs = 10 };
        var callCount = 0;
        
        // Act
        var result = await policy.ExecuteAsync(async () =>
        {
            callCount++;
            if (callCount < 3)
            {
                throw new InvalidOperationException($"Fail {callCount}");
            }
            await Task.Delay(1);
            return "success";
        });
        
        // Assert
        Assert.Equal("success", result);
        Assert.Equal(3, callCount);
    }
    
    [Fact]
    public async Task ExecuteAsync_AllRetriesFail_ThrowsRetryExhaustedException()
    {
        // Arrange
        var policy = new RetryPolicy { MaxRetries = 2, InitialDelayMs = 10 };
        var callCount = 0;
        
        // Act & Assert
        var ex = await Assert.ThrowsAsync<RetryExhaustedException>(async () =>
        {
            await policy.ExecuteAsync<string>(async () =>
            {
                callCount++;
                await Task.Delay(1);
                throw new InvalidOperationException($"Fail {callCount}");
            });
        });
        
        Assert.Equal(3, callCount); // Initial + 2 retries
        Assert.Equal(3, ex.Attempts.Count);
        Assert.Contains("after 3 attempt(s)", ex.Message);
    }
    
    [Fact]
    public async Task ExecuteAsync_CancellationRequested_ThrowsWithoutRetry()
    {
        // Arrange
        var policy = new RetryPolicy { MaxRetries = 3 };
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var callCount = 0;
        
        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await policy.ExecuteAsync(async ct =>
            {
                callCount++;
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1, ct);
                return "success";
            }, cts.Token);
        });
        
        Assert.Equal(1, callCount); // Should not retry on cancellation
    }
    
    [Fact]
    public void None_HasZeroRetries()
    {
        Assert.Equal(0, RetryPolicy.None.MaxRetries);
    }
    
    [Fact]
    public void Default_HasThreeRetries()
    {
        Assert.Equal(3, RetryPolicy.Default.MaxRetries);
    }
    
    [Fact]
    public async Task EvaluateWithRetryAsync_ReturnsFailOnExhaustion()
    {
        // Arrange
        var policy = new RetryPolicy { MaxRetries = 1, InitialDelayMs = 10 };
        var metric = new FailingTestMetric();
        var context = new EvaluationContext { Input = "test", Output = "test" };
        
        // Act
        var result = await metric.EvaluateWithRetryAsync(context, policy);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("failed after 2 attempts", result.Explanation);
    }
    
    /// <summary>Test metric that always throws.</summary>
    private class FailingTestMetric : IMetric
    {
        public string Name => "FailingMetric";
        public string Description => "Always fails";
        
        public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken ct)
        {
            throw new InvalidOperationException("Intentional failure");
        }
    }
}
