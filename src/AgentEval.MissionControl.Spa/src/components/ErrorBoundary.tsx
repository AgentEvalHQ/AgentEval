import { Component } from "react";
import type { ErrorInfo, ReactNode } from "react";

// Plan-08 MC1.6.3 (Opus review F14): top-level error boundary so a render-time
// throw in any page doesn't kill the AppShell. Mounted around <Outlet/> in
// AppShell.tsx.
//
// Note: error boundaries in React 19 still need to be class components — there
// is no hook-based equivalent yet. This is the React-recommended shape.

interface ErrorBoundaryProps {
  children: ReactNode;
}

interface ErrorBoundaryState {
  error: Error | null;
}

export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  constructor(props: ErrorBoundaryProps) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo): void {
    // In dev this surfaces in the console; in production we'd ship to a
    // server-side telemetry sink (deferred to plan-08 follow-up).
    // eslint-disable-next-line no-console
    console.error("Mission Control SPA error boundary caught:", error, errorInfo);
  }

  reset = () => this.setState({ error: null });

  override render(): ReactNode {
    if (this.state.error) {
      return (
        <div className="p-6 max-w-2xl">
          <h2 className="text-xl font-bold text-red-700 mb-2">
            Something went wrong
          </h2>
          <p className="text-sm text-slate-700 mb-4">
            The page failed to render. The error has been logged to the browser
            console. You can try reloading or navigating away.
          </p>
          <pre className="text-xs bg-red-50 border border-red-200 rounded p-3 overflow-x-auto whitespace-pre-wrap">
            {this.state.error.message}
          </pre>
          <button
            onClick={this.reset}
            className="mt-4 px-4 py-2 rounded-md bg-accent-700 text-white text-sm font-medium hover:bg-accent-500"
          >
            Try again
          </button>
        </div>
      );
    }
    return this.props.children;
  }
}
