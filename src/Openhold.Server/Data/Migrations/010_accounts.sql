-- Sign-in accounts. Every person who signs in has one: Windows and organisation (OpenID Connect) accounts are
-- added the first time they sign in, Openhold accounts are created by an administrator. An account can be disabled,
-- which stops every way of signing in at once. Accounts live in the server's identity vault (the first vault).
CREATE TABLE accounts (
    username             text PRIMARY KEY,         -- lower case; 'jsmith' for home-domain users, 'jsmith@partner.com' otherwise
    display_name         text NOT NULL DEFAULT '',
    email                text,
    kind                 text NOT NULL CHECK (kind IN ('windows', 'local', 'oidc')),
    password_hash        text,                     -- Openhold accounts only
    must_change_password boolean NOT NULL DEFAULT false,
    mfa_required         boolean NOT NULL DEFAULT false,
    mfa_secret           bytea,                    -- TOTP secret once set up
    mfa_enabled          boolean NOT NULL DEFAULT false,
    disabled             boolean NOT NULL DEFAULT false,
    failed_attempts      int NOT NULL DEFAULT 0,
    locked_until         timestamptz,
    created_at           timestamptz NOT NULL DEFAULT now(),
    created_by           text,
    last_sign_in_at      timestamptz
);

-- Long-lived sign-in sessions for Openhold and organisation accounts. Only a hash of each token is kept, so a copy
-- of the database can't be used to sign in. Revoking a session deletes it.
CREATE TABLE refresh_tokens (
    id         uuid PRIMARY KEY,
    username   text NOT NULL REFERENCES accounts (username) ON DELETE CASCADE,
    token_hash bytea NOT NULL UNIQUE,
    machine    text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    used_at    timestamptz
);

CREATE INDEX refresh_tokens_user ON refresh_tokens (username);

-- Public keys that verify the short-lived access tokens the authority signs. Kept in the database so every
-- site's server can verify tokens; the private key never leaves the authority.
CREATE TABLE signing_keys (
    kid        text PRIMARY KEY,
    public_key text NOT NULL,                      -- PEM
    created_at timestamptz NOT NULL DEFAULT now(),
    retired_at timestamptz
);
