-- Deleted files go to a trash bin (files.deleted_at, already present) and can be restored with full history.
ALTER TABLE files ADD COLUMN deleted_by uuid REFERENCES users (id);

CREATE INDEX files_deleted ON files (deleted_at) WHERE deleted_at IS NOT NULL;
