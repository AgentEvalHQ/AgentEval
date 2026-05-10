import { Route, Routes } from "react-router-dom";
import { AppShell } from "@/components/AppShell";
import { DashboardPage } from "@/pages/DashboardPage";
import { SubjectDetailPage } from "@/pages/SubjectDetailPage";
import { RunsListPage } from "@/pages/RunsListPage";
import { RunDetailPage } from "@/pages/RunDetailPage";
import { ComplianceListPage } from "@/pages/ComplianceListPage";
import { EvaluatorsPage } from "@/pages/EvaluatorsPage";
import { NotFoundPage } from "@/pages/NotFoundPage";

// Plan-08 MC1.6.2 + Waves 2-3: routing.
// Routes match plan-07 §10. Wave 2 added /subjects/:kind/:name; Wave 3 adds
// /runs + /runs/:runId. Subsequent waves fill in /compliance/* + /evaluators/:key.
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
        <Route path="evaluators" element={<EvaluatorsPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
