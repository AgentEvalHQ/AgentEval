import type { ReactNode } from "react";

// Plan-08 MC1.6.3 (Opus review F14): unified loading / error / empty state
// rendering for TanStack Query results.
//
// Saves every page from re-implementing the `isPending / isError / data &&
// data.length === 0` triplet. Pages pass the query state + render function
// for the success branch; this component handles the rest.

interface DataStateProps<T> {
  isPending: boolean;
  isError: boolean;
  error?: unknown;
  data: T | undefined;
  isEmpty?: (data: T) => boolean;
  loadingMessage?: string;
  emptyMessage?: ReactNode;
  errorPrefix?: string;
  children: (data: T) => ReactNode;
}

export function DataState<T>({
  isPending,
  isError,
  error,
  data,
  isEmpty,
  loadingMessage = "Loading…",
  emptyMessage = "No results.",
  errorPrefix = "Failed to load",
  children,
}: DataStateProps<T>) {
  if (isPending) {
    return <p className="text-sm text-slate-500">{loadingMessage}</p>;
  }
  if (isError) {
    const message =
      error instanceof Error ? error.message : "Unknown error";
    return (
      <p className="text-sm text-red-600">
        {errorPrefix}: {message}
      </p>
    );
  }
  if (data === undefined) {
    // Should be unreachable when isPending and isError are false, but TS
    // narrowing benefits from the guard.
    return null;
  }
  if (isEmpty?.(data)) {
    return <div className="text-sm text-slate-500">{emptyMessage}</div>;
  }
  return <>{children(data)}</>;
}
