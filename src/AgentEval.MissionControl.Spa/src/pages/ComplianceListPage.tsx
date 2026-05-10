// Plan-08 placeholder for MC1.6.7 (ComplianceMatrix wave).
// Real implementation lands in Wave 4 — Visx HeatmapCircle backed by
// Query.complianceMatrix(regulation).
export function ComplianceListPage() {
  return (
    <div className="space-y-3">
      <h2 className="text-2xl font-bold text-slate-900">Compliance</h2>
      <p className="text-sm text-slate-600">
        Placeholder — the regulation × subject × control matrix view (Visx
        heatmap) lands in plan-08 Wave 4 (MC1.6.7). The backend resolver{" "}
        <code className="bg-slate-100 px-1 rounded">Query.complianceMatrix</code>{" "}
        is already live and tested.
      </p>
    </div>
  );
}
