import {
  CartesianGrid,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { formatDate } from "@/lib/format";

// Plan-08 Wave 2 (MC1.6.4): score-over-time chart for the subject detail page.
// Recharts is the primary chart library per plan-07 §6.1 (Recharts vs ECharts
// challenge). Plan-07 §10 maps `recommendedVisualization: "timeline"` to this
// component.

export interface TimelinePoint {
  timestamp: string; // ISO 8601
  score: number; // 0..1
  runId?: string;
  verdict?: "PASS" | "WARN" | "FAIL" | "PENDING";
}

interface TimelineChartProps {
  data: TimelinePoint[];
  height?: number;
  threshold?: number;
}

export function TimelineChart({ data, height = 240, threshold }: TimelineChartProps) {
  if (data.length === 0) {
    return (
      <div className="text-sm text-slate-500 italic py-12 text-center">
        No timeline data yet.
      </div>
    );
  }

  // Recharts wants stable, sorted, primitive-typed data.
  const chartData = data
    .slice()
    .sort((a, b) => a.timestamp.localeCompare(b.timestamp))
    .map((p) => ({
      ts: new Date(p.timestamp).getTime(),
      score: p.score,
      verdict: p.verdict ?? null,
      runId: p.runId ?? null,
    }));

  return (
    <ResponsiveContainer width="100%" height={height}>
      <LineChart data={chartData} margin={{ top: 8, right: 16, bottom: 8, left: 0 }}>
        <CartesianGrid stroke="#e2e8f0" strokeDasharray="3 3" />
        <XAxis
          dataKey="ts"
          type="number"
          domain={["dataMin", "dataMax"]}
          tickFormatter={(t) => formatDate(new Date(t))}
          stroke="#94a3b8"
          fontSize={11}
        />
        <YAxis
          domain={[0, 1]}
          tickCount={6}
          stroke="#94a3b8"
          fontSize={11}
        />
        <Tooltip
          contentStyle={{
            background: "white",
            border: "1px solid #e2e8f0",
            borderRadius: "0.375rem",
            fontSize: "0.75rem",
          }}
          labelFormatter={(t) => formatDate(new Date(t as number))}
          formatter={(value: number) => [value.toFixed(3), "Score"]}
        />
        <Line
          type="monotone"
          dataKey="score"
          stroke="#1d4ed8"
          strokeWidth={2}
          dot={{ r: 3, fill: "#1d4ed8" }}
          activeDot={{ r: 5 }}
          isAnimationActive={false}
        />
        {threshold !== undefined && (
          <Line
            type="monotone"
            dataKey={() => threshold}
            stroke="#94a3b8"
            strokeDasharray="4 4"
            dot={false}
            activeDot={false}
            isAnimationActive={false}
            name="threshold"
          />
        )}
      </LineChart>
    </ResponsiveContainer>
  );
}
