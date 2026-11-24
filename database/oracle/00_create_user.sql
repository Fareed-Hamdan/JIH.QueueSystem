-- Run as SYSTEM (or another DBA account) in the pluggable database, e.g. FREEPDB1.
-- Only needed when the database is NOT created by docker-compose.yml (which creates the user automatically).
CREATE USER JIH_QUEUE IDENTIFIED BY "ChangeMe_App_2026" QUOTA UNLIMITED ON USERS;
GRANT CREATE SESSION, CREATE TABLE, CREATE SEQUENCE TO JIH_QUEUE;
