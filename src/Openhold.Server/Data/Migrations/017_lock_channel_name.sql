-- Lock changes are announced on the openhold_locks channel. Vaults created before the product was named Openhold
-- announced them under the earlier name; this brings them in line.
CREATE OR REPLACE FUNCTION notify_lock_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_notify('openhold_locks', COALESCE(NEW.file_id, OLD.file_id)::text);
    RETURN NULL;
END $$;
