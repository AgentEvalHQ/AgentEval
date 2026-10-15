import { lazy } from "react";
import { Route, Routes } from "react-router";
import { AppShell } from "@/components/AppShell";
import { NotFoundPage } from "@/pages/NotFoundPage";

// Load page code on navigation so governance reads do not download every chart.
const DashboardPage = lazy(() => import("@/pages/DashboardPage").then(module => ({ default: module.DashboardPage })));
const SubjectDetailPage = lazy(() => import("@/pages/SubjectDetailPage").then(module => ({ default: module.SubjectDetailPage })));
const RunsListPage = lazy(() => import("@/pages/RunsListPage").then(module => ({ default: module.RunsListPage })));
const RunDetailPage = lazy(() => import("@/pages/RunDetailPage").then(module => ({ default: module.RunDetailPage })));
const ScenarioTreePage = lazy(() => import("@/pages/ScenarioTreePage").then(module => ({ default: module.ScenarioTreePage })));
const TraceWaterfallPage = lazy(() => import("@/pages/TraceWaterfallPage").then(module => ({ default: module.TraceWaterfallPage })));
const ComplianceListPage = lazy(() => import("@/pages/ComplianceListPage").then(module => ({ default: module.ComplianceListPage })));
const ComplianceMatrixPage = lazy(() => import("@/pages/ComplianceMatrixPage").then(module => ({ default: module.ComplianceMatrixPage })));
const EvidenceDetailPage = lazy(() => import("@/pages/EvidenceDetailPage").then(module => ({ default: module.EvidenceDetailPage })));
const EvaluatorsPage = lazy(() => import("@/pages/EvaluatorsPage").then(module => ({ default: module.EvaluatorsPage })));
const EvaluatorDetailPage = lazy(() => import("@/pages/EvaluatorDetailPage").then(module => ({ default: module.EvaluatorDetailPage })));
const RedTeamCampaigns = lazy(() => import("@/pages/RedTeamCampaigns").then(module => ({ default: module.RedTeamCampaigns })));
const GovernancePage = lazy(() => import("@/pages/GovernancePage").then(module => ({ default: module.GovernancePage })));
const RequirementsPage = lazy(() => import("@/pages/RequirementsPage").then(module => ({ default: module.RequirementsPage })));
const CheckpointsPage = lazy(() => import("@/pages/CheckpointsPage").then(module => ({ default: module.CheckpointsPage })));
const GovernanceSourcePage = lazy(() => import("@/pages/GovernanceSourcePage").then(module => ({ default: module.GovernanceSourcePage })));

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
        <Route path="governance" element={<GovernancePage />} />
        <Route path="governance/requirements" element={<RequirementsPage />} />
        <Route path="governance/checkpoints" element={<CheckpointsPage />} />
        <Route path="governance/sources/:sourceId" element={<GovernanceSourcePage />} />
        <Route path="compliance/:regulation" element={<ComplianceMatrixPage />} />
        <Route
          path="compliance/:regulation/:kind/:name/:ts"
          element={<EvidenceDetailPage />}
        />
        <Route path="evaluators" element={<EvaluatorsPage />} />
        <Route path="evaluators/:key" element={<EvaluatorDetailPage />} />
        {/* T3.6 (2026-05-25) — red-team campaigns list view. Detail
            page is deferred to a follow-up. */}
        <Route path="red-team" element={<RedTeamCampaigns />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
