import { Route, Routes } from "react-router-dom";
import { AppShell } from "@/components/AppShell";
import { DashboardPage } from "@/pages/DashboardPage";
import { SubjectDetailPage } from "@/pages/SubjectDetailPage";
import { ComplianceListPage } from "@/pages/ComplianceListPage";
import { EvaluatorsPage } from "@/pages/EvaluatorsPage";
import { NotFoundPage } from "@/pages/NotFoundPage";

// Plan-08 MC1.6.2 + Wave 2: routing.
// Routes match plan-07 §10. Wave 2 adds /subjects/:kind/:name detail.
// Subsequent waves fill in /runs/:runId, /compliance/*, /evaluators/:key.
export function App() {
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<DashboardPage />} />
        <Route path="subjects" element={<DashboardPage />} />
        <Route path="subjects/:kind/:name" element={<SubjectDetailPage />} />
        <Route path="compliance" element={<ComplianceListPage />} />
        <Route path="evaluators" element={<EvaluatorsPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
