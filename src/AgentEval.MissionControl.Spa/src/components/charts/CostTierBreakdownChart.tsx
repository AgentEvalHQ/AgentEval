import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { formatCost } from "@/lib/format";

// Plan-08 Wave 6 (MC1.6.6): cost-by-tier stacked bar.
// Driven by Query.runCostBreakdown(runId) which returns
// { totalCost, byTier: {trivial, low, medium, high}, unknownKeyCost,
//   filteredOut: [] }. Plan-07 §10.

interface CostTierBreakdownChartProps {
  byTier: {
    trivial: number;
    low: number;
    medium: number;
    high: number;
  };
  unknown: number;
  height?: number;
}

const TIER_COLORS: Record<string, string> = {
  trivial: "#cbd5e1", // slate-300
  low:     "#86efac", // green-300
  medium:  "#fcd34d", // amber-300
  high:    "#fca5a5", // red-300
  unknown: "#e2e8f0", // slate-200
};

export function CostTierBreakdownChart({
  byTier,
  unknown,
  height = 80,
}: CostTierBreakdownChartProps) {
  const total = byTier.trivial + byTier.low + byTier.medium + byTier.high + unknown;
  if (total === 0) {
    return (
      <p className="text-sm text-slate-500 italic py-3 text-center">
        No measurable cost recorded for this run.
      </p>
    );
  }

  // Single horizontal stacked bar. Recharts wants an array of "rows" with
  // each tier as its own dataKey on the same row.
  const data = [
    {
      name: "cost",
      trivial: byTier.trivial,
      low: byTier.low,
      medium: byTier.medium,
      high: byTier.high,
      unknown,
    },
  ];

  return (
    <ResponsiveContainer width="100%" height={height}>
      <BarChart
        layout="vertical"
        data={data}
        margin={{ top: 4, right: 8, bottom: 4, left: 0 }}
      >
        <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" horizontal={false} />
        <XAxis
          type="number"
          tickFormatter={(v: number) => formatCost(v)}
          stroke="#94a3b8"
          fontSize={11}
        />
        <YAxis type="category" dataKey="name" hide />
        <Tooltip
          contentStyle={{
            background: "white",
            border: "1px solid #e2e8f0",
            borderRadius: "0.375rem",
            fontSize: "0.75rem",
          }}
          formatter={(value: number, name: string) => [formatCost(value), name]}
        />
        <Legend wrapperStyle={{ fontSize: 11 }} />
        <Bar dataKey="trivial" stackId="cost" fill={TIER_COLORS.trivial} name="trivial" />
        <Bar dataKey="low"     stackId="cost" fill={TIER_COLORS.low}     name="low" />
        <Bar dataKey="medium"  stackId="cost" fill={TIER_COLORS.medium}  name="medium" />
        <Bar dataKey="high"    stackId="cost" fill={TIER_COLORS.high}    name="high" />
        <Bar dataKey="unknown" stackId="cost" fill={TIER_COLORS.unknown} name="unknown" />
      </BarChart>
    </ResponsiveContainer>
  );
}
