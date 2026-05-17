// Plan-08 MC1.6.3 (Opus review F14): timestamp formatting helpers.
//
// Mission Control surfaces timestamps in three places: (a) recent-runs list
// (relative — "12 minutes ago"), (b) run-detail header (absolute — "May 9
// 14:30:22 UTC"), (c) compliance evidence date (audit-friendly — "2026-05-09").
// One helper per shape so the SPA renders consistently.

const dateOnly = new Intl.DateTimeFormat(undefined, {
  year: "numeric",
  month: "short",
  day: "numeric",
});

const dateTime = new Intl.DateTimeFormat(undefined, {
  year: "numeric",
  month: "short",
  day: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  timeZoneName: "short",
});

/** Audit-friendly absolute date — "May 9, 2026". */
export function formatDate(iso: string | Date): string {
  const d = typeof iso === "string" ? new Date(iso) : iso;
  return dateOnly.format(d);
}

/** Run-detail absolute timestamp — "May 9, 2026 14:30:22 UTC". */
export function formatDateTime(iso: string | Date): string {
  const d = typeof iso === "string" ? new Date(iso) : iso;
  return dateTime.format(d);
}

/** Recent-runs relative timestamp — "12 minutes ago" / "in 2 hours". */
export function formatRelative(iso: string | Date, now = new Date()): string {
  const d = typeof iso === "string" ? new Date(iso) : iso;
  const diffMs = d.getTime() - now.getTime();
  const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });

  const abs = Math.abs(diffMs);
  if (abs < 60_000) return rtf.format(Math.round(diffMs / 1000), "second");
  if (abs < 3_600_000) return rtf.format(Math.round(diffMs / 60_000), "minute");
  if (abs < 86_400_000) return rtf.format(Math.round(diffMs / 3_600_000), "hour");
  if (abs < 604_800_000) return rtf.format(Math.round(diffMs / 86_400_000), "day");
  if (abs < 2_592_000_000) return rtf.format(Math.round(diffMs / 604_800_000), "week");
  if (abs < 31_536_000_000) return rtf.format(Math.round(diffMs / 2_592_000_000), "month");
  return rtf.format(Math.round(diffMs / 31_536_000_000), "year");
}

/** Number-formatted score — "0.87" or "—" for null. */
export function formatScore(score: number | null | undefined): string {
  if (score === null || score === undefined) return "—";
  return score.toFixed(2);
}

/** Cost in dollars — "$0.0124". */
export function formatCost(cost: number | null | undefined): string {
  if (cost === null || cost === undefined) return "—";
  if (cost === 0) return "$0";
  if (cost < 0.001) return "<$0.001";
  return `$${cost.toFixed(4).replace(/\.?0+$/, "")}`;
}
