DO $$ DECLARE
    tabname RECORD;
BEGIN
    FOR tabname IN (SELECT tablename
                    FROM pg_tables
                    WHERE schemaname = 'monitor')
LOOP
    EXECUTE 'DROP TABLE IF EXISTS monitor.' || quote_ident(tabname.tablename) || ' CASCADE';
END LOOP;
END $$;
