// Plan-08 Wave 4 (MC1.6.7): the killer-feature compliance matrix.
//
// CSS Grid implementation — handles up to ~2,500 cells comfortably, which
// covers any realistic regulation × subject deployment. Visx HeatmapCircle
// upgrade is documented in docs/missioncontrol/charting.md as a polish
// move when matrices get genuinely dense (1000+ subjects).

type CellStatus = "pass" | "warn" | "fail" | "no-data";

export interface MatrixSubject {
  name: string;
  kind: "AGENT" | "WORKFLOW";
}

export interface MatrixControl {
  id: string;
  title: string;
}

export interface MatrixCell {
  subjectName: string;
  controlId: string;
  status: CellStatus;
  passRate: number;
  lastEvidenceAt: string;
  lastEvidenceRunId: string;
  regressedFromBaseline: boolean | null;
}

interface ComplianceMatrixProps {
  subjects: MatrixSubject[];
  controls: MatrixControl[];
  cells: MatrixCell[];
  onCellClick?: (subject: string, controlId: string) => void;
}

const CELL_TONE: Record<CellStatus, string> = {
  pass:      "bg-green-200 hover:bg-green-300",
  warn:      "bg-amber-200 hover:bg-amber-300",
  fail:      "bg-red-200 hover:bg-red-300",
  "no-data": "bg-slate-100 hover:bg-slate-200",
};

const CELL_LABEL: Record<CellStatus, string> = {
  pass:      "✓",
  warn:      "!",
  fail:      "✗",
  "no-data": "·",
};

export function ComplianceMatrix({
  subjects,
  controls,
  cells,
  onCellClick,
}: ComplianceMatrixProps) {
  if (subjects.length === 0 || controls.length === 0) {
    return (
      <p className="text-sm text-slate-500 italic py-8 text-center">
        No compliance data for this regulation yet.
      </p>
    );
  }

  // Index cells for O(1) lookup.
  const cellIndex = new Map<string, MatrixCell>();
  for (const c of cells) {
    cellIndex.set(`${c.subjectName}::${c.controlId}`, c);
  }

  return (
    <div className="overflow-x-auto">
      <div
        className="grid"
        style={{
          gridTemplateColumns: `12rem repeat(${controls.length}, minmax(2rem, 1fr))`,
        }}
      >
        {/* Header row */}
        <div className="border-b border-slate-300 bg-slate-50 sticky left-0 z-10" />
        {controls.map((c) => (
          <div
            key={c.id}
            className="border-b border-slate-300 bg-slate-50 px-1 py-2 text-[10px] text-slate-700 font-medium"
            style={{
              writingMode: "vertical-lr",
              transform: "rotate(180deg)",
              minHeight: "10rem",
            }}
            title={c.title}
          >
            {c.id}
          </div>
        ))}

        {/* Body rows */}
        {subjects.map((s) => (
          <Row
            key={`${s.kind}-${s.name}`}
            subject={s}
            controls={controls}
            cellIndex={cellIndex}
            onCellClick={onCellClick}
          />
        ))}
      </div>

      <Legend />
    </div>
  );
}

function Row({
  subject,
  controls,
  cellIndex,
  onCellClick,
}: {
  subject: MatrixSubject;
  controls: MatrixControl[];
  cellIndex: Map<string, MatrixCell>;
  onCellClick?: (subject: string, controlId: string) => void;
}) {
  return (
    <>
      <div className="px-3 py-2 text-sm border-r border-slate-200 sticky left-0 bg-white z-10">
        <span className="text-[10px] uppercase text-slate-400 mr-1">
          {subject.kind}
        </span>
        <span className="font-medium text-slate-900">{subject.name}</span>
      </div>
      {controls.map((c) => {
        const cell = cellIndex.get(`${subject.name}::${c.id}`);
        const status: CellStatus = cell?.status ?? "no-data";
        return (
          <button
            key={`${subject.name}-${c.id}`}
            onClick={() => onCellClick?.(subject.name, c.id)}
            className={`${CELL_TONE[status]} border border-slate-50 grid place-items-center text-xs font-medium text-slate-700 transition`}
            title={
              cell
                ? `${subject.name} · ${c.id}\nStatus: ${cell.status}\nPass rate: ${(cell.passRate * 100).toFixed(0)}%\nLast evidence: ${cell.lastEvidenceAt}`
                : `${subject.name} · ${c.id}\nNo evidence`
            }
          >
            {CELL_LABEL[status]}
          </button>
        );
      })}
    </>
  );
}

function Legend() {
  return (
    <div className="flex items-center gap-4 mt-3 text-xs text-slate-600">
      <span className="font-medium">Legend:</span>
      <LegendDot tone="bg-green-200" label="pass" />
      <LegendDot tone="bg-amber-200" label="warn" />
      <LegendDot tone="bg-red-200" label="fail" />
      <LegendDot tone="bg-slate-100" label="no data" />
    </div>
  );
}

function LegendDot({ tone, label }: { tone: string; label: string }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className={`size-3 rounded-sm ${tone}`} />
      {label}
    </span>
  );
}
