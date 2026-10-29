-- Row count and an md5 of every row (as text, in byte order) for each table
-- in public, except schema_migrations (the .NET migrator's own record).
-- Two databases with the same digests hold the same data. Run after
-- SET DateStyle = 'ISO, MDY', TimeZone = 'UTC', extra_float_digits = 1,
-- bytea_output = 'hex', IntervalStyle = 'postgres', so every client prints
-- rows the same way (make_migration_fixtures.py and the .NET tests both do).
-- The function lives in pg_temp: it disappears with the connection.
CREATE OR REPLACE FUNCTION pg_temp.ax_table_digest(tbl text, OUT n bigint, OUT h text)
LANGUAGE plpgsql AS $fn$
BEGIN
    EXECUTE format('SELECT count(*), md5(string_agg(t::text, E''\n'' ORDER BY t::text COLLATE "C")) FROM public.%I t', tbl)
       INTO n, h;
END
$fn$;

SELECT jsonb_object_agg(c.relname, jsonb_build_object('rows', d.n, 'md5', COALESCE(d.h, '')))::text
  FROM pg_class c
  JOIN pg_namespace ns ON ns.oid = c.relnamespace
  CROSS JOIN LATERAL pg_temp.ax_table_digest(c.relname) d
 WHERE ns.nspname = 'public' AND c.relkind IN ('r', 'p') AND c.relname <> 'schema_migrations';
