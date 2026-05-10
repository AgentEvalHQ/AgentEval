import { NavLink, Outlet } from "react-router-dom";
import { LayoutDashboard, ShieldCheck, ListChecks, Activity } from "lucide-react";
import { ErrorBoundary } from "./ErrorBoundary";

// Plan-08 MC1.6.2: shell layout — top nav + left sidebar.
// Uses Tailwind 4; routes correspond to plan-07 §10.
// Wraps <Outlet/> in <ErrorBoundary/> per Opus review F14 — a render-time
// throw in any page renders the boundary's fallback instead of unmounting
// the whole app.

const navItems = [
  { to: "/", label: "Dashboard", icon: LayoutDashboard, end: true },
  { to: "/subjects", label: "Subjects", icon: ListChecks, end: false },
  { to: "/compliance", label: "Compliance", icon: ShieldCheck, end: false },
  { to: "/evaluators", label: "Evaluators", icon: Activity, end: false },
];

export function AppShell() {
  return (
    <div className="min-h-screen flex flex-col">
      <Header />
      <div className="flex flex-1">
        <Sidebar />
        <main className="flex-1 p-6 bg-slate-50">
          <ErrorBoundary>
            <Outlet />
          </ErrorBoundary>
        </main>
      </div>
    </div>
  );
}

function Header() {
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
        Mode A — Local viewer
      </span>
    </header>
  );
}

function Sidebar() {
  return (
    <nav className="w-56 border-r border-slate-200 bg-white py-4">
      <ul className="space-y-1 px-2">
        {navItems.map((item) => (
          <li key={item.to}>
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
          </li>
        ))}
      </ul>
    </nav>
  );
}
