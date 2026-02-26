// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Models;

/// <summary>
/// A test case loaded from a dataset.
/// </summary>
public class DatasetTestCase
{
    /// <summary>Unique identifier for this test case.</summary>
    public string Id { get; set; } = "";
    
    /// <summary>Category or group (optional).</summary>
    public string? Category { get; set; }
    
    /// <summary>The input prompt/query.</summary>
    public string Input { get; set; } = "";
    
    /// <summary>Expected output/answer (for comparison).</summary>
    public string? ExpectedOutput { get; set; }
    
    /// <summary>Context documents (for RAG evaluation).</summary>
    public IReadOnlyList<string>? Context { get; set; }
    
    /// <summary>Expected tools to be called.</summary>
    public IReadOnlyList<string>? ExpectedTools { get; set; }
    
    /// <summary>Ground truth tool call (for function calling benchmarks).</summary>
    public GroundTruthToolCall? GroundTruth { get; set; }
    
    /// <summary>Evaluation criteria for AI-powered evaluation.</summary>
    public IReadOnlyList<string>? EvaluationCriteria { get; set; }
    
    /// <summary>Tags for categorizing test cases.</summary>
    public IReadOnlyList<string>? Tags { get; set; }
    
    /// <summary>Minimum score to pass (0-100). Maps to <c>TestCase.PassingScore</c>.</summary>
    public int? PassingScore { get; set; }
    
    /// <summary>Custom metadata.</summary>
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

/// <summary>
/// Ground truth for a tool/function call (used in BFCL-style benchmarks).
/// </summary>
public class GroundTruthToolCall
{
    /// <summary>Tool/function name.</summary>
    public string Name { get; set; } = "";
    
    /// <summary>Expected arguments.</summary>
    public Dictionary<string, object?> Arguments { get; set; } = new();
}
