-- This script runs automatically the FIRST time the postgres container
-- starts with an empty data volume.

-- Table: usage_logs
-- Every APPROVED request gets one row here, written asynchronously by the
-- background worker (never on the hot path). This powers the dashboard:
-- trend graphs, average response time, per-client usage over time.
CREATE TABLE IF NOT EXISTS usage_logs (
    id              BIGSERIAL PRIMARY KEY,
    client_id       VARCHAR(100)        NOT NULL,
    timestamp_utc   TIMESTAMPTZ         NOT NULL,
    was_allowed     BOOLEAN             NOT NULL,
    response_time_ms DOUBLE PRECISION,
    endpoint        VARCHAR(200)
);

-- The dashboard's main queries are: "give me client X's usage between two
-- dates" and "give me trends over the last N days". This composite index
-- makes both fast because Postgres can seek directly to the client's rows
-- and scan them in timestamp order, instead of scanning the whole table.
CREATE INDEX IF NOT EXISTS idx_usage_logs_client_time
    ON usage_logs (client_id, timestamp_utc DESC);

-- Table: client_policies
-- Lets us change a client's rate limit without redeploying code.
-- The API reads this (and caches it) instead of hardcoding limits.
CREATE TABLE IF NOT EXISTS client_policies (
    client_id        VARCHAR(100) PRIMARY KEY,
    limit_per_window  INTEGER      NOT NULL,
    window_seconds    INTEGER      NOT NULL
);

-- Seed data: 11 clients with deliberately varied limits AND window sizes,
-- not just "more of the same" - useful for rigorous testing of edge cases
-- beyond the brief's two headline examples (Client A: 100/min, Client B:
-- 5000/min, both kept below as client-a and client-b).
--
--   client_id  | limit | window  | 
--   -----------|-------|---------|
--   client-a   |   100 |  60s    |
--   client-b   |  5000 |  60s    |
--   client-c   |    20 |  60s    |
--   client-d   |     5 |  60s    |
--   client-e   |     1 |  60s    |
--   client-f   |    50 |  10s    |
--   client-g   |  1000 |  60s    |
--   client-h   | 10000 |  60s    |
--   client-i   |   200 | 300s    |
--   client-j   |    30 |   5s    |
--   client-k   |     2 |   1s    |
INSERT INTO client_policies (client_id, limit_per_window, window_seconds) VALUES
    ('client-a', 100,   60),
    ('client-b', 5000,  60),
    ('client-c', 20,    60),
    ('client-d', 5,     60),
    ('client-e', 1,     60),
    ('client-f', 50,    10),
    ('client-g', 1000,  60),
    ('client-h', 10000, 60),
    ('client-i', 200,   300),
    ('client-j', 30,    5),
    ('client-k', 2,     1)
ON CONFLICT (client_id) DO NOTHING;