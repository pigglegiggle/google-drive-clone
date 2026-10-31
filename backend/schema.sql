CREATE TABLE IF NOT EXISTS users (
    id uuid PRIMARY KEY,
    email text NOT NULL UNIQUE,
    password_hash text NOT NULL,
    quota bigint NOT NULL DEFAULT 10737418240,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS entries (
    id uuid PRIMARY KEY,
    owner_id uuid NOT NULL REFERENCES users(id),
    parent_id uuid REFERENCES entries(id),
    name text NOT NULL,
    kind text NOT NULL CHECK (kind IN ('file','folder')),
    trashed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS sibling_names ON entries(owner_id, COALESCE(parent_id,'00000000-0000-0000-0000-000000000000'::uuid), lower(name)) WHERE trashed_at IS NULL;
CREATE INDEX IF NOT EXISTS entry_parent ON entries(owner_id,parent_id);
CREATE TABLE IF NOT EXISTS versions (
    id uuid PRIMARY KEY,
    entry_id uuid NOT NULL REFERENCES entries(id),
    object_key text NOT NULL UNIQUE,
    size bigint NOT NULL,
    sha256 text NOT NULL,
    content_type text NOT NULL,
    created_by uuid NOT NULL REFERENCES users(id),
    selected_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS entry_versions ON versions(entry_id,selected_at DESC,id DESC);
CREATE TABLE IF NOT EXISTS grants (
    entry_id uuid NOT NULL REFERENCES entries(id),
    user_id uuid NOT NULL REFERENCES users(id),
    role text NOT NULL CHECK(role IN ('viewer','editor')),
    PRIMARY KEY(entry_id,user_id)
);
CREATE TABLE IF NOT EXISTS shares (
    id uuid PRIMARY KEY,
    entry_id uuid NOT NULL REFERENCES entries(id),
    token_hash text NOT NULL UNIQUE,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS uploads (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id),
    owner_id uuid NOT NULL REFERENCES users(id),
    entry_id uuid REFERENCES entries(id),
    parent_id uuid REFERENCES entries(id),
    name text NOT NULL,
    size bigint NOT NULL,
    sha256 text NOT NULL,
    content_type text NOT NULL,
    object_key text NOT NULL UNIQUE,
    multipart_id text NOT NULL,
    state text NOT NULL DEFAULT 'active' CHECK(state IN ('active','assembling','complete','failed','cancelled','expired')),
    result_entry_id uuid REFERENCES entries(id),
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL DEFAULT now()+interval '24 hours'
);
CREATE TABLE IF NOT EXISTS upload_parts (
    upload_id uuid NOT NULL REFERENCES uploads(id) ON DELETE CASCADE,
    number integer NOT NULL,
    etag text NOT NULL,
    size bigint NOT NULL,
    sha256 text NOT NULL,
    PRIMARY KEY(upload_id,number)
);
CREATE INDEX IF NOT EXISTS uploads_state ON uploads(state,expires_at);
CREATE TABLE IF NOT EXISTS object_deletions (
    object_key text PRIMARY KEY,
    multipart_id text,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE OR REPLACE VIEW live_entries AS
WITH RECURSIVE visible AS (
    SELECT * FROM entries WHERE parent_id IS NULL AND trashed_at IS NULL
    UNION ALL
    SELECT child.* FROM entries child JOIN visible parent ON child.parent_id=parent.id
    WHERE child.trashed_at IS NULL
) SELECT * FROM visible;
