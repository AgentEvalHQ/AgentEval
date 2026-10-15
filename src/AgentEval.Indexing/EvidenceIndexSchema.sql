CREATE TABLE index_metadata (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    generation INTEGER NOT NULL CHECK (generation >= 0),
    instance_id TEXT NOT NULL
) STRICT;
CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY,
    script_sha256 TEXT NOT NULL CHECK (length(script_sha256) = 64)
) STRICT;
CREATE TABLE sources (
    source_id TEXT PRIMARY KEY,
    root_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('aef', 'legacy', 'observation')),
    content_digest TEXT NOT NULL,
    indexed_generation INTEGER NOT NULL CHECK (indexed_generation >= 0),
    verification TEXT NOT NULL CHECK (verification IN ('intact', 'unverified', 'invalid')),
    UNIQUE (root_id, relative_path),
    UNIQUE (source_id, kind),
    CHECK (kind = 'aef' OR verification = 'unverified')
) STRICT;
CREATE TABLE evaluation_runs (
    source_id TEXT PRIMARY KEY,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('aef', 'legacy')),
    run_id TEXT NOT NULL,
    run_hash TEXT,
    subject_id TEXT,
    suite_id TEXT,
    suite_definition_digest TEXT,
    started_at TEXT,
    outcome TEXT,
    completeness TEXT NOT NULL,
    FOREIGN KEY (source_id, source_kind) REFERENCES sources(source_id, kind) ON DELETE CASCADE
) STRICT;
CREATE TABLE evaluation_results (
    source_id TEXT NOT NULL REFERENCES evaluation_runs(source_id) ON DELETE CASCADE,
    result_id TEXT NOT NULL,
    case_id TEXT,
    trial INTEGER CHECK (trial >= 0),
    outcome TEXT,
    PRIMARY KEY (source_id, result_id)
) STRICT;
CREATE TABLE metric_values (
    source_id TEXT NOT NULL,
    result_id TEXT NOT NULL,
    metric_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    score REAL,
    score_text TEXT,
    passed INTEGER CHECK (passed IN (0, 1)),
    provenance TEXT NOT NULL,
    PRIMARY KEY (source_id, result_id, metric_id, ordinal),
    FOREIGN KEY (source_id, result_id) REFERENCES evaluation_results(source_id, result_id) ON DELETE CASCADE,
    CHECK (score IS NULL OR score_text IS NULL)
) STRICT;
CREATE TABLE observation_batches (
    source_id TEXT PRIMARY KEY,
    source_kind TEXT NOT NULL DEFAULT 'observation' CHECK (source_kind = 'observation'),
    receiver_source TEXT NOT NULL,
    capture TEXT NOT NULL CHECK (capture IN ('Off', 'On')),
    content_revision TEXT NOT NULL,
    removals_json TEXT NOT NULL,
    FOREIGN KEY (source_id, source_kind) REFERENCES sources(source_id, kind) ON DELETE CASCADE
) STRICT;
CREATE TABLE observed_spans (
    source_id TEXT NOT NULL REFERENCES observation_batches(source_id) ON DELETE CASCADE,
    report_ordinal INTEGER NOT NULL CHECK (report_ordinal >= 0),
    group_id TEXT NOT NULL,
    grouping_revision TEXT NOT NULL,
    grouping_basis TEXT NOT NULL,
    mapping_revision TEXT NOT NULL,
    trace_id TEXT NOT NULL,
    span_id TEXT NOT NULL,
    parent_span_id TEXT NOT NULL,
    start_nanos TEXT NOT NULL CHECK (length(start_nanos) = 20 AND start_nanos NOT GLOB '*[^0-9]*'),
    end_nanos TEXT NOT NULL CHECK (length(end_nanos) = 20 AND end_nanos NOT GLOB '*[^0-9]*'),
    resource_index INTEGER NOT NULL CHECK (resource_index >= 0),
    scope_index INTEGER NOT NULL CHECK (scope_index >= 0),
    span_index INTEGER NOT NULL CHECK (span_index >= 0),
    PRIMARY KEY (source_id, report_ordinal)
) STRICT;
CREATE TABLE observed_fields (
    source_id TEXT NOT NULL,
    report_ordinal INTEGER NOT NULL,
    field_name TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('Missing', 'Present', 'Invalid', 'Conflict')),
    text_value TEXT,
    integer_value INTEGER,
    sources_json TEXT NOT NULL,
    PRIMARY KEY (source_id, report_ordinal, field_name),
    FOREIGN KEY (source_id, report_ordinal) REFERENCES observed_spans(source_id, report_ordinal) ON DELETE CASCADE,
    CHECK (text_value IS NULL OR integer_value IS NULL),
    CHECK (state = 'Present' OR (text_value IS NULL AND integer_value IS NULL))
) STRICT;
CREATE TABLE catalogue_entities (
    catalogue_digest TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    entity_kind TEXT NOT NULL,
    definition_json TEXT NOT NULL,
    PRIMARY KEY (catalogue_digest, entity_id)
) STRICT;
CREATE INDEX ix_sources_root ON sources(root_id, relative_path, source_id);
CREATE INDEX ix_runs_subject ON evaluation_runs(subject_id, started_at, source_id);
CREATE INDEX ix_runs_suite ON evaluation_runs(suite_id, started_at, source_id);
CREATE INDEX ix_results_case ON evaluation_results(case_id, source_id, result_id);
CREATE INDEX ix_metrics_name ON metric_values(metric_id, source_id, result_id, ordinal);
CREATE INDEX ix_spans_group ON observed_spans(group_id, start_nanos, trace_id, span_id, source_id, report_ordinal);
CREATE INDEX ix_spans_trace ON observed_spans(trace_id, span_id, source_id, report_ordinal);
