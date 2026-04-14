-- =============================================================================
-- IofBIM Revit MCP — PostgreSQL Schema
-- Target: Revit 2025+ / .NET 8 (element IDs are 64-bit)
-- All Revit element ID columns use BIGINT to avoid int32 overflow.
-- Composite primary keys on (doc_id, id) support multiple models in one DB.
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Core model tables
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS revit_elements (
    id          BIGINT NOT NULL,
    guid        UUID,
    name        TEXT,
    category    TEXT,
    type_name   TEXT,
    level       TEXT,
    doc_id      TEXT NOT NULL,
    last_saved  TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_revit_elements PRIMARY KEY (doc_id, id)
);

CREATE TABLE IF NOT EXISTS revit_elementtypes (
    id          BIGINT NOT NULL,
    guid        UUID,
    family      TEXT,
    type_name   TEXT,
    category    TEXT,
    doc_id      TEXT NOT NULL,
    last_saved  TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_revit_elementtypes PRIMARY KEY (doc_id, id)
);

CREATE TABLE IF NOT EXISTS revit_parameters (
    id                    SERIAL PRIMARY KEY,
    doc_id                TEXT,
    element_id            BIGINT,
    param_name            TEXT,
    param_value           TEXT,
    is_type               BOOLEAN,
    applicable_categories TEXT[],
    last_saved            TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT unique_element_param_v2 UNIQUE (doc_id, element_id, param_name)
);

CREATE INDEX IF NOT EXISTS idx_revit_parameters_doc_param   ON revit_parameters (doc_id, param_name);
CREATE INDEX IF NOT EXISTS idx_revit_parameters_doc_element ON revit_parameters (doc_id, element_id);

CREATE TABLE IF NOT EXISTS revit_type_parameters (
    id                    SERIAL PRIMARY KEY,
    doc_id                TEXT,
    element_type_id       BIGINT,
    param_name            TEXT,
    param_value           TEXT,
    applicable_categories TEXT[],
    last_saved            TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT unique_type_param_v2 UNIQUE (doc_id, element_type_id, param_name)
);

CREATE INDEX IF NOT EXISTS idx_revit_type_parameters_doc_param ON revit_type_parameters (doc_id, param_name);
CREATE INDEX IF NOT EXISTS idx_revit_type_parameters_doc_type  ON revit_type_parameters (doc_id, element_type_id);

-- ---------------------------------------------------------------------------
-- Model metadata
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS model_info (
    doc_id             TEXT PRIMARY KEY,
    model_name         TEXT,
    guid               UUID,
    last_saved         TIMESTAMP,
    project_info       JSONB,
    project_parameters JSONB
);

-- ---------------------------------------------------------------------------
-- Views, sheets, schedules, families, categories
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS revit_views (
    id                  BIGINT NOT NULL,
    guid                UUID,
    name                TEXT,
    view_type           TEXT,
    scale               INTEGER,
    discipline          TEXT,
    detail_level        TEXT,
    associated_sheet_id BIGINT,
    doc_id              TEXT NOT NULL,
    last_saved          TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_revit_views PRIMARY KEY (doc_id, id)
);

CREATE TABLE IF NOT EXISTS revit_sheets (
    id          BIGINT NOT NULL,
    guid        UUID,
    name        TEXT,
    number      TEXT,
    title_block TEXT,
    doc_id      TEXT NOT NULL,
    last_saved  TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_revit_sheets PRIMARY KEY (doc_id, id)
);

CREATE TABLE IF NOT EXISTS revit_schedules (
    id         BIGINT NOT NULL,
    guid       UUID,
    name       TEXT,
    category   TEXT,
    doc_id     TEXT NOT NULL,
    last_saved TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_revit_schedules PRIMARY KEY (doc_id, id)
);

CREATE TABLE IF NOT EXISTS revit_families (
    id          SERIAL PRIMARY KEY,
    name        TEXT,
    family_type TEXT,
    category    TEXT,
    guid        UUID,
    doc_id      TEXT,
    last_saved  TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT unique_family_name_type_v2 UNIQUE (doc_id, name, family_type, category)
);

CREATE TABLE IF NOT EXISTS revit_categories (
    id             SERIAL PRIMARY KEY,
    enum           TEXT,
    name           TEXT,
    category_group TEXT,
    description    TEXT,
    guid           UUID,
    last_saved     TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT unique_category_enum UNIQUE (enum)
);

-- ---------------------------------------------------------------------------
-- Revit link instances and linked model data
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS revit_link_instances (
    host_doc_id                 TEXT NOT NULL,
    instance_id                 BIGINT NOT NULL,
    link_doc_id                 TEXT NOT NULL,
    origin_x                    DOUBLE PRECISION,
    origin_y                    DOUBLE PRECISION,
    origin_z                    DOUBLE PRECISION,
    basisx_x                    DOUBLE PRECISION,
    basisx_y                    DOUBLE PRECISION,
    basisx_z                    DOUBLE PRECISION,
    basisy_x                    DOUBLE PRECISION,
    basisy_y                    DOUBLE PRECISION,
    basisy_z                    DOUBLE PRECISION,
    basisz_x                    DOUBLE PRECISION,
    basisz_y                    DOUBLE PRECISION,
    basisz_z                    DOUBLE PRECISION,
    rotation_z_radians          DOUBLE PRECISION,
    angle_to_true_north_radians DOUBLE PRECISION,
    last_saved                  TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_revit_link_instances PRIMARY KEY (host_doc_id, instance_id)
);

CREATE TABLE IF NOT EXISTS revit_linked_elements (
    host_doc_id      TEXT NOT NULL,
    link_instance_id BIGINT NOT NULL,
    link_doc_id      TEXT NOT NULL,
    id               BIGINT NOT NULL,
    guid             UUID,
    name             TEXT,
    category         TEXT,
    type_name        TEXT,
    level            TEXT,
    last_saved       TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_revit_linked_elements PRIMARY KEY (host_doc_id, link_instance_id, id)
);

CREATE TABLE IF NOT EXISTS revit_linked_elementtypes (
    host_doc_id      TEXT NOT NULL,
    link_instance_id BIGINT NOT NULL,
    link_doc_id      TEXT NOT NULL,
    id               BIGINT NOT NULL,
    guid             UUID,
    family           TEXT,
    type_name        TEXT,
    category         TEXT,
    last_saved       TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_revit_linked_elementtypes PRIMARY KEY (host_doc_id, link_instance_id, id)
);

CREATE TABLE IF NOT EXISTS revit_linked_parameters (
    id                    SERIAL PRIMARY KEY,
    host_doc_id           TEXT NOT NULL,
    link_instance_id      BIGINT NOT NULL,
    element_id            BIGINT NOT NULL,
    param_name            TEXT,
    param_value           TEXT,
    is_type               BOOLEAN,
    applicable_categories TEXT[],
    last_saved            TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT uq_revit_linked_parameters UNIQUE (host_doc_id, link_instance_id, element_id, param_name)
);

CREATE TABLE IF NOT EXISTS model_info_linked (
    host_doc_id        TEXT NOT NULL,
    link_doc_id        TEXT NOT NULL,
    model_name         TEXT,
    guid               UUID,
    last_saved         TIMESTAMP,
    project_info       JSONB,
    project_parameters JSONB,
    CONSTRAINT pk_model_info_linked PRIMARY KEY (host_doc_id, link_doc_id)
);

-- ---------------------------------------------------------------------------
-- Async job queue
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS mcp_queue (
    id           SERIAL PRIMARY KEY,
    plan         JSONB,
    status       TEXT DEFAULT 'pending',
    created_at   TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    completed_at TIMESTAMP,
    result       JSONB
);

-- ---------------------------------------------------------------------------
-- UI event stream (dashboard / analytics)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS ui_events (
    id         SERIAL PRIMARY KEY,
    session_id TEXT,
    doc_id     TEXT,
    event_type TEXT NOT NULL,
    payload    JSONB,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- =============================================================================
-- Migration: apply to existing databases created before Revit 2025+ support
-- =============================================================================

-- Add doc_id columns where missing (tables created by older schema versions)
ALTER TABLE revit_parameters      ADD COLUMN IF NOT EXISTS doc_id TEXT;
ALTER TABLE revit_type_parameters ADD COLUMN IF NOT EXISTS doc_id TEXT;

-- Upgrade all Revit element ID columns from INTEGER to BIGINT
ALTER TABLE revit_elements              ALTER COLUMN id               TYPE BIGINT;
ALTER TABLE revit_elementtypes          ALTER COLUMN id               TYPE BIGINT;
ALTER TABLE revit_parameters            ALTER COLUMN element_id       TYPE BIGINT;
ALTER TABLE revit_type_parameters       ALTER COLUMN element_type_id  TYPE BIGINT;
ALTER TABLE revit_views                 ALTER COLUMN id               TYPE BIGINT;
ALTER TABLE revit_sheets                ALTER COLUMN id               TYPE BIGINT;
ALTER TABLE revit_schedules             ALTER COLUMN id               TYPE BIGINT;
ALTER TABLE revit_link_instances        ALTER COLUMN instance_id      TYPE BIGINT;
ALTER TABLE revit_linked_elements       ALTER COLUMN id               TYPE BIGINT;
ALTER TABLE revit_linked_elements       ALTER COLUMN link_instance_id TYPE BIGINT;
ALTER TABLE revit_linked_elementtypes   ALTER COLUMN id               TYPE BIGINT;
ALTER TABLE revit_linked_elementtypes   ALTER COLUMN link_instance_id TYPE BIGINT;
ALTER TABLE revit_linked_parameters     ALTER COLUMN element_id       TYPE BIGINT;
ALTER TABLE revit_linked_parameters     ALTER COLUMN link_instance_id TYPE BIGINT;

-- Add composite unique indexes for V2 conflict resolution (if not already present)
CREATE UNIQUE INDEX IF NOT EXISTS idx_revit_elements_doc_id_id
    ON revit_elements (doc_id, id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_revit_elementtypes_doc_id_id
    ON revit_elementtypes (doc_id, id);
