import { Route, Routes } from "react-router-dom";
import { AppShell } from "@/components/AppShell";
import { DashboardPage } from "@/pages/DashboardPage";
import { SubjectDetailPage } from "@/pages/SubjectDetailPage";
import { RunsListPage } from "@/pages/RunsListPage";
import { RunDetailPage } from "@/pages/RunDetailPage";
import { ComplianceListPage } from "@/pages/ComplianceListPage";
import { ComplianceMatrixPage } from "@/pages/ComplianceMatrixPage";
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
        <Route path="compliance" element={<ComplianceListPage />} />
        <Route path="compliance/:regulation" element={<ComplianceMatrixPage />} />
        <Route path="evaluators" element={<EvaluatorsPage />} />
        <Route path="evaluators/:key" element={<EvaluatorDetailPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
