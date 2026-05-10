import {
  Bar,
  BarChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

// Plan-08 Wave 2 (MC1.6.4): score-distribution histogram. Plan-07 §10 maps
// `recommendedVisualization: "histogram"` to this component.
//
// Input: a list of scores in [0,1]. The component bins them into 10 buckets
// (0.0-0.1, 0.1-0.2, …, 0.9-1.0) and shows the count per bucket.

interface HistogramChartProps {
  scores: number[];
  buckets?: number;
  height?: number;
}

export function HistogramChart({
  scores,
  buckets = 10,
  height = 240,
}: HistogramChartProps) {
  if (scores.length === 0) {
    return (
      <div className="text-sm text-slate-500 italic py-12 text-center">
        No data to histogram.
      </div>
    );
  }

  const data = bin(scores, buckets);

  return (
    <ResponsiveContainer width="100%" height={height}>
      <BarChart data={data} margin={{ top: 8, right: 8, bottom: 8, left: 0 }}>
        <CartesianGrid stroke="#e2e8f0" strokeDasharray="3 3" />
        <XAxis dataKey="label" stroke="#94a3b8" fontSize={11} />
        <YAxis stroke="#94a3b8" fontSize={11} allowDecimals={false} />
        <Tooltip
          contentStyle={{
            background: "white",
            border: "1px solid #e2e8f0",
            borderRadius: "0.375rem",
            fontSize: "0.75rem",
          }}
          formatter={(value: number) => [value, "Runs"]}
        />
        <Bar dataKey="count" fill="#1d4ed8" radius={[2, 2, 0, 0]} />
      </BarChart>
    </ResponsiveContainer>
  );
}

function bin(scores: number[], buckets: number): { label: string; count: number }[] {
  const result = Array.from({ length: buckets }, (_, i) => ({
    label: `${i / buckets}`.slice(0, 3),
    count: 0,
  }));

  for (const score of scores) {
    const clamped = Math.max(0, Math.min(0.999_999_9, score));
    const idx = Math.floor(clamped * buckets);
    if (result[idx]) {
      result[idx].count += 1;
    }
  }
  return result;
}
