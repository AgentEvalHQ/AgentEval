import { NavLink, Outlet } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { LayoutDashboard, ShieldCheck, ListChecks, Activity, History, Swords } from "lucide-react";
import { ErrorBoundary } from "./ErrorBoundary";
import { gqlRequest } from "@/lib/graphql-client";
import { fetchVersion, formatModeLabel } from "@/lib/rest-client";
import { queryKeys } from "@/lib/keys";
import { WorkspaceLandingPage } from "@/pages/WorkspaceLandingPage";

// Plan-08 MC1.6.2: shell layout — top nav + left sidebar.
// Uses Tailwind 4; routes correspond to plan-07 §10.
// Wraps <Outlet/> in <ErrorBoundary/> per Opus review F14 — a render-time
// throw in any page renders the boundary's fallback instead of unmounting
// the whole app.
//
// Plan-08 MC1.10.1: first-run landing. Issues a single Query.workspace at
// shell mount. When `initialized: false`, renders <WorkspaceLandingPage/>
// in the main slot instead of <Outlet/> — so every route gives the user a
// "run agenteval init" message instead of broken empty dashboards.

interface WorkspaceStateResponse {
  workspace: {
    initialized: boolean;
    root: string | null;
    agentEvalVersion: string;
  };
}

const WORKSPACE_STATE_QUERY = /* GraphQL */ `
  query WorkspaceState {
    workspace {
      initialized
      root
      agentEvalVersion
    }
  }
`;

const navItems = [
  { to: "/", label: "Dashboard", icon: LayoutDashboard, end: true },
  { to: "/subjects", label: "Subjects", icon: ListChecks, end: false },
  { to: "/runs", label: "Runs", icon: History, end: false },
  { to: "/compliance", label: "Compliance", icon: ShieldCheck, end: false },
  { to: "/evaluators", label: "Evaluators", icon: Activity, end: false },
  // T3.6 (2026-05-25) — red-team campaigns list view.
  { to: "/red-team", label: "Red-team", icon: Swords, end: false },
];

export function AppShell() {
  const wsQ = useQuery({
    queryKey: queryKeys.workspace.state(),
    queryFn: () => gqlRequest<WorkspaceStateResponse>(WORKSPACE_STATE_QUERY),
    // 30 s staleTime + refetch on window focus: cheap mitigation for the
    // common "ran agenteval init in another terminal, came back to MC tab"
    // flow. Pure refresh-only would force users to hard-reload the page.
    staleTime: 30_000,
    refetchOnWindowFocus: true,
  });

  // While the probe is in flight, render the shell with the normal outlet
  // so existing pages don't flash. Once we have an answer, switch to the
  // landing page if uninitialised. On query error we fall back to the
  // outlet — better to show partial functionality than block on a
  // degraded probe.
  const showLanding = wsQ.data?.workspace.initialized === false;

  return (
    <div className="min-h-screen flex flex-col">
      <Header />
      <div className="flex flex-1">
        <Sidebar disabled={showLanding} />
        <main className="flex-1 p-6 bg-slate-50">
          <ErrorBoundary>
            {showLanding && wsQ.data ? (
              <WorkspaceLandingPage
                root={wsQ.data.workspace.root}
                agentEvalVersion={wsQ.data.workspace.agentEvalVersion}
              />
            ) : (
              <Outlet />
            )}
          </ErrorBoundary>
        </main>
      </div>
    </div>
  );
}

function Header() {
  // portal-review A16: this used to hard-code "Mode A — Local viewer" regardless of what mode the
  // server actually reported. Query the same /api/v1/version endpoint DashboardPage already uses
  // (shared queryKeys.version() cache key, so this doesn't add an extra network round trip on pages
  // that also render DashboardPage) and render the REAL mode. Show nothing until it resolves rather
  // than guess — a wrong label is worse than a brief blank.
  const versionQ = useQuery({
    queryKey: queryKeys.version(),
    queryFn: fetchVersion,
  });

  return (
    <header className="border-b border-slate-200 bg-white px-6 py-3 flex items-center justify-between">
      <div className="flex items-center gap-2">
        <div className="size-8 rounded-md bg-accent-700 grid place-items-center text-white text-sm font-semibold">
          MC
        </div>
        <h1 className="text-lg font-semibold text-slate-900">
          AgentEval Mission Control
        </h1>
      </div>
      <span className="text-xs text-slate-500">
        {versionQ.data ? formatModeLabel(versionQ.data.mode) : ""}
      </span>
    </header>
  );
}

function Sidebar({ disabled = false }: { disabled?: boolean }) {
  return (
    <nav className="w-56 border-r border-slate-200 bg-white py-4">
      <ul className="space-y-1 px-2">
        {navItems.map((item) => (
          <li key={item.to}>
            {disabled ? (
              // Render the same anchor shape but mark it disabled to assistive
              // tech. `aria-disabled` + `tabIndex={-1}` keeps screen-reader
              // semantics ("link, dimmed, disabled") instead of a decorative
              // span; `pointer-events-none` blocks the click without removing
              // the focusable role.
              <a
                role="link"
                aria-disabled="true"
                tabIndex={-1}
                title="Initialise the workspace first"
                className="flex items-center gap-2 rounded-md px-3 py-2 text-sm text-slate-400 cursor-not-allowed pointer-events-none"
              >
                <item.icon size={16} />
                {item.label}
              </a>
            ) : (
              <NavLink
                to={item.to}
                end={item.end}
                className={({ isActive }) =>
                  [
                    "flex items-center gap-2 rounded-md px-3 py-2 text-sm",
                    isActive
                      ? "bg-accent-50 text-accent-700 font-medium"
                      : "text-slate-700 hover:bg-slate-100",
                  ].join(" ")
                }
              >
                <item.icon size={16} />
                {item.label}
              </NavLink>
            )}
          </li>
        ))}
      </ul>
    </nav>
  );
}
