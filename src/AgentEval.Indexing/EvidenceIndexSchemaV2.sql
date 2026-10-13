CREATE TABLE root_bindings (
    root_id TEXT PRIMARY KEY,
    directory TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('aef', 'legacy', 'observation'))
) STRICT;
