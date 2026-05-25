// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

import { Link, useLocation } from "react-router-dom";
import { Compass, Home, ListChecks, ShieldCheck } from "lucide-react";

// Plan-08 portal-review A23 (T2.2, 2026-05-25): when a user lands on a bad
// URL we echo the captured path back and offer three first-level navigation
// suggestions. Previously the page only offered "Back to dashboard" which
// stranded users who had mistyped a deep link (e.g. /runs/abc/tracee).
export function NotFoundPage() {
  const location = useLocation();
  const badPath = `${location.pathname}${location.search}${location.hash}`;

  return (
    <div className="max-w-2xl mx-auto py-12 space-y-6">
      <header className="space-y-2">
        <h2 className="text-3xl font-bold text-slate-900">404 &mdash; not found</h2>
        <p className="text-sm text-slate-700">
          No page at{" "}
          <code className="bg-slate-100 text-slate-900 font-mono text-xs px-1.5 py-0.5 rounded">
            {badPath || "/"}
          </code>
          .
        </p>
      </header>

      <section className="rounded-lg border border-slate-200 bg-white p-4 space-y-3">
        <h3 className="text-sm font-semibold text-slate-700">Try one of these</h3>
        <ul className="space-y-2">
          <li>
            <Link
              to="/"
              className="inline-flex items-center gap-2 text-sm text-accent-700 hover:text-accent-500"
            >
              <Home size={14} /> Dashboard
            </Link>
          </li>
          <li>
            <Link
              to="/runs"
              className="inline-flex items-center gap-2 text-sm text-accent-700 hover:text-accent-500"
            >
              <ListChecks size={14} /> Runs
            </Link>
          </li>
          <li>
            <Link
              to="/compliance"
              className="inline-flex items-center gap-2 text-sm text-accent-700 hover:text-accent-500"
            >
              <ShieldCheck size={14} /> Compliance
            </Link>
          </li>
        </ul>
      </section>

      <p className="text-xs text-slate-500 inline-flex items-center gap-1.5">
        <Compass size={12} />
        Use the navigation bar above to find what you need.
      </p>
    </div>
  );
}
