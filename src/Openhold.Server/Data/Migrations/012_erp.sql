-- Each catalogue item's record in each ERP connection, and the outbox that keeps them in step. A row is 'pending'
-- when the item changed since it was last sent; the ERP sync worker sends it and retries failures with back-off.
CREATE TABLE item_erp_links (
    item_id         uuid NOT NULL REFERENCES items (id),
    connection_id   text NOT NULL,                 -- ErpConnectionOptions.Id
    external_id     text,                          -- the ERP's id for the item, once it exists there
    status          text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'done', 'failed')),
    sent_version    int,                           -- the item row_version last sent
    attempts        int NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_error      text,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (item_id, connection_id)
);

CREATE INDEX item_erp_links_due ON item_erp_links (next_attempt_at) WHERE status IN ('pending', 'failed');
