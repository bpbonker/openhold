-- Categories, lifecycles, revision schemes and groups live in one configuration document. Saving appends a
-- version, so every change an administrator makes is kept and attributable; the newest version is in force.
CREATE TABLE vault_config (
    version  int PRIMARY KEY,
    saved_by uuid REFERENCES users (id),
    saved_at timestamptz NOT NULL DEFAULT now(),
    comment  text NOT NULL DEFAULT '',
    document jsonb NOT NULL
);

-- A file's category (an id in the configuration document) decides its lifecycle and revision scheme.
ALTER TABLE files ADD COLUMN category_id text;

-- The lifecycle state of each version. A state change is a new version with the same content, so history
-- shows who released what and when. NULL means the lifecycle's first state (versions from before lifecycles).
ALTER TABLE file_versions ADD COLUMN state text;

-- A request to change the state of a set of files that needs sign-off first. Files move together once
-- every approval rule is met, or not at all.
CREATE TABLE change_requests (
    id           uuid PRIMARY KEY,           -- client-generated, so a retried submission can't create two
    to_state     text NOT NULL,
    comment      text NOT NULL DEFAULT '',
    approvals    jsonb NOT NULL,             -- the approval rules that must be met: [{group, required}]
    submitter_may_approve boolean NOT NULL DEFAULT false,
    requested_by uuid NOT NULL REFERENCES users (id),
    site_id      text NOT NULL REFERENCES sites (id),
    requested_at timestamptz NOT NULL DEFAULT now(),
    status       text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'rejected', 'cancelled')),
    resolved_at  timestamptz,
    changeset_id uuid REFERENCES changesets (id)
);

CREATE INDEX change_requests_pending ON change_requests (requested_at) WHERE status = 'pending';

CREATE TABLE change_request_files (
    request_id uuid NOT NULL REFERENCES change_requests (id),
    file_id    uuid NOT NULL REFERENCES files (id),
    version    int NOT NULL,                 -- the version submitted; a newer one means it changed under review
    from_state text NOT NULL,
    PRIMARY KEY (request_id, file_id)
);

CREATE INDEX change_request_files_file ON change_request_files (file_id);

CREATE TABLE approval_decisions (
    request_id uuid NOT NULL REFERENCES change_requests (id),
    user_id    uuid NOT NULL REFERENCES users (id),
    decision   text NOT NULL CHECK (decision IN ('approved', 'rejected')),
    groups     text[] NOT NULL,              -- the groups the person signed for, as they were at the time
    comment    text NOT NULL DEFAULT '',
    decided_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (request_id, user_id)
);
