export interface EvidenceOutcome<T> { state: string; code: string | null; data: T | null }

const messages: Record<string, string> = {
  EMPTY: "No indexed records in this page.",
  NOT_CONFIGURED: "This query capability is not configured on the host.",
  NOT_FOUND: "The selected evidence was not found.",
  INVALID_REQUEST: "The request is not valid for this evidence. Check the selection and UTC view time.",
  STALE_CURSOR: "The index changed. Restart from the first page.",
  RESOURCE_LIMIT: "The query exceeded the host's resource budget.",
  UNAVAILABLE: "Evidence is currently unavailable. Check the host and refresh.",
};

export function EvidenceQueryStatus({ fetching, error, outcome }: {
  fetching: boolean; error: boolean; outcome?: EvidenceOutcome<unknown>;
}) {
  return <p role="status" className="text-sm" aria-live="polite">{fetching ? "Loading evidence…" : error
    ? "Connection failed. Refresh to try again." : outcome?.state === "AVAILABLE" ? "Evidence loaded."
    : outcome ? messages[outcome.state] ?? "Unsupported availability state." : "No response available."}</p>;
}
