-- Numbering schemes that count per prefix (e.g. each area of each project from 001) and whose fields are pick lists.
ALTER TABLE numbering_schemes ADD COLUMN fields jsonb NOT NULL DEFAULT '[]';
ALTER TABLE numbering_schemes ADD COLUMN start_value bigint NOT NULL DEFAULT 1;
ALTER TABLE numbering_schemes ADD COLUMN description text;

-- One counter per scheme and scope (the varying text before the counter; '' when the scheme has one counter, which
-- stays in numbering_schemes.next_value as before).
CREATE TABLE numbering_counters (
    scheme_id  text NOT NULL REFERENCES numbering_schemes (id),
    scope      text NOT NULL,
    next_value bigint NOT NULL,
    PRIMARY KEY (scheme_id, scope)
);

-- The same counter value now appears once per scope.
ALTER TABLE number_reservations ADD COLUMN scope text NOT NULL DEFAULT '';
ALTER TABLE number_reservations DROP CONSTRAINT number_reservations_pkey;
ALTER TABLE number_reservations ADD PRIMARY KEY (scheme_id, scope, value);

-- The universal part numbers start at 100000 so they always have six digits.
UPDATE numbering_schemes SET start_value = 100000 WHERE id = 'items';
