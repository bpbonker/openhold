-- Openhold imposes no numbering: catalogue items take their numbers from whichever scheme an administrator picks.
-- A vault being created (nothing configured yet) loses the scheme the catalogue migration used to add; a vault already
-- in use keeps it, and its configuration now names it as the catalogue's scheme.
DELETE FROM numbering_schemes s
WHERE s.id = 'items'
  AND NOT EXISTS (SELECT 1 FROM vault_config)
  AND NOT EXISTS (SELECT 1 FROM number_reservations r WHERE r.scheme_id = s.id)
  AND NOT EXISTS (SELECT 1 FROM numbering_counters c WHERE c.scheme_id = s.id);

INSERT INTO vault_config (version, comment, document)
SELECT latest.version + 1, 'Catalogue numbering is now a setting', jsonb_set(latest.document, '{catalogueNumberingSchemeId}', '"items"')
FROM (SELECT version, document FROM vault_config ORDER BY version DESC LIMIT 1) latest
WHERE EXISTS (SELECT 1 FROM numbering_schemes WHERE id = 'items')
  AND NOT (latest.document ? 'catalogueNumberingSchemeId');
