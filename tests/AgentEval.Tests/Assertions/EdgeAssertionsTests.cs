// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Models;

namespace AgentEval.Tests.Assertions;

/// <summary>
/// Tests for edge-related workflow assertions.
/// </summary>
public class EdgeAssertionsTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // GRAPH STRUCTURE ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveGraphStructure_Should_PassWhenGraphExists()
    {
        var result = CreateResultWithGraph();

        result.Should()
            .HaveGraphStructure()
            .Validate();
    }

    [Fact]
    public void HaveGraphStructure_Should_FailWhenGraphIsNull()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [],
            Graph = null
        };

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should().HaveGraphStructure().Validate());
        
        Assert.Contains("graph structure", exception.Message.ToLower());
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EDGE TRAVERSAL ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveTraversedEdge_Should_PassWhenEdgeWasTraversed()
    {
        var result = CreateResultWithTraversedEdges();

        result.Should()
            .HaveTraversedEdge("agent-a", "agent-b")
            .Validate();
    }

    [Fact]
    public void HaveTraversedEdge_Should_FailWhenEdgeWasNotTraversed()
    {
        var result = CreateResultWithTraversedEdges();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should().HaveTraversedEdge("agent-a", "agent-x").Validate());
        
        Assert.Contains("agent-a", exception.Message);
        Assert.Contains("agent-x", exception.Message);
    }

    [Fact]
    public void HaveUsedEdgeType_Should_PassWhenEdgeTypeExists()
    {
        var result = CreateResultWithConditionalEdge();

        result.Should()
            .HaveUsedEdgeType(EdgeType.Conditional)
            .Validate();
    }

    [Fact]
    public void HaveUsedEdgeType_Should_FailWhenEdgeTypeNotUsed()
    {
        var result = CreateResultWithTraversedEdges();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should().HaveUsedEdgeType(EdgeType.ParallelFanOut).Validate());
        
        Assert.Contains("ParallelFanOut", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CONDITIONAL ROUTING ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveConditionalRouting_Should_PassWhenConditionalEdgesExist()
    {
        var result = CreateResultWithConditionalEdge();

        result.Should()
            .HaveConditionalRouting()
            .Validate();
    }

    [Fact]
    public void HaveConditionalRouting_Should_FailWhenNoConditionalEdges()
    {
        var result = CreateResultWithTraversedEdges();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should().HaveConditionalRouting().Validate());
        
        Assert.Contains("conditional routing", exception.Message.ToLower());
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PARALLEL EXECUTION ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveParallelExecution_Should_PassWhenParallelEdgesExist()
    {
        var result = CreateResultWithParallelExecution();

        result.Should()
            .HaveParallelExecution()
            .Validate();
    }

    [Fact]
    public void HaveParallelExecution_Should_FailWhenNoParallelEdges()
    {
        var result = CreateResultWithTraversedEdges();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should().HaveParallelExecution().Validate());
        
        Assert.Contains("parallel execution", exception.Message.ToLower());
    }

    [Fact]
    public void HaveParallelBranchCount_Should_PassWhenCountMatches()
    {
        var result = CreateResultWithParallelBranches();

        result.Should()
            .HaveParallelBranchCount(2)
            .Validate();
    }

    [Fact]
    public void HaveParallelBranchCount_Should_FailWhenCountDoesNotMatch()
    {
        var result = CreateResultWithParallelBranches();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should().HaveParallelBranchCount(3).Validate());
        
        Assert.Contains("3", exception.Message);
        Assert.Contains("2", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ROUTING DECISION ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveRoutingDecision_Should_PassWhenDecisionExists()
    {
        var result = CreateResultWithRoutingDecision();

        result.Should()
            .HaveRoutingDecision("classifier", "edge-billing")
            .Validate();
    }

    [Fact]
    public void HaveRoutingDecision_Should_FailWhenDecisionNotFound()
    {
        var result = CreateResultWithRoutingDecision();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should().HaveRoutingDecision("classifier", "edge-unknown").Validate());
        
        Assert.Contains("edge-unknown", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXECUTION PATH ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveExecutionPath_Should_PassWhenPathMatches()
    {
        var result = CreateResultWithTraversedEdges();

        result.Should()
            .HaveExecutionPath("agent-a", "agent-b", "agent-c")
            .Validate();
    }

    [Fact]
    public void HaveExecutionPath_Should_FailWhenPathDoesNotMatch()
    {
        var result = CreateResultWithTraversedEdges();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should().HaveExecutionPath("agent-a", "agent-c", "agent-b").Validate());
        
        Assert.Contains("execution path", exception.Message.ToLower());
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NODE ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveNodes_Should_PassWhenAllNodesExist()
    {
        var result = CreateResultWithGraph();

        result.Should()
            .HaveNodes("agent-a", "agent-b")
            .Validate();
    }

    [Fact]
    public void HaveNodes_Should_FailWhenNodesMissing()
    {
        var result = CreateResultWithGraph();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should().HaveNodes("agent-a", "agent-x").Validate());
        
        Assert.Contains("agent-x", exception.Message);
    }

    [Fact]
    public void HaveEntryPoint_Should_PassWhenEntryPointMatches()
    {
        var result = CreateResultWithGraph();

        result.Should()
            .HaveEntryPoint("agent-a")
            .Validate();
    }

    [Fact]
    public void HaveEntryPoint_Should_FailWhenEntryPointDoesNotMatch()
    {
        var result = CreateResultWithGraph();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should().HaveEntryPoint("agent-b").Validate());
        
        Assert.Contains("agent-b", exception.Message);
        Assert.Contains("agent-a", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EDGE ASSERTION BUILDER TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForEdge_Exist_Should_PassWhenEdgeExists()
    {
        var result = CreateResultWithTraversedEdges();

        result.Should()
            .ForEdge("agent-a", "agent-b")
                .Exist()
            .And()
            .Validate();
    }

    [Fact]
    public void ForEdge_Exist_Should_FailWhenEdgeDoesNotExist()
    {
        var result = CreateResultWithTraversedEdges();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should()
                .ForEdge("agent-a", "agent-x")
                    .Exist()
                .And()
                .Validate());
        
        Assert.Contains("agent-a", exception.Message);
        Assert.Contains("agent-x", exception.Message);
    }

    [Fact]
    public void ForEdge_BeOfType_Should_PassWhenTypeMatches()
    {
        var result = CreateResultWithConditionalEdge();

        result.Should()
            .ForEdge("router", "handler")
                .BeOfType(EdgeType.Conditional)
            .And()
            .Validate();
    }

    [Fact]
    public void ForEdge_BeOfType_Should_FailWhenTypeDoesNotMatch()
    {
        var result = CreateResultWithTraversedEdges();

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should()
                .ForEdge("agent-a", "agent-b")
                    .BeOfType(EdgeType.Conditional)
                .And()
                .Validate());
        
        Assert.Contains("Conditional", exception.Message);
        Assert.Contains("Sequential", exception.Message);
    }

    [Fact]
    public void ForEdge_HaveConditionResult_Should_PassWhenResultMatches()
    {
        var result = CreateResultWithConditionalEdge();

        result.Should()
            .ForEdge("router", "handler")
                .HaveConditionResult(true)
            .And()
            .Validate();
    }

    [Fact]
    public void ForEdge_HaveMatchedSwitchLabel_Should_PassWhenLabelMatches()
    {
        var result = CreateResultWithSwitchEdge();

        result.Should()
            .ForEdge("classifier", "billing-handler")
                .HaveMatchedSwitchLabel("billing")
            .And()
            .Validate();
    }

    [Fact]
    public void ForEdge_HaveTransferredDataContaining_Should_PassWhenDataContainsExpected()
    {
        var result = CreateResultWithDataTransfer();

        result.Should()
            .ForEdge("producer", "consumer")
                .HaveTransferredDataContaining("important data")
            .And()
            .Validate();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXECUTOR STEP EDGE ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForExecutor_HaveBeenConditionallyRouted_Should_PassWhenTrue()
    {
        var result = CreateResultWithConditionallyRoutedStep();

        result.Should()
            .ForExecutor("handler")
                .HaveBeenConditionallyRouted()
            .And()
            .Validate();
    }

    [Fact]
    public void ForExecutor_BeInParallelBranch_Should_PassWhenInBranch()
    {
        var result = CreateResultWithParallelStep();

        result.Should()
            .ForExecutor("parallel-worker")
                .BeInParallelBranch()
            .And()
            .Validate();
    }

    [Fact]
    public void ForExecutor_BeInParallelBranch_WithId_Should_PassWhenBranchIdMatches()
    {
        var result = CreateResultWithParallelStep();

        result.Should()
            .ForExecutor("parallel-worker")
                .BeInParallelBranch("branch-1")
            .And()
            .Validate();
    }

    [Fact]
    public void ForExecutor_HaveIncomingEdge_Should_PassWhenEdgeExists()
    {
        var result = CreateResultWithConditionallyRoutedStep();

        result.Should()
            .ForExecutor("handler")
                .HaveIncomingEdge()
            .And()
            .Validate();
    }

    [Fact]
    public void ForExecutor_HaveIncomingEdgeOfType_Should_PassWhenTypeMatches()
    {
        var result = CreateResultWithConditionallyRoutedStep();

        result.Should()
            .ForExecutor("handler")
                .HaveIncomingEdgeOfType(EdgeType.Conditional)
            .And()
            .Validate();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CHAINED ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ChainedAssertions_Should_ValidateMultipleConditions()
    {
        var result = CreateResultWithConditionalEdge();

        result.Should()
            .HaveGraphStructure()
            .HaveConditionalRouting()
            .HaveUsedEdgeType(EdgeType.Conditional)
            .ForEdge("router", "handler")
                .BeOfType(EdgeType.Conditional)
                .HaveConditionResult(true)
            .And()
            .HaveSucceeded()
            .Validate();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════════════

    private static WorkflowExecutionResult CreateResultWithGraph()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [
                new ExecutorStep { ExecutorId = "agent-a", Output = "a", StepIndex = 0 },
                new ExecutorStep { ExecutorId = "agent-b", Output = "b", StepIndex = 1 }
            ],
            Graph = new WorkflowGraphSnapshot
            {
                Nodes = [
                    new WorkflowNode { NodeId = "agent-a", IsEntryPoint = true },
                    new WorkflowNode { NodeId = "agent-b", IsExitNode = true }
                ],
                Edges = [
                    new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "agent-a", TargetExecutorId = "agent-b" }
                ],
                EntryNodeId = "agent-a",
                ExitNodeIds = ["agent-b"]
            }
        };
    }

    private static WorkflowExecutionResult CreateResultWithTraversedEdges()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [
                new ExecutorStep { ExecutorId = "agent-a", Output = "a", StepIndex = 0 },
                new ExecutorStep { ExecutorId = "agent-b", Output = "b", StepIndex = 1 },
                new ExecutorStep { ExecutorId = "agent-c", Output = "c", StepIndex = 2 }
            ],
            Graph = new WorkflowGraphSnapshot
            {
                Nodes = [
                    new WorkflowNode { NodeId = "agent-a" },
                    new WorkflowNode { NodeId = "agent-b" },
                    new WorkflowNode { NodeId = "agent-c" }
                ],
                Edges = [
                    new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "agent-a", TargetExecutorId = "agent-b" },
                    new WorkflowEdge { EdgeId = "e2", SourceExecutorId = "agent-b", TargetExecutorId = "agent-c" }
                ],
                TraversedEdges = [
                    new EdgeExecution { EdgeId = "e1", SourceExecutorId = "agent-a", TargetExecutorId = "agent-b", EdgeType = EdgeType.Sequential, TraversedAt = TimeSpan.FromSeconds(1) },
                    new EdgeExecution { EdgeId = "e2", SourceExecutorId = "agent-b", TargetExecutorId = "agent-c", EdgeType = EdgeType.Sequential, TraversedAt = TimeSpan.FromSeconds(2) }
                ],
                EntryNodeId = "agent-a",
                ExitNodeIds = ["agent-c"]
            }
        };
    }

    private static WorkflowExecutionResult CreateResultWithConditionalEdge()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [
                new ExecutorStep { ExecutorId = "router", Output = "routing", StepIndex = 0 },
                new ExecutorStep { ExecutorId = "handler", Output = "handled", StepIndex = 1 }
            ],
            Graph = new WorkflowGraphSnapshot
            {
                Nodes = [
                    new WorkflowNode { NodeId = "router" },
                    new WorkflowNode { NodeId = "handler" }
                ],
                Edges = [
                    new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "router", TargetExecutorId = "handler", EdgeType = EdgeType.Conditional }
                ],
                TraversedEdges = [
                    new EdgeExecution { 
                        EdgeId = "e1", 
                        SourceExecutorId = "router", 
                        TargetExecutorId = "handler", 
                        EdgeType = EdgeType.Conditional,
                        ConditionResult = true,
                        TraversedAt = TimeSpan.FromSeconds(1)
                    }
                ],
                EntryNodeId = "router",
                ExitNodeIds = ["handler"]
            }
        };
    }

    private static WorkflowExecutionResult CreateResultWithParallelExecution()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [],
            Graph = new WorkflowGraphSnapshot
            {
                Nodes = [new WorkflowNode { NodeId = "splitter" }],
                Edges = [
                    new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "splitter", TargetExecutorId = "worker-a", EdgeType = EdgeType.ParallelFanOut }
                ],
                EntryNodeId = "splitter",
                ExitNodeIds = ["worker-a"]
            }
        };
    }

    private static WorkflowExecutionResult CreateResultWithParallelBranches()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [],
            Graph = new WorkflowGraphSnapshot
            {
                Nodes = [new WorkflowNode { NodeId = "a" }],
                Edges = [],
                ParallelBranches = [
                    new ParallelBranch { BranchId = "branch-1", ExecutorIds = ["a", "b"] },
                    new ParallelBranch { BranchId = "branch-2", ExecutorIds = ["c", "d"] }
                ],
                EntryNodeId = "a",
                ExitNodeIds = ["a"]
            }
        };
    }

    private static WorkflowExecutionResult CreateResultWithRoutingDecision()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [],
            RoutingDecisions = [
                new RoutingDecision
                {
                    DeciderExecutorId = "classifier",
                    PossibleEdgeIds = ["edge-technical", "edge-billing", "edge-general"],
                    SelectedEdgeId = "edge-billing",
                    EvaluatedValue = "billing inquiry"
                }
            ]
        };
    }

    private static WorkflowExecutionResult CreateResultWithSwitchEdge()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [],
            Graph = new WorkflowGraphSnapshot
            {
                Nodes = [new WorkflowNode { NodeId = "classifier" }],
                Edges = [
                    new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "classifier", TargetExecutorId = "billing-handler", EdgeType = EdgeType.Switch }
                ],
                TraversedEdges = [
                    new EdgeExecution { 
                        EdgeId = "e1", 
                        SourceExecutorId = "classifier", 
                        TargetExecutorId = "billing-handler", 
                        EdgeType = EdgeType.Switch,
                        MatchedSwitchLabel = "billing"
                    }
                ],
                EntryNodeId = "classifier",
                ExitNodeIds = ["billing-handler"]
            }
        };
    }

    private static WorkflowExecutionResult CreateResultWithDataTransfer()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [],
            Graph = new WorkflowGraphSnapshot
            {
                Nodes = [new WorkflowNode { NodeId = "producer" }],
                Edges = [],
                TraversedEdges = [
                    new EdgeExecution { 
                        EdgeId = "e1", 
                        SourceExecutorId = "producer", 
                        TargetExecutorId = "consumer",
                        TransferredData = "This is important data that was transferred"
                    }
                ],
                EntryNodeId = "producer",
                ExitNodeIds = ["consumer"]
            }
        };
    }

    private static WorkflowExecutionResult CreateResultWithConditionallyRoutedStep()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [
                new ExecutorStep { 
                    ExecutorId = "handler", 
                    Output = "handled", 
                    StepIndex = 0,
                    IncomingEdge = new EdgeExecution
                    {
                        EdgeId = "e1",
                        SourceExecutorId = "router",
                        TargetExecutorId = "handler",
                        EdgeType = EdgeType.Conditional
                    }
                }
            ]
        };
    }

    private static WorkflowExecutionResult CreateResultWithParallelStep()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = [
                new ExecutorStep { 
                    ExecutorId = "parallel-worker", 
                    Output = "work done", 
                    StepIndex = 0,
                    ParallelBranchId = "branch-1"
                }
            ]
        };
    }
}
