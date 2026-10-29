-- Which sites hold each file content (by whole-file hash) in their chunk store. A site gets a row once every chunk
-- of the content is in its store: when a file is checked in there, or when its replicator has fetched the chunks
-- from a peer. A version whose content has no row for the local site is shown as "syncing" there.
CREATE TABLE content_presence (
    content_hash bytea NOT NULL,
    site_id      text NOT NULL REFERENCES sites (id),
    arrived_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (content_hash, site_id)
);

CREATE INDEX content_presence_site ON content_presence (site_id);

-- Existing contents are held by the site they were checked in at.
INSERT INTO content_presence (content_hash, site_id)
SELECT DISTINCT content_hash, origin_site FROM file_versions
ON CONFLICT DO NOTHING;

INSERT INTO content_presence (content_hash, site_id)
SELECT DISTINCT r.content_hash, v.origin_site
FROM renditions r JOIN file_versions v ON v.file_id = r.file_id AND v.version = r.version
ON CONFLICT DO NOTHING;

-- When each server last reported in, for the sites health view.
ALTER TABLE sites ADD COLUMN last_seen timestamptz;
