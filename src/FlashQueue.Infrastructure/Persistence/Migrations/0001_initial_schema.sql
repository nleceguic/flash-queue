-- FlashQueue: esquema inicial (events + reservations).
-- Aplicado por SchemaMigrator vía Npgsql/Dapper (sin EF), de forma idempotente.

CREATE TABLE IF NOT EXISTS events
(
    id             UUID PRIMARY KEY,
    name           TEXT    NOT NULL,
    total_stock    INTEGER NOT NULL CHECK (total_stock >= 0),
    reserved_stock INTEGER NOT NULL DEFAULT 0 CHECK (reserved_stock >= 0),
    CONSTRAINT ck_events_reserved_not_exceeding_total CHECK (reserved_stock <= total_stock)
);

CREATE TABLE IF NOT EXISTS reservations
(
    id                UUID PRIMARY KEY,
    event_id          UUID        NOT NULL REFERENCES events (id),
    user_id           UUID        NOT NULL,
    quantity          INTEGER     NOT NULL CHECK (quantity > 0),
    status            TEXT        NOT NULL CHECK (status IN ('Pending', 'Confirmed', 'Rejected')),
    requested_at      TIMESTAMPTZ NOT NULL,
    resolved_at       TIMESTAMPTZ,
    rejection_reason  TEXT
);

CREATE INDEX IF NOT EXISTS ix_reservations_event_id ON reservations (event_id);
