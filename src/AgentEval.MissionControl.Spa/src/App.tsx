import { Route, Routes } from "react-router-dom";
import { AppShell } from "@/components/AppShell";
import { DashboardPage } from "@/pages/DashboardPage";
import { SubjectDetailPage } from "@/pages/SubjectDetailPage";
import { RunsListPage } from "@/pages/RunsListPage";
import { RunDetailPage } from "@/pages/RunDetailPage";
import { ScenarioTreePage } from "@/pages/ScenarioTreePage";
import { TraceWaterfallPage } from "@/pages/TraceWaterfallPage";
import { ComplianceListPage } from "@/pages/ComplianceListPage";
import { ComplianceMatrixPage } from "@/pages/ComplianceMatrixPage";
import { EvidenceDetailPage } from "@/pages/EvidenceDetailPage";
import { EvaluatorsPage } from "@/pages/EvaluatorsPage";
import { EvaluatorDetailPage } from "@/pages/EvaluatorDetailPage";
import { NotFoundPage } from "@/pages/NotFoundPage";

// Plan-08 MC1.6.2 + Waves 2-5: routing.
// Routes match plan-07 §10. Wave 4 adds /compliance/:regulation. Phase 1
// SPA core navigation is now feature-complete for the shipped backend.
export function App() {
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<DashboardPage />} />
        <Route path="subjects" element={<DashboardPage />} />
        <Route path="subjects/:kind/:name" element={<SubjectDetailPage />} />
        <Route path="runs" element={<RunsListPage />} />
        <Route path="runs/:runId" element={<RunDetailPage />} />
        <Route
          path="runs/:runId/scenarios/:scenarioId"
          element={<ScenarioTreePage />}
        />
        {/* Plan-08 portal-review A3 — stub route added in T2.2 (2026-05-25);
            real horizontal-waterfall implementation landed in T2.3 (same
            day) using recharts with each TraceEvent as a bar whose duration
            is the gap to the next event. See TraceWaterfallPage's header
            comment for the shape rationale. */}
        <Route path="runs/:runId/trace" element={<TraceWaterfallPage />} />
        <Route path="compliance" element={<ComplianceListPage />} />
        <Route path="compliance/:regulation" element={<ComplianceMatrixPage />} />
        <Route
          path="compliance/:regulation/:kind/:name/:ts"
          element={<EvidenceDetailPage />}
        />
        <Route path="evaluators" element={<EvaluatorsPage />} />
        <Route path="evaluators/:key" element={<EvaluatorDetailPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
