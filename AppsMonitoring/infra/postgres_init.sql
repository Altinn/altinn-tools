CREATE DATABASE monitoringdb;
\c monitoringdb;
CREATE SCHEMA monitoring;

ALTER SYSTEM SET max_connections TO '200';

CREATE ROLE platform_monitoring WITH LOGIN PASSWORD 'Password';

GRANT USAGE ON SCHEMA monitoring TO platform_monitoring;
GRANT SELECT,INSERT,UPDATE,REFERENCES,DELETE,TRUNCATE,REFERENCES,TRIGGER ON ALL TABLES IN SCHEMA monitoring TO platform_monitoring;
GRANT ALL ON ALL SEQUENCES IN SCHEMA monitoring TO platform_monitoring;
