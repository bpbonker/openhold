-- The item master: one universal part number per part, used by every company, site and ERP. Items are never
-- deleted (numbers are never reused); they become Obsolete instead.
CREATE TABLE items (
    id           uuid PRIMARY KEY,
    number       text NOT NULL,
    description  text NOT NULL,
    unit         text NOT NULL DEFAULT 'each',
    kind         text NOT NULL CHECK (kind IN ('made', 'bought')),
    class_id     text,                         -- an item class in the vault configuration
    state        text NOT NULL DEFAULT 'Draft' CHECK (state IN ('Draft', 'Released', 'Obsolete')),
    attributes   jsonb NOT NULL DEFAULT '{}',
    notes        text,
    created_by   uuid NOT NULL REFERENCES users (id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid REFERENCES users (id),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    released_by  uuid REFERENCES users (id),
    released_at  timestamptz,
    row_version  int NOT NULL DEFAULT 1        -- optimistic concurrency: a save names the version it edited
);

CREATE UNIQUE INDEX items_number_ci ON items (lower(number));
CREATE INDEX items_class ON items (class_id);

-- Approved manufacturer list (AML): who makes the part, and their part number. One manufacturer part belongs to
-- exactly one item, compared ignoring case, spaces and punctuation, so the same part can't get two numbers.
CREATE TABLE item_manufacturer_parts (
    item_id          uuid NOT NULL REFERENCES items (id) ON DELETE CASCADE,
    manufacturer     text NOT NULL,
    part_number      text NOT NULL,
    manufacturer_key text NOT NULL,
    part_number_key  text NOT NULL,
    preferred        boolean NOT NULL DEFAULT false,
    PRIMARY KEY (item_id, manufacturer_key, part_number_key)
);

CREATE UNIQUE INDEX item_manufacturer_parts_unique ON item_manufacturer_parts (manufacturer_key, part_number_key);
CREATE INDEX item_manufacturer_parts_key ON item_manufacturer_parts (part_number_key);

-- Other numbers the same part is known by: legacy vault numbers, each ERP's old code. Searchable, never the key.
CREATE TABLE item_cross_references (
    item_id uuid NOT NULL REFERENCES items (id) ON DELETE CASCADE,
    system  text NOT NULL,                     -- e.g. 'Legacy vault', 'simPRO (Company A)'
    number  text NOT NULL,
    PRIMARY KEY (item_id, system, number)
);

CREATE UNIQUE INDEX item_cross_references_unique ON item_cross_references (lower(system), lower(number));
CREATE INDEX item_cross_references_number ON item_cross_references (lower(number));

-- Files that define or describe an item: its model, drawing, datasheet.
CREATE TABLE item_files (
    item_id uuid NOT NULL REFERENCES items (id) ON DELETE CASCADE,
    file_id uuid NOT NULL REFERENCES files (id),
    role    text NOT NULL DEFAULT 'model' CHECK (role IN ('model', 'drawing', 'datasheet', 'other')),
    PRIMARY KEY (item_id, file_id)
);

CREATE INDEX item_files_file ON item_files (file_id);

INSERT INTO numbering_schemes (id, name, template, next_value)
VALUES ('items', 'Items (universal part numbers)', 'P-{seq:000000}-{check}', 100000)
ON CONFLICT (id) DO NOTHING;
