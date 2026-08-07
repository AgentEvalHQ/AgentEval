import { Link, useSearchParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { gqlRequest } from "@/lib/graphql-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";

// Plan-08 Wave 5 (MC1.6.10): evaluator registry table.
// Drives off the already-shipped Query.evaluators(category?, costTier?) backed
// by EvaluatorCardRegistry — 59 cards in plan-08 MC1.5.1.

type CostTier = "TRIVIAL" | "LOW" | "MEDIUM" | "HIGH";

interface EvaluatorsResponse {
  evaluators: {
    key: string;
    name: string;
    category: string;
    costTier: CostTier;
    higherIsBetter: boolean;
    description: string;
    recommendedVisualization: string | null;
  }[];
}

const EVALUATORS_QUERY = /* GraphQL */ `
  query EvaluatorRegistry($category: String, $costTier: CostTier) {
    evaluators(category: $category, costTier: $costTier) {
      key
      name
      category
      costTier
      higherIsBetter
      description
      recommendedVisualization
    }
  }
`;

const ALL_TIERS: CostTier[] = ["TRIVIAL", "LOW", "MEDIUM", "HIGH"];

export function EvaluatorsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const tierFilter = searchParams.get("tier") as CostTier | null;
  const categoryFilter = searchParams.get("category");
  const searchTerm = (searchParams.get("q") ?? "").toLowerCase();

  const evaluatorsQ = useQuery({
    queryKey: queryKeys.evaluators.list({
      category: categoryFilter ?? undefined,
      costTier: tierFilter ?? undefined,
    }),
    queryFn: () =>
      gqlRequest<EvaluatorsResponse>(EVALUATORS_QUERY, {
        category: categoryFilter,
        costTier: tierFilter,
      }),
  });

  const setParam = (key: string, value: string | null) => {
    const next = new URLSearchParams(searchParams);
    if (value === null || value === "") next.delete(key);
    else next.set(key, value);
    setSearchParams(next);
  };

  return (
    <div className="space-y-4">
      <header>
        <h2 className="text-2xl font-bold text-slate-900">Evaluators</h2>
        <p className="text-sm text-slate-600">
          Schema-driven registry of all evaluators registered in
          {" "}<code className="bg-slate-100 px-1 rounded">EvaluatorCostMap</code>{" "}
          + their <code className="bg-slate-100 px-1 rounded">EvaluatorCard</code>{" "}
          metadata.
        </p>
      </header>

      <div className="flex flex-wrap gap-3 items-center">
        <input
          type="search"
          placeholder="Filter by name, key, or category…"
          value={searchTerm}
          onChange={(e) => setParam("q", e.target.value || null)}
          className="border border-slate-300 rounded-md px-3 py-1.5 text-sm w-72 focus:outline-none focus:ring-2 focus:ring-accent-500"
        />
        <fieldset className="flex items-center gap-1">
          <span className="text-xs text-slate-500 mr-1">Tier:</span>
          {ALL_TIERS.map((t) => (
            <button
              key={t}
              onClick={() => setParam("tier", tierFilter === t ? null : t)}
              className={`text-xs px-2 py-1 rounded-md border ${
                tierFilter === t
                  ? "bg-accent-700 text-white border-accent-700"
                  : "bg-white text-slate-700 border-slate-300 hover:border-accent-500"
              }`}
            >
              {t.toLowerCase()}
            </button>
          ))}
          {tierFilter && (
            <button
              onClick={() => setParam("tier", null)}
              className="text-xs px-2 py-1 rounded-md text-slate-500 hover:text-slate-700"
            >
              clear
            </button>
          )}
        </fieldset>
      </div>

      <DataState
        isPending={evaluatorsQ.isPending}
        isError={evaluatorsQ.isError}
        error={evaluatorsQ.error}
        data={evaluatorsQ.data}
        loadingMessage="Loading registry…"
        errorPrefix="Failed to load evaluators"
      >
        {(d) => {
          const filtered = searchTerm
            ? d.evaluators.filter(
                (e) =>
                  e.key.toLowerCase().includes(searchTerm) ||
                  e.name.toLowerCase().includes(searchTerm) ||
                  e.category.toLowerCase().includes(searchTerm),
              )
            : d.evaluators;

          return (
            <>
              <p className="text-xs text-slate-500">
                Showing <strong>{filtered.length}</strong> of {d.evaluators.length} evaluators
                {tierFilter && ` · tier=${tierFilter.toLowerCase()}`}
                {categoryFilter && ` · category=${categoryFilter}`}
              </p>

              <div className="rounded-lg border border-slate-200 bg-white overflow-hidden">
                <table className="w-full text-sm">
                  <thead className="bg-slate-50 text-slate-600 text-xs uppercase">
                    <tr>
                      <th className="px-3 py-2 text-left">Key</th>
                      <th className="px-3 py-2 text-left">Name</th>
                      <th className="px-3 py-2 text-left">Category</th>
                      <th className="px-3 py-2 text-left">Tier</th>
                      <th className="px-3 py-2 text-left">Description</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {filtered.map((e) => (
                      <tr key={e.key} className="hover:bg-slate-50">
                        <td className="px-3 py-2">
                          <Link
                            to={`/evaluators/${e.key}`}
                            className="font-mono text-xs text-accent-700 hover:text-accent-500"
                          >
                            {e.key}
                          </Link>
                        </td>
                        <td className="px-3 py-2 text-slate-900">{e.name}</td>
                        <td className="px-3 py-2">
                          <button
                            onClick={() =>
                              setParam(
                                "category",
                                categoryFilter === e.category ? null : e.category,
                              )
                            }
                            className="text-xs text-slate-600 hover:text-accent-700 underline-offset-2 hover:underline"
                          >
                            {e.category}
                          </button>
                        </td>
                        <td className="px-3 py-2">
                          <CostTierPill tier={e.costTier} />
                        </td>
                        <td className="px-3 py-2 text-slate-700 text-xs max-w-md">
                          {e.description.length > 140
                            ? e.description.slice(0, 140) + "…"
                            : e.description}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          );
        }}
      </DataState>
    </div>
  );
}

const TIER_TONE: Record<CostTier, string> = {
  TRIVIAL: "bg-slate-100 text-slate-700",
  LOW:     "bg-green-100 text-green-800",
  MEDIUM:  "bg-amber-100 text-amber-800",
  HIGH:    "bg-red-100 text-red-800",
};

export function CostTierPill({ tier }: { tier: CostTier }) {
  return (
    <span
      className={`inline-flex items-center font-medium text-xs px-2 py-0.5 rounded ${TIER_TONE[tier]}`}
    >
      {tier.toLowerCase()}
    </span>
  );
}
