-- Search: substring matches on file names stay fast as the vault grows.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX files_name_trgm ON files USING gin (lower(name) gin_trgm_ops) WHERE deleted_at IS NULL;
