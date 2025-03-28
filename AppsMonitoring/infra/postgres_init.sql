CREATE DATABASE monitordb;
\c monitordb;
CREATE SCHEMA monitor;

ALTER SYSTEM SET max_connections TO '200';

CREATE ROLE platform_monitoring WITH LOGIN PASSWORD 'Password';

GRANT USAGE ON SCHEMA monitor TO platform_monitoring;
