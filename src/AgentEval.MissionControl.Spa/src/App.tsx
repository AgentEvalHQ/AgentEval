import { Route, Routes } from "react-router-dom";
import { AppShell } from "@/components/AppShell";
import { DashboardPage } from "@/pages/DashboardPage";
import { ComplianceListPage } from "@/pages/ComplianceListPage";
import { EvaluatorsPage } from "@/pages/EvaluatorsPage";
import { NotFoundPage } from "@/pages/NotFoundPage";

// Plan-08 MC1.6.2: routing skeleton.
// Routes match plan-07 §10 — placeholder pages for each top-level route.
// Components fill in waves 2-9.
export function App() {
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<DashboardPage />} />
        <Route path="subjects" element={<DashboardPage />} />
        <Route path="compliance" element={<ComplianceListPage />} />
        <Route path="evaluators" element={<EvaluatorsPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
