-- An approved change that can no longer be applied (a file changed or was deleted since it was submitted) is
-- closed as blocked, with the reasons, so its files are free to be submitted again. The approvals stay on record.
ALTER TABLE change_requests DROP CONSTRAINT change_requests_status_check;
ALTER TABLE change_requests ADD CONSTRAINT change_requests_status_check
    CHECK (status IN ('pending', 'approved', 'rejected', 'cancelled', 'blocked'));
ALTER TABLE change_requests ADD COLUMN problems jsonb;
