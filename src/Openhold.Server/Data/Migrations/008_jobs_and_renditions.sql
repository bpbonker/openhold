-- Work done after the fact by job workers (Windows machines with AutoCAD/Inventor), e.g. publishing a PDF of
-- a drawing when it's released. Jobs never hold up the change that queued them; a worker claims one at a time.
CREATE TABLE jobs (
    id          bigserial PRIMARY KEY,
    kind        text NOT NULL,               -- e.g. 'pdf'
    file_id     uuid NOT NULL REFERENCES files (id),
    version     int NOT NULL,
    status      text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued', 'running', 'done', 'failed')),
    attempts    int NOT NULL DEFAULT 0,
    last_error  text,
    worker      text,                        -- who is running it (username @ machine)
    queued_by   uuid REFERENCES users (id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    started_at  timestamptz,
    finished_at timestamptz,
    FOREIGN KEY (file_id, version) REFERENCES file_versions (file_id, version)
);

CREATE INDEX jobs_waiting ON jobs (created_at) WHERE status IN ('queued', 'running');
CREATE UNIQUE INDEX jobs_one_open_per_version ON jobs (kind, file_id, version) WHERE status IN ('queued', 'running');

-- Files made from a version, e.g. the stamped PDF of a released drawing. Content lives in the chunk store
-- like any file's, so it replicates the same way.
CREATE TABLE renditions (
    file_id      uuid NOT NULL,
    version      int NOT NULL,
    kind         text NOT NULL,
    manifest     bytea NOT NULL,
    length       bigint NOT NULL,
    content_hash bytea NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (file_id, version, kind),
    FOREIGN KEY (file_id, version) REFERENCES file_versions (file_id, version)
);
