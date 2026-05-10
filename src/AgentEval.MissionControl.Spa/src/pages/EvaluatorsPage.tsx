// Plan-08 placeholder for MC1.6.10 (EvaluatorRegistry wave).
// Real implementation: TanStack Table over Query.evaluators(category?, costTier?)
// with filters + per-card drill-down to the EvaluatorCardView detail page.
export function EvaluatorsPage() {
  return (
    <div className="space-y-3">
      <h2 className="text-2xl font-bold text-slate-900">Evaluators</h2>
      <p className="text-sm text-slate-600">
        Placeholder — the 59-card registry view lands in plan-08 Wave 5
        (MC1.6.10). The backend resolvers{" "}
        <code className="bg-slate-100 px-1 rounded">Query.evaluators</code> +{" "}
        <code className="bg-slate-100 px-1 rounded">Query.evaluator(key)</code>{" "}
        are already live and tested.
      </p>
    </div>
  );
}
