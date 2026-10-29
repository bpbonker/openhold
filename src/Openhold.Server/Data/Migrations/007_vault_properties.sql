-- Properties kept in the vault only, because the file can't hold them (e.g. a DWG property with no title-block
-- attribute). Kept apart from the properties read from the file, so a check-in from the CAD program, which
-- sends what's in the file, never drops them. Shown and searched together with the file's own.
ALTER TABLE file_versions ADD COLUMN vault_properties jsonb NOT NULL DEFAULT '{}';
