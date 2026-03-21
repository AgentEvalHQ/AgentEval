# Agent TKI — Benchmark Reporting: Architecture & Implementation Guide

## Document Purpose

This document contains exhaustive recommendations for implementing rich, interactive benchmark reporting for the Agent TKI project. It covers the full pipeline: data ingestion from JSON baselines, visualization components, statistical evaluation strategies (including stochastic + memory evaluation), profile archetypes, progression tracking, regression detection, and the spider/radar graph system. These recommendations are intended to be handed to an implementing model or developer for incorporation into the design.

---

## 1. Overall Architecture

### 1.1 Pipeline Overview

The reporting system should follow this data flow:

```
JSON Baselines → Parser/Normalizer → Metrics Store → Evaluation Engine → Report Generator → Interactive HTML Output
```

Each stage is decoupled so individual components can be replaced or extended independently.

### 1.2 Tech Stack Recommendation

| Layer | Technology | Rationale |
|---|---|---|
| Baseline storage | JSON files (one per run) | Human-readable, diffable in git, easy to produce from .NET |
| Report generator | Node.js script or .NET tool that emits standalone HTML | Can run in CI/CD, no server needed |
| Charting library | **Plotly.js** (primary) or Chart.js (lightweight alternative) | Plotly handles radar/spider charts, timelines, heatmaps, box plots natively with interactivity; Chart.js is lighter if bundle size matters |
| Layout/styling | Tailwind CSS (CDN) + a single HTML file | Self-contained, no build step, looks polished |
| Statistical analysis | Inline JS using simple-statistics or math.js, OR pre-compute in .NET and embed results in JSON | Keeps everything in one artifact |
| LLM integration (optional) | Anthropic API call from the report generator | For narrative summaries, anomaly explanations, archetype classification |

### 1.3 Why Standalone HTML

A single self-contained HTML file (with inlined JS/CSS and embedded JSON data) is the ideal output format because: it opens in any browser without a server, it can be committed to git alongside the baselines, it can be attached to CI artifacts, and it can be emailed or shared as-is. Plotly and Chart.js both support this pattern well.

---

## 2. Benchmark JSON Structure

### 2.1 Baseline File Format

Each benchmark run should produce a JSON file with this structure:

```json
{
  "metadata": {
    "run_id": "run-2026-03-15-001",
    "timestamp": "2026-03-15T14:30:00Z",
    "agent_version": "1.4.2",
    "configuration": {
      "model": "gpt-4o",
      "temperature": 0.7,
      "max_tokens": 4096,
      "memory_enabled": true,
      "memory_backend": "vector-store-v2"
    },
    "environment": {
      "runtime": "dotnet-8.0",
      "os": "linux",
      "hardware": "4-core, 16GB"
    },
    "tags": ["nightly", "regression-suite", "memory-on"]
  },
  "metrics": {
    "task_completion_rate": 0.87,
    "accuracy": 0.92,
    "coherence": 4.2,
    "relevance": 4.5,
    "tool_usage_efficiency": 0.78,
    "hallucination_rate": 0.03,
    "latency_p50_ms": 1200,
    "latency_p95_ms": 3400,
    "cost_per_task_usd": 0.045,
    "memory_recall_accuracy": 0.85,
    "memory_integration_score": 0.73,
    "context_utilization": 0.81,
    "self_correction_rate": 0.65,
    "plan_adherence": 0.88
  },
  "per_task_results": [
    {
      "task_id": "task-001",
      "task_category": "code-generation",
      "profile_archetype": "analytical",
      "passed": true,
      "score": 0.95,
      "latency_ms": 1100,
      "tokens_used": 2340,
      "memory_hits": 3,
      "stochastic_variance": 0.02,
      "attempts": [
        { "attempt": 1, "score": 0.90 },
        { "attempt": 2, "score": 0.95 },
        { "attempt": 3, "score": 0.95 }
      ]
    }
  ],
  "stochastic_summary": {
    "total_repeated_tasks": 50,
    "repetitions_per_task": 3,
    "mean_variance": 0.034,
    "max_variance": 0.12,
    "tasks_with_high_variance": ["task-017", "task-042"]
  }
}
```

### 2.2 Key Design Decisions

- **Metrics are normalized**: everything is either 0.0–1.0 (rates/percentages) or 1–5 (Likert-style scores). The report generator should know which scale each metric uses and display accordingly (percentage vs. star rating vs. raw number).
- **Per-task results carry stochastic data**: each task stores multiple attempts so variance can be computed.
- **Profile archetypes are tagged per-task**: this allows filtering and grouping in visualizations.
- **Metadata is rich**: version, config, environment, and tags enable powerful filtering and comparison.

---

## 3. The Comparer: Baseline Comparison Engine

### 3.1 What It Does

The comparer takes two or more baseline JSON files and produces a structured diff. This is the foundation for all comparative visualizations.

### 3.2 Comparison Output Structure

```json
{
  "comparison": {
    "baseline": { "run_id": "run-2026-03-01-001", "label": "v1.3.0 Release" },
    "current": { "run_id": "run-2026-03-15-001", "label": "v1.4.2 Nightly" },
    "metric_deltas": {
      "task_completion_rate": { "baseline": 0.82, "current": 0.87, "delta": +0.05, "delta_pct": +6.1, "significance": "significant", "direction": "improved" },
      "hallucination_rate": { "baseline": 0.02, "current": 0.03, "delta": +0.01, "delta_pct": +50.0, "significance": "warning", "direction": "regressed" }
    },
    "regression_flags": ["hallucination_rate"],
    "improvement_flags": ["task_completion_rate", "accuracy", "memory_recall_accuracy"]
  }
}
```

### 3.3 Implementation Approach

The comparer should be implemented as a pure function: `compare(baseline: BenchmarkRun, current: BenchmarkRun) → ComparisonResult`. It should:

1. Align metrics by key name.
2. Compute absolute delta and percentage delta.
3. For metrics where "lower is better" (like hallucination_rate, latency, cost), invert the direction logic.
4. Flag regressions when delta exceeds a configurable threshold (e.g., >5% regression = warning, >10% = critical).
5. Run a simple significance test when stochastic data is available (see Section 7).

### 3.4 Multi-Baseline Comparison

Support comparing N baselines at once for timeline views. The comparer should accept an array of runs and produce a time-series structure:

```json
{
  "timeline": [
    { "run_id": "...", "timestamp": "...", "metrics": { ... } },
    { "run_id": "...", "timestamp": "...", "metrics": { ... } }
  ]
}
```

---

## 4. Spider / Radar Graph Visualization

### 4.1 Purpose

The spider graph is the centerpiece visualization. It provides an at-a-glance shape comparison of agent capabilities across multiple dimensions.

### 4.2 Axes

Recommended default axes for the spider chart (all normalized to 0–100%):

1. **Task Completion** — percentage of tasks passed
2. **Accuracy** — correctness score
3. **Coherence** — quality of reasoning (1–5 → mapped to 0–100)
4. **Relevance** — output relevance (1–5 → mapped to 0–100)
5. **Tool Efficiency** — how well the agent uses available tools
6. **Memory Recall** — ability to retrieve relevant past context
7. **Memory Integration** — ability to weave memory into responses
8. **Self-Correction** — rate of catching and fixing own errors
9. **Plan Adherence** — following multi-step plans
10. **Hallucination Resistance** — inverse of hallucination rate (low hallucination = high resistance)

### 4.3 Normalization Strategy

All axes must be on the same 0–100 scale:

- Metrics already in 0.0–1.0: multiply by 100.
- Metrics on 1–5 Likert scale: map via `(value - 1) / 4 * 100`.
- Inverse metrics (hallucination, latency, cost): use `(1 - normalized_value) * 100` so that "better" always means "farther from center."
- Latency: normalize against a defined maximum acceptable latency (e.g., 10s = 0%, 0s = 100%).

### 4.4 Spider Graph: Single Run View

Display a single filled polygon on the radar chart. Use semi-transparent fill so the shape is visible. Show the actual value on hover for each axis.

### 4.5 Spider Graph: Comparison View (Difference Spider)

Overlay two polygons (baseline in grey/blue, current in green/red). The visual diff immediately shows where the agent improved or regressed. Additionally, show a "difference spider" — a separate radar chart where each axis shows the delta between runs, with a center line at zero. Positive deltas extend outward (green), negative deltas extend inward (red).

### 4.6 Plotly.js Implementation Sketch

```javascript
// Single spider
const trace = {
  type: 'scatterpolar',
  r: [87, 92, 85, 90, 78, 85, 73, 65, 88, 97],
  theta: ['Completion', 'Accuracy', 'Coherence', 'Relevance', 'Tool Efficiency',
          'Memory Recall', 'Memory Integration', 'Self-Correction', 'Plan Adherence', 'Hallucination Resistance'],
  fill: 'toself',
  name: 'v1.4.2',
  fillcolor: 'rgba(44, 160, 101, 0.3)',
  line: { color: 'rgb(44, 160, 101)' }
};

// Comparison: add a second trace with the baseline
const baselineTrace = {
  type: 'scatterpolar',
  r: [82, 89, 83, 88, 75, 80, 70, 60, 85, 98],
  theta: /* same labels */,
  fill: 'toself',
  name: 'v1.3.0 (baseline)',
  fillcolor: 'rgba(99, 110, 250, 0.2)',
  line: { color: 'rgb(99, 110, 250)', dash: 'dash' }
};

Plotly.newPlot('spider', [baselineTrace, trace], {
  polar: { radialaxis: { visible: true, range: [0, 100] } },
  showlegend: true
});
```

### 4.7 Spider Graph Interactivity

- Hover shows exact value + delta from baseline.
- Click on an axis label to drill down into per-task results for that metric.
- Toggle button to switch between absolute values and delta view.
- Dropdown to select which baseline to compare against.

---

## 5. Timeline with Regression Detection

### 5.1 Purpose

Track how each metric evolves across runs over time. Detect regressions automatically.

### 5.2 Visualization: Multi-Metric Timeline

Use Plotly line charts with one line per metric. Group related metrics (e.g., all memory metrics together, all quality metrics together). Each chart should have:

- X-axis: timestamp or run label
- Y-axis: metric value (normalized)
- Confidence band: if stochastic data is available, show ±1 standard deviation as a shaded band around the line
- Regression markers: red dots or vertical lines where a regression was detected
- Annotation: on hover, show the full run metadata (version, config, etc.)

### 5.3 Regression Detection Algorithms

Implement multiple detection strategies, configurable per metric:

**Threshold-based**: Flag if metric drops by more than X% from the previous run or from a pinned baseline. Simple and predictable.

**Moving average**: Compare current value against the trailing N-run moving average. Flag if current value is more than K standard deviations below the moving average. This smooths out noise from stochastic variance.

**Linear regression trend**: Fit a simple linear regression over the last N runs. If the slope is negative and statistically significant (p < 0.05), flag a regression trend. This catches slow, steady degradation that threshold-based detection misses.

**Change point detection**: For more sophistication, use CUSUM (cumulative sum) or Bayesian online change point detection. These detect when the underlying distribution of a metric has shifted. This is particularly useful for stochastic evaluations.

### 5.4 Implementation

```javascript
// Simple moving average regression detection
function detectRegression(values, windowSize = 5, threshold = 2.0) {
  const recent = values.slice(-windowSize);
  const mean = recent.reduce((a, b) => a + b) / recent.length;
  const std = Math.sqrt(recent.map(v => (v - mean) ** 2).reduce((a, b) => a + b) / recent.length);
  const current = values[values.length - 1];
  return {
    isRegression: current < mean - threshold * std,
    zScore: (current - mean) / std,
    mean, std, current
  };
}
```

### 5.5 Regression Report Card

At the top of the timeline section, show a summary card: total regressions detected, most critical regressions (sorted by severity), and the metrics that have been most stable.

---

## 6. Profile Archetypes

### 6.1 Concept

Profile archetypes represent distinct behavioral patterns the agent exhibits when handling different types of tasks. Instead of treating all tasks as homogeneous, archetypes let you evaluate the agent's performance across different "modes of operation."

### 6.2 Recommended Archetypes

Define these as categories that each task is tagged with:

| Archetype | Description | Key Metrics to Watch |
|---|---|---|
| **Analytical** | Tasks requiring data analysis, reasoning, computation | Accuracy, Plan Adherence, Self-Correction |
| **Creative** | Tasks requiring generation, ideation, open-ended responses | Coherence, Relevance, Hallucination Resistance |
| **Conversational** | Multi-turn dialogue, context retention, personality consistency | Memory Recall, Memory Integration, Coherence |
| **Executor** | Tool-use-heavy tasks, API calls, structured outputs | Tool Efficiency, Task Completion, Plan Adherence |
| **Retriever** | Tasks that depend heavily on memory or knowledge retrieval | Memory Recall, Accuracy, Context Utilization |
| **Adversarial** | Edge cases, tricky prompts, attempts to confuse the agent | Hallucination Resistance, Self-Correction, Accuracy |

### 6.3 Archetype-Specific Spider Graphs

Generate a separate spider chart per archetype, showing only the metrics most relevant to that archetype (or all metrics, with the key ones highlighted). This reveals whether the agent is strong at analytical tasks but weak at creative ones, for example.

### 6.4 Archetype Progression

Track each archetype's aggregate score over time. This produces a "progression matrix" — a heatmap where rows are archetypes, columns are runs, and cells are colored by aggregate score. This immediately shows which archetype is improving or degrading.

### 6.5 Archetype Classification (Optional LLM Enhancement)

If tasks are not pre-tagged, use an LLM to classify each task into an archetype based on the task description. This can be done as a preprocessing step:

```
Prompt: "Given this task description, classify it into one of these archetypes: [Analytical, Creative, Conversational, Executor, Retriever, Adversarial]. Respond with just the archetype name."
```

This is a deterministic-enough use of an LLM (classification into fixed categories) that it won't introduce meaningful noise.

---

## 7. Stochastic + Memory Evaluation

### 7.1 The Problem

LLMs are non-deterministic. The same prompt can produce different outputs across runs, even with the same temperature setting. This means a single benchmark run is a sample from a distribution, not a fixed point. On top of this, memory-enabled agents have an additional source of variance: the memory state affects outputs, and the memory itself evolves.

### 7.2 Stochastic Evaluation Strategy

**Repeated trials**: Run each task N times (recommend N=3 minimum, N=5 for critical metrics, N=10+ for publishable results). Store all attempts in the per-task results.

**Per-task variance**: For each task, compute mean score and standard deviation across attempts. Tasks with high variance are "unstable" — the agent's behavior on these tasks is unpredictable.

**Aggregate confidence intervals**: For each metric, compute the mean across all tasks and a 95% confidence interval using the task-level variances. Report metrics as "87% ± 3%" rather than just "87%."

**Variance budget**: Define an acceptable variance threshold per metric. If the observed variance exceeds the budget, flag the metric as "unstable" regardless of its mean value. An agent that scores 90% ± 15% is less reliable than one scoring 85% ± 2%.

### 7.3 Memory-Specific Evaluation

Memory introduces a stateful dimension. Evaluate it in two modes:

**Cold start (no memory)**: Run tasks with memory cleared. This measures raw capability without any learned context. This is your deterministic baseline — closer to a pure function.

**Warm start (with memory)**: Run tasks after the agent has accumulated memory from previous interactions. This measures how well the agent leverages stored context.

**Memory delta**: The difference between warm and cold performance. Positive delta = memory is helping. Negative delta = memory is hurting (e.g., retrieving irrelevant context, confusing the agent).

**Memory consistency**: Run the same task sequence multiple times with memory enabled. Measure how consistent the agent's memory-dependent behavior is across runs. High variance here means the memory system is unreliable.

**Memory decay test**: Run tasks at increasing intervals after the relevant memory was stored. Measure if recall accuracy degrades over time or context volume.

### 7.4 Combining Stochastic and Memory Dimensions

Create a 2×2 evaluation matrix:

|  | Low Stochastic Variance | High Stochastic Variance |
|---|---|---|
| **Memory Helps** | Ideal: stable + memory adds value | Risky: memory helps but behavior is unpredictable |
| **Memory Hurts** | Investigate: stable but memory is net negative | Critical: unstable and memory makes it worse |

For each task, classify it into one of these quadrants. Visualize this as a scatter plot with variance on the X-axis and memory delta on the Y-axis.

### 7.5 Making It More Deterministic

To reduce chaos while still using LLMs:

- **Temperature 0**: For evaluation runs, set temperature to 0 (or as low as the API allows). This doesn't eliminate variance but significantly reduces it.
- **Seed parameter**: If the API supports seed-based reproducibility, use fixed seeds for evaluation runs.
- **Majority voting**: Run N times, take the majority answer. Report both the majority result and the agreement rate.
- **Structured output**: Force the LLM to respond in structured formats (JSON, specific templates). This constrains the output space and reduces variance.
- **Judge LLM consistency**: If using an LLM-as-judge, measure the judge's own consistency by having it evaluate the same output multiple times. Report inter-rater reliability (Cohen's kappa or similar).

### 7.6 Visualization for Stochastic Data

- **Box plots per task**: Show the distribution of scores across attempts. Outliers are immediately visible.
- **Violin plots per metric**: Show the full distribution shape, not just quartiles.
- **Stability heatmap**: Tasks on Y-axis, metrics on X-axis, cell color = variance. Red = high variance = unstable.
- **Confidence band on timeline**: As mentioned in Section 5, show the ±1σ band on all timeline charts when stochastic data is available.

---

## 8. Progression Tracking

### 8.1 What Is Progression

Progression tracks how the agent's capabilities evolve over time across releases, configuration changes, or training iterations. It answers: "Is the agent getting better?"

### 8.2 Progression Score

Compute a single aggregate "progression score" per run:

```
progression_score = Σ (weight_i × normalized_metric_i) / Σ weight_i
```

Where weights are configurable per metric (e.g., accuracy might be weighted 2x vs. latency). This gives a single number that trends up or down.

### 8.3 Progression Visualization

**Sparkline dashboard**: A grid of small sparklines, one per metric, showing the last N runs. At a glance you see what's trending up, down, or flat.

**Cumulative improvement chart**: Show the cumulative percentage improvement over the initial baseline. Each metric is a line. If all lines trend upward, the agent is universally improving. Diverging lines reveal trade-offs.

**Milestone markers**: On the timeline, mark significant events — version releases, config changes, model swaps. This creates a visual correlation between changes and metric movements.

### 8.4 Progression Alerts

Configure alerting rules:

- **Regression alert**: Any metric drops below its N-run moving average by more than K standard deviations.
- **Plateau alert**: A metric hasn't improved by more than X% in the last N runs. The agent may have hit a ceiling.
- **Trade-off alert**: Two metrics are moving in opposite directions (e.g., accuracy up but latency up). Flag the trade-off.

---

## 9. Report Layout and Components

### 9.1 Recommended Report Sections

The generated HTML report should contain these sections, in order:

1. **Header**: Agent version, run timestamp, configuration summary, tags.
2. **Executive Summary**: A 3-sentence LLM-generated narrative summarizing the run (optional, uses Anthropic API).
3. **Score Card**: Grid of key metrics with traffic-light indicators (green/yellow/red vs. baseline).
4. **Spider Graph**: The main radar chart, with comparison overlay toggle.
5. **Difference Spider**: Delta view against selected baseline.
6. **Archetype Breakdown**: Per-archetype spider charts or grouped bar charts.
7. **Timeline**: Multi-metric line charts with regression markers.
8. **Stochastic Analysis**: Box plots, variance heatmap, confidence intervals.
9. **Memory Evaluation**: Cold vs. warm comparison, memory delta charts, the 2×2 scatter plot.
10. **Progression Dashboard**: Sparklines, cumulative improvement, milestone timeline.
11. **Regression Report**: List of flagged regressions with severity, affected tasks, and recommended actions.
12. **Raw Data**: Collapsible section with the full JSON for reference.

### 9.2 Interactivity Features

- **Baseline selector**: Dropdown to pick which past run to compare against.
- **Metric filter**: Checkboxes to show/hide specific metrics across all charts.
- **Archetype filter**: Toggle which archetypes are included in aggregate calculations.
- **Time range selector**: Slider or date picker to zoom the timeline.
- **Export**: Button to download the current view as PNG or PDF.
- **Dark/light mode**: Respect system preference via CSS `prefers-color-scheme`.

### 9.3 Responsive Design

Use CSS grid or flexbox so the report renders well on both wide monitors and tablets. Spider charts should have a minimum size of 400×400px. Timelines should be full-width.

---

## 10. Implementation Roadmap

### Phase 1: Foundation
- Define the JSON baseline schema (Section 2).
- Build the baseline parser and normalizer.
- Implement the basic comparer (Section 3).
- Create the single-run spider chart (Section 4.4).

### Phase 2: Comparison and Timeline
- Implement the comparison spider and difference spider (Section 4.5).
- Build the timeline chart with basic threshold regression detection (Section 5).
- Add the score card with traffic-light indicators.

### Phase 3: Stochastic and Memory
- Extend the JSON schema for per-task attempts and stochastic summary.
- Implement stochastic analysis visualizations: box plots, variance heatmap (Section 7.6).
- Add memory evaluation: cold vs. warm comparison, memory delta (Section 7.3).
- Add confidence bands to timeline charts.

### Phase 4: Archetypes and Progression
- Define and tag tasks with archetypes (Section 6).
- Build archetype-specific views and progression matrix.
- Implement progression tracking: sparklines, cumulative improvement (Section 8).
- Add regression detection algorithms beyond simple thresholds (Section 5.3).

### Phase 5: Polish and LLM Enhancement
- Add LLM-generated executive summary (optional).
- Implement archetype auto-classification via LLM (optional).
- Add interactivity features: filters, selectors, export (Section 9.2).
- Dark/light mode, responsive layout.
- Integration with CI pipeline for automatic report generation.

---

## 11. Technical Notes

### 11.1 Generating Reports from .NET

The recommended approach is to have your .NET benchmark runner output JSON baselines, then use a separate Node.js script (or even a simple .NET template engine) to generate the HTML report. The Node.js approach is simpler because Plotly.js is a JavaScript library and you can use template literals to embed the data directly:

```javascript
const fs = require('fs');
const baseline = JSON.parse(fs.readFileSync('baseline.json'));
const current = JSON.parse(fs.readFileSync('current.json'));
const comparison = compare(baseline, current);

const html = `<!DOCTYPE html>
<html>
<head>
  <script src="https://cdn.plot.ly/plotly-latest.min.js"></script>
</head>
<body>
  <div id="spider"></div>
  <script>
    const data = ${JSON.stringify(comparison)};
    // ... Plotly rendering code
  </script>
</body>
</html>`;

fs.writeFileSync('report.html', html);
```

Alternatively, from .NET you can use a Razor template or simply string interpolation to produce the HTML with embedded JSON data and Plotly.js CDN reference.

### 11.2 CI Integration

The report generator should be runnable as a CLI command:

```bash
dotnet run --project BenchmarkReporter -- --current results/latest.json --baseline results/release-v1.3.0.json --output report.html
```

Or with Node.js:

```bash
node generate-report.js --current results/latest.json --baseline results/release-v1.3.0.json --output report.html
```

In CI, upload the HTML report as a build artifact. If regressions are detected, fail the build or post a warning comment.

### 11.3 Keeping It Deterministic

The report generation itself is fully deterministic — the same inputs always produce the same output. The only non-deterministic element is the optional LLM-generated narrative summary. If determinism is required, either skip the LLM summary or cache it with the run data. The stochastic evaluation of the agent is non-deterministic by nature, but the statistical analysis of that variance is deterministic: given the same set of repeated trials, the computed mean, variance, and confidence intervals are always the same.

---

## 12. Summary of All Visualization Components

| Component | Chart Type | Library | Data Source | Interactive? |
|---|---|---|---|---|
| Spider (single run) | Radar / Scatterpolar | Plotly.js | Single baseline JSON | Hover values |
| Spider (comparison) | Overlaid radar | Plotly.js | Two baseline JSONs | Toggle, hover |
| Difference spider | Radar with +/- | Plotly.js | Comparer output | Hover deltas |
| Score card | Grid with indicators | Custom HTML/CSS | Comparer output | Click to drill down |
| Timeline | Line chart with bands | Plotly.js | Array of baselines | Zoom, hover, filter |
| Regression markers | Annotated timeline | Plotly.js | Regression detector output | Click for details |
| Archetype spiders | Small multiple radars | Plotly.js | Per-archetype aggregates | Filter, hover |
| Archetype progression | Heatmap | Plotly.js | Archetype × run matrix | Hover values |
| Box plots (stochastic) | Box plot | Plotly.js | Per-task attempt data | Hover quartiles |
| Variance heatmap | Heatmap | Plotly.js | Task × metric variance | Hover, click |
| Memory comparison | Grouped bar chart | Plotly.js | Cold vs. warm results | Toggle |
| Memory scatter | Scatter plot | Plotly.js | Variance × memory delta | Hover task IDs |
| Sparkline dashboard | Small line charts | Custom SVG or Plotly | Last N runs per metric | Hover |
| Cumulative improvement | Multi-line chart | Plotly.js | Progressive deltas | Zoom, filter |

---

## 13. Data Scaling Reference

For consistent display across all components:

| Metric Type | Raw Range | Display Range | Normalization |
|---|---|---|---|
| Rate/percentage | 0.0 – 1.0 | 0% – 100% | × 100 |
| Likert score | 1 – 5 | 0% – 100% | (v - 1) / 4 × 100 |
| Inverse metric (hallucination, error) | 0.0 – 1.0 | 0% – 100% | (1 - v) × 100 |
| Latency (ms) | 0 – max_acceptable | 0% – 100% | (1 - v/max) × 100, capped at 0 |
| Cost (USD) | 0 – max_acceptable | 0% – 100% | (1 - v/max) × 100, capped at 0 |
| Count (tokens, attempts) | 0 – N | Raw display | No normalization, display as-is |

---

End of document. All sections are designed to be modular — implement them incrementally following the Phase 1–5 roadmap, or cherry-pick individual components as needed.
