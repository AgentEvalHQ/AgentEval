// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Models;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Tests for <see cref="MAFWorkflowAdapter.FromMAFWorkflow"/>.
/// Verifies the factory wires up event bridge + graph extractor correctly.
/// </summary>
public class MAFWorkflowAdapterFromMAFWorkflowTests
{
    [Fact]
    public void FromMAFWorkflow_ThrowsOnNullWorkflow()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MAFWorkflowAdapter.FromMAFWorkflow(null!, "test", ["A"]));
    }

    [Fact]
    public void FromMAFWorkflow_ThrowsOnNullName()
    {
        var binding = CreateFuncBinding("A");
        var workflow = new MAFWorkflows.WorkflowBuilder(binding).Build(validateOrphans: false);

        Assert.Throws<ArgumentNullException>(() =>
            MAFWorkflowAdapter.FromMAFWorkflow(workflow, null!, ["A"]));
    }

    [Fact]
    public void FromMAFWorkflow_ThrowsOnNullExecutorIds()
    {
        var binding = CreateFuncBinding("A");
        var workflow = new MAFWorkflows.WorkflowBuilder(binding).Build(validateOrphans: false);

        Assert.Throws<ArgumentNullException>(() =>
            MAFWorkflowAdapter.FromMAFWorkflow(workflow, "test", null!));
    }

    [Fact]
    public void FromMAFWorkflow_SetsNameCorrectly()
    {
        var binding = CreateFuncBinding("A");
        var workflow = new MAFWorkflows.WorkflowBuilder(binding).Build(validateOrphans: false);

        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "MyPipeline", ["A"]);

        Assert.Equal("MyPipeline", adapter.Name);
    }

    [Fact]
    public void FromMAFWorkflow_SetsWorkflowType()
    {
        var binding = CreateFuncBinding("A");
        var workflow = new MAFWorkflows.WorkflowBuilder(binding).Build(validateOrphans: false);

        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
            workflow, "MyPipeline", ["A"], workflowType: "PromptChaining");

        Assert.Equal("PromptChaining", adapter.WorkflowType);
    }

    [Fact]
    public void FromMAFWorkflow_SetsExecutorIds()
    {
        var a = CreateFuncBinding("A");
        var b = CreateFuncBinding("B");
        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge(a, b)
            .Build();

        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
            workflow, "Chain", ["A", "B"]);

        Assert.Equal(2, adapter.ExecutorIds.Count);
        Assert.Contains("A", adapter.ExecutorIds);
        Assert.Contains("B", adapter.ExecutorIds);
    }

    [Fact]
    public void FromMAFWorkflow_BuildsGraphDefinition()
    {
        var a = CreateFuncBinding("A");
        var b = CreateFuncBinding("B");
        var c = CreateFuncBinding("C");

        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge(a, b)
            .AddEdge(b, c)
            .Build();

        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
            workflow, "Pipeline", ["A", "B", "C"]);

        Assert.NotNull(adapter.GraphDefinition);
        Assert.Equal(3, adapter.GraphDefinition.Nodes.Count);
        Assert.Equal("A", adapter.GraphDefinition.EntryNodeId);
        Assert.Contains("C", adapter.GraphDefinition.ExitNodeIds);
        Assert.Equal(2, adapter.GraphDefinition.Edges.Count);
    }

    [Fact]
    public async Task FromMAFWorkflow_ExecuteWorkflowAsync_RunsRealExecution()
    {
        // Arrange
        var a = CreateFuncBinding("A");
        var b = CreateFuncBinding("B");

        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge(a, b)
            .WithOutputFrom(b)
            .Build();

        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
            workflow, "LiveTest", ["A", "B"]);

        // Act
        var result = await adapter.ExecuteWorkflowAsync("hello");

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Steps);
        Assert.NotNull(result.FinalOutput);
        Assert.True(result.TotalDuration > TimeSpan.Zero);
    }

    [Fact]
    public async Task FromMAFWorkflow_InvokeAsync_ReturnsText()
    {
        var a = CreateFuncBinding("A");
        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .WithOutputFrom(a)
            .Build(validateOrphans: false);

        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
            workflow, "Simple", ["A"]);

        var response = await adapter.InvokeAsync("hello");

        Assert.NotNull(response);
        Assert.NotNull(response.Text);
    }

    // ── Helper ──

    private static MAFWorkflows.ExecutorBinding CreateFuncBinding(string id)
    {
        return ((Func<string, ValueTask<string>>)(input => new ValueTask<string>($"processed:{input}")))
            .BindAsExecutor<string, string>(id);
    }
}
