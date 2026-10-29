-- ERP connections set up by administrators in Openhold (alongside any in the server's settings file). Secrets (API keys,
-- client secrets) are stored only encrypted (SettingsSecrets), as one JSON object of name to value.
CREATE TABLE erp_connections (
    id          text PRIMARY KEY,
    name        text NOT NULL,
    kind        text NOT NULL,
    enabled     boolean NOT NULL DEFAULT true,
    settings    jsonb NOT NULL DEFAULT '{}',
    secrets     bytea,
    updated_by  uuid REFERENCES users (id),
    updated_at  timestamptz NOT NULL DEFAULT now()
);
