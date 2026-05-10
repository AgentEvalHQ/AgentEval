import {
  PolarAngleAxis,
  PolarGrid,
  PolarRadiusAxis,
  Radar,
  RadarChart as RechartsRadar,
  ResponsiveContainer,
  Tooltip,
} from "recharts";

// Plan-08 Wave 2 (MC1.6.4): per-dimension radar chart for evaluators with a
// `recommendedVisualization: "radar"` (composites of N sub-LLM-judges, e.g.
// `tool_call_accuracy` rolling up 5 sub-judges).
//
// Renamed `RadarProfileChart` to avoid collision with Recharts' own
// `RadarChart` import name.

export interface RadarPoint {
  dimension: string;
  score: number; // 0..1
}

interface RadarProfileChartProps {
  data: RadarPoint[];
  height?: number;
}

export function RadarProfileChart({ data, height = 280 }: RadarProfileChartProps) {
  if (data.length < 3) {
    return (
      <div className="text-sm text-slate-500 italic py-12 text-center">
        Radar charts need at least 3 dimensions.
      </div>
    );
  }
  return (
    <ResponsiveContainer width="100%" height={height}>
      <RechartsRadar data={data} margin={{ top: 8, right: 24, bottom: 8, left: 24 }}>
        <PolarGrid stroke="#e2e8f0" />
        <PolarAngleAxis dataKey="dimension" tick={{ fontSize: 11, fill: "#475569" }} />
        <PolarRadiusAxis domain={[0, 1]} tickCount={5} tick={{ fontSize: 10, fill: "#94a3b8" }} />
        <Radar
          dataKey="score"
          stroke="#1d4ed8"
          fill="#1d4ed8"
          fillOpacity={0.25}
          isAnimationActive={false}
        />
        <Tooltip
          contentStyle={{
            background: "white",
            border: "1px solid #e2e8f0",
            borderRadius: "0.375rem",
            fontSize: "0.75rem",
          }}
          formatter={(value: number) => [value.toFixed(3), "Score"]}
        />
      </RechartsRadar>
    </ResponsiveContainer>
  );
}
