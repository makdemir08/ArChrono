-- ArChrono metadata şeması v1
-- Zaman damgaları: UTC unix milisaniye (INTEGER).
-- Git repository içeriği burada tutulmaz; yalnızca uygulamanın ürettiği bilgiler.
-- Sırlar (API anahtarı, token) bu veritabanına ASLA yazılmaz.

CREATE TABLE repository (
    id                    INTEGER PRIMARY KEY,
    root_path             TEXT    NOT NULL UNIQUE,      -- çalışma dizini (worktree kökü)
    git_dir               TEXT    NOT NULL,
    common_dir            TEXT    NOT NULL,             -- aynı repo'nun worktree'leri bunu paylaşır
    name                  TEXT    NOT NULL,
    object_format         TEXT    NOT NULL DEFAULT 'sha1' CHECK (object_format IN ('sha1', 'sha256')),
    created_at            INTEGER NOT NULL,
    last_opened_at        INTEGER,
    is_favorite           INTEGER NOT NULL DEFAULT 0,
    time_machine_enabled  INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX ix_repository_common_dir ON repository (common_dir);
CREATE INDEX ix_repository_last_opened ON repository (last_opened_at DESC);

-- Content store'daki (store/objects) her blob için bir satır.
CREATE TABLE content_blob (
    blob_id      TEXT    PRIMARY KEY,                   -- Git uyumlu blob id
    size         INTEGER NOT NULL,                      -- ham boyut
    stored_size  INTEGER NOT NULL,                      -- diskteki boyut
    compression  TEXT    NOT NULL CHECK (compression IN ('none', 'brotli')),
    created_at   INTEGER NOT NULL
) WITHOUT ROWID;

CREATE TABLE snapshot (
    id             INTEGER PRIMARY KEY,
    repository_id  INTEGER NOT NULL REFERENCES repository (id) ON DELETE CASCADE,
    created_at     INTEGER NOT NULL,
    trigger        TEXT    NOT NULL CHECK (trigger IN ('operation', 'timer', 'manual', 'restore', 'shutdown')),
    label          TEXT,
    head_ref       TEXT,                                -- refs/heads/main; detached ise NULL
    head_sha       TEXT,                                -- unborn branch ise NULL
    index_tree     TEXT    NOT NULL,
    pin_commit     TEXT,                                -- refs/archrono/pins/<sha>
    fingerprint    TEXT    NOT NULL,
    entry_count    INTEGER NOT NULL,
    total_bytes    INTEGER NOT NULL,                    -- overlay dosyalarının ham toplamı
    new_bytes      INTEGER NOT NULL,                    -- bu snapshot ile store'a eklenen (sıkıştırılmış)
    skipped_json   TEXT                                 -- [{path, reason}]
);
CREATE INDEX ix_snapshot_repo_time ON snapshot (repository_id, created_at DESC);
CREATE INDEX ix_snapshot_pin ON snapshot (pin_commit);

-- Overlay: index'ten farklı çalışma alanı dosyaları + untracked dosyalar.
CREATE TABLE snapshot_entry (
    snapshot_id  INTEGER NOT NULL REFERENCES snapshot (id) ON DELETE CASCADE,
    path         TEXT    NOT NULL,                      -- repository köküne göre, '/' ayraçlı
    kind         TEXT    NOT NULL CHECK (kind IN ('modified', 'untracked', 'deleted', 'conflicted')),
    blob_id      TEXT,                                  -- deleted ise NULL
    mode         INTEGER,                               -- Git mode (0o100644, 0o100755, 0o120000)
    size         INTEGER,
    PRIMARY KEY (snapshot_id, path)
) WITHOUT ROWID;
CREATE INDEX ix_snapshot_entry_blob ON snapshot_entry (blob_id);
CREATE INDEX ix_snapshot_entry_path ON snapshot_entry (path, snapshot_id);

CREATE TABLE git_operation (
    id                      INTEGER PRIMARY KEY,
    repository_id           INTEGER NOT NULL REFERENCES repository (id) ON DELETE CASCADE,
    kind                    TEXT    NOT NULL,           -- reset, rebase, branch-delete, commit, undo…
    title                   TEXT    NOT NULL,
    command_text            TEXT    NOT NULL,           -- gösterim amaçlı eşdeğer git komutu
    risk                    TEXT    NOT NULL CHECK (risk IN ('safe', 'reversible', 'destructive', 'remote')),
    status                  TEXT    NOT NULL CHECK (status IN ('running', 'succeeded', 'failed', 'conflicted', 'blocked', 'cancelled')),
    started_at              INTEGER NOT NULL,
    finished_at             INTEGER,
    error_code              TEXT,
    error_message           TEXT,
    error_details           TEXT,
    before_point_id         INTEGER REFERENCES recovery_point (id) ON DELETE SET NULL,
    after_point_id          INTEGER REFERENCES recovery_point (id) ON DELETE SET NULL,
    undo_of_operation_id    INTEGER REFERENCES git_operation (id) ON DELETE SET NULL,
    undone_by_operation_id  INTEGER REFERENCES git_operation (id) ON DELETE SET NULL,
    metadata_json           TEXT
);
CREATE INDEX ix_git_operation_repo_time ON git_operation (repository_id, started_at DESC);

CREATE TABLE recovery_point (
    id                  INTEGER PRIMARY KEY,
    repository_id       INTEGER NOT NULL REFERENCES repository (id) ON DELETE CASCADE,
    created_at          INTEGER NOT NULL,
    kind                TEXT    NOT NULL CHECK (kind IN ('before-operation', 'after-operation', 'before-restore', 'manual')),
    title               TEXT    NOT NULL,
    operation_id        INTEGER REFERENCES git_operation (id) ON DELETE SET NULL,
    head_ref            TEXT,
    head_sha            TEXT,
    branch              TEXT,
    repo_state          TEXT    NOT NULL,
    snapshot_id         INTEGER REFERENCES snapshot (id) ON DELETE SET NULL,
    changed_file_count  INTEGER NOT NULL,
    is_pinned           INTEGER NOT NULL DEFAULT 0,
    metadata_json       TEXT
);
CREATE INDEX ix_recovery_point_repo_time ON recovery_point (repository_id, created_at DESC);
CREATE INDEX ix_recovery_point_snapshot ON recovery_point (snapshot_id);

CREATE TABLE recovery_ref (
    recovery_point_id  INTEGER NOT NULL REFERENCES recovery_point (id) ON DELETE CASCADE,
    ref_name           TEXT    NOT NULL,
    target_sha         TEXT    NOT NULL,
    PRIMARY KEY (recovery_point_id, ref_name)
) WITHOUT ROWID;
CREATE INDEX ix_recovery_ref_target ON recovery_ref (target_sha);

CREATE TABLE recovery_stash (
    recovery_point_id  INTEGER NOT NULL REFERENCES recovery_point (id) ON DELETE CASCADE,
    position           INTEGER NOT NULL,                -- stash@{position}
    commit_sha         TEXT    NOT NULL,
    message            TEXT    NOT NULL,
    PRIMARY KEY (recovery_point_id, position)
) WITHOUT ROWID;

-- refs/archrono/pins/<sha> ref'lerinin kaydı (common_dir başına).
CREATE TABLE git_pin (
    common_dir  TEXT    NOT NULL,
    commit_sha  TEXT    NOT NULL,
    created_at  INTEGER NOT NULL,
    PRIMARY KEY (common_dir, commit_sha)
) WITHOUT ROWID;

-- Worktree / agent çalışma alanları (Phase 2 UI; şema şimdiden hazır).
CREATE TABLE workspace (
    id              INTEGER PRIMARY KEY,
    repository_id   INTEGER NOT NULL REFERENCES repository (id) ON DELETE CASCADE,
    worktree_path   TEXT    NOT NULL UNIQUE,
    branch          TEXT,
    label           TEXT,
    agent_kind      TEXT,                               -- claude-code, codex, cursor, custom
    agent_command   TEXT,
    status          TEXT    NOT NULL DEFAULT 'idle' CHECK (status IN ('idle', 'running', 'stopped', 'missing')),
    created_at      INTEGER NOT NULL,
    last_active_at  INTEGER
);

-- AI denetim kaydı. İstem metni saklanmaz; yalnızca özet ve dosya yolları.
CREATE TABLE ai_request (
    id                   INTEGER PRIMARY KEY,
    repository_id        INTEGER REFERENCES repository (id) ON DELETE SET NULL,
    created_at           INTEGER NOT NULL,
    feature              TEXT    NOT NULL,              -- commit-message, explain-commit, explain-branch…
    provider             TEXT    NOT NULL,
    model                TEXT    NOT NULL,
    endpoint_host        TEXT    NOT NULL,
    request_bytes        INTEGER NOT NULL,
    included_paths_json  TEXT    NOT NULL,
    excluded_paths_json  TEXT    NOT NULL,
    redaction_count      INTEGER NOT NULL,
    prompt_sha256        TEXT    NOT NULL,
    status               TEXT    NOT NULL CHECK (status IN ('succeeded', 'failed', 'cancelled')),
    duration_ms          INTEGER,
    input_tokens         INTEGER,
    output_tokens        INTEGER,
    error_message        TEXT
);
CREATE INDEX ix_ai_request_time ON ai_request (created_at DESC);

-- AI açıklama önbelleği (yalnızca yerel; aynı commit için tekrar istek atılmaz).
CREATE TABLE ai_result_cache (
    cache_key      TEXT    PRIMARY KEY,                 -- sha256(feature|subject|provider|model|prompt)
    repository_id  INTEGER REFERENCES repository (id) ON DELETE CASCADE,
    feature        TEXT    NOT NULL,
    subject        TEXT    NOT NULL,                    -- commit sha, branch adı…
    provider       TEXT    NOT NULL,
    model          TEXT    NOT NULL,
    content        TEXT    NOT NULL,
    created_at     INTEGER NOT NULL
) WITHOUT ROWID;
CREATE INDEX ix_ai_result_subject ON ai_result_cache (repository_id, feature, subject);

-- Uygulama ayarları (JSON değerler). Sır içermez.
CREATE TABLE setting (
    key    TEXT PRIMARY KEY,
    value  TEXT NOT NULL
) WITHOUT ROWID;
