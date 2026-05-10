import { Line, LineChart, ResponsiveContainer, YAxis } from "recharts";

// Plan-08 Wave 2 (MC1.6.4): minimal trend chart for SubjectCard.
// No axes, no tooltip, no legend — just a line. The component is intentionally
// dumb; data shaping happens in the parent.

interface SparklineProps {
  data: { value: number }[];
  width?: number | string;
  height?: number;
  color?: string;
}

export function Sparkline({
  data,
  width = "100%",
  height = 32,
  color = "#3b82f6",
}: SparklineProps) {
  if (data.length === 0) {
    return (
      <div
        className="text-xs text-slate-400 italic"
        style={{ width, height, lineHeight: `${height}px` }}
      >
        no history
      </div>
    );
  }
  return (
    <ResponsiveContainer width={width} height={height}>
      <LineChart data={data} margin={{ top: 2, right: 2, bottom: 2, left: 2 }}>
        {/* Lock the y-axis to the score range so trend differences are visible. */}
        <YAxis hide domain={[0, 1]} />
        <Line
          type="monotone"
          dataKey="value"
          stroke={color}
          strokeWidth={1.5}
          dot={false}
          isAnimationActive={false}
        />
      </LineChart>
    </ResponsiveContainer>
  );
}
