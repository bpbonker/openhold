-- A small PNG preview per version, captured at check-in from the Windows shell (the same image Windows
-- Explorer shows). Stored with the version so it replicates to every site with the rest of the metadata.
ALTER TABLE file_versions ADD COLUMN thumbnail bytea;
