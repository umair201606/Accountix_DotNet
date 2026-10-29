-- One JSON document describing the public schema: every table with its
-- columns, constraints and indexes, and the sequences, views, functions,
-- triggers and types. Used to prove that two databases have the same schema
-- (SchemaDescription.cs; the golden file expected_schema.json was written
-- from a database the Python app built).
--
-- Run with search_path = public, so defaults print as Python's databases
-- show them (nextval('users_id_seq'::regclass)). schema_migrations, the
-- .NET migrator's own record, is left out. NOT NULL constraints are read
-- from the columns, never from pg_constraint (PostgreSQL 18 lists them
-- there too; 16 does not).
WITH rels AS (
    SELECT c.oid, c.relname, c.relkind
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'public'
       AND c.relname <> 'schema_migrations'
),
cols AS (
    SELECT r.relname AS table_name,
           jsonb_agg(jsonb_build_object(
               'name', a.attname,
               'position', a.pos,
               'type', format_type(a.atttypid, a.atttypmod),
               'not_null', a.attnotnull,
               'default', pg_get_expr(d.adbin, d.adrelid),
               'identity', NULLIF(a.attidentity::text, ''),
               'generated', NULLIF(a.attgenerated::text, ''),
               'collation', CASE WHEN a.attcollation <> t.typcollation THEN co.collname END
           ) ORDER BY a.pos) AS columns
      FROM rels r
      JOIN LATERAL (
          SELECT x.*, row_number() OVER (ORDER BY x.attnum) AS pos
            FROM pg_attribute x
           WHERE x.attrelid = r.oid AND x.attnum > 0 AND NOT x.attisdropped
      ) a ON true
      JOIN pg_type t ON t.oid = a.atttypid
      LEFT JOIN pg_collation co ON co.oid = a.attcollation
      LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
     WHERE r.relkind IN ('r', 'p')
     GROUP BY r.relname
),
cons AS (
    SELECT r.relname AS table_name,
           jsonb_agg(jsonb_build_object(
               'name', k.conname,
               'type', k.contype::text,
               'definition', pg_get_constraintdef(k.oid, true)
           ) ORDER BY k.conname) AS constraints
      FROM rels r
      JOIN pg_constraint k ON k.conrelid = r.oid
     WHERE k.contype <> 'n'
     GROUP BY r.relname
),
idx AS (
    SELECT i.tablename AS table_name,
           jsonb_agg(jsonb_build_object('name', i.indexname, 'definition', i.indexdef)
                     ORDER BY i.indexname) AS indexes
      FROM pg_indexes i
     WHERE i.schemaname = 'public' AND i.tablename <> 'schema_migrations'
     GROUP BY i.tablename
),
seq_owner AS (
    SELECT dp.objid AS seq_oid, tc.relname || '.' || a.attname AS owned_by
      FROM pg_depend dp
      JOIN pg_class tc ON tc.oid = dp.refobjid
      JOIN pg_attribute a ON a.attrelid = tc.oid AND a.attnum = dp.refobjsubid
     WHERE dp.classid = 'pg_class'::regclass
       AND dp.refclassid = 'pg_class'::regclass
       AND dp.deptype IN ('a', 'i')
       AND dp.refobjsubid > 0
),
seqs AS (
    SELECT jsonb_agg(jsonb_build_object(
               'name', s.relname,
               'type', format_type(q.seqtypid, NULL),
               'start', q.seqstart,
               'increment', q.seqincrement,
               'min', q.seqmin,
               'max', q.seqmax,
               'cache', q.seqcache,
               'cycle', q.seqcycle,
               'owned_by', o.owned_by
           ) ORDER BY s.relname) AS items
      FROM rels s
      JOIN pg_sequence q ON q.seqrelid = s.oid
      LEFT JOIN seq_owner o ON o.seq_oid = s.oid
     WHERE s.relkind = 'S'
),
views AS (
    SELECT jsonb_agg(jsonb_build_object('name', r.relname, 'kind', r.relkind::text,
                                        'definition', pg_get_viewdef(r.oid, true))
                     ORDER BY r.relname) AS items
      FROM rels r
     WHERE r.relkind IN ('v', 'm')
),
funcs AS (
    SELECT jsonb_agg(jsonb_build_object('name', p.proname,
                                        'arguments', pg_get_function_identity_arguments(p.oid),
                                        'definition', pg_get_functiondef(p.oid))
                     ORDER BY p.proname, pg_get_function_identity_arguments(p.oid)) AS items
      FROM pg_proc p
      JOIN pg_namespace n ON n.oid = p.pronamespace
     WHERE n.nspname = 'public' AND p.prokind IN ('f', 'p')
),
trigs AS (
    SELECT jsonb_agg(jsonb_build_object('table', r.relname, 'name', g.tgname,
                                        'definition', pg_get_triggerdef(g.oid, true))
                     ORDER BY r.relname, g.tgname) AS items
      FROM pg_trigger g
      JOIN rels r ON r.oid = g.tgrelid
     WHERE NOT g.tgisinternal
),
types AS (
    SELECT jsonb_agg(jsonb_build_object(
               'name', t.typname,
               'kind', t.typtype::text,
               'labels', (SELECT jsonb_agg(e.enumlabel ORDER BY e.enumsortorder)
                            FROM pg_enum e WHERE e.enumtypid = t.oid),
               'base', CASE WHEN t.typtype = 'd' THEN format_type(t.typbasetype, t.typtypmod) END
           ) ORDER BY t.typname) AS items
      FROM pg_type t
      JOIN pg_namespace n ON n.oid = t.typnamespace
     WHERE n.nspname = 'public'
       AND t.typtype IN ('e', 'd', 'r', 'm')
),
tables AS (
    SELECT jsonb_object_agg(r.relname, jsonb_build_object(
               'columns', COALESCE(c.columns, '[]'::jsonb),
               'constraints', COALESCE(k.constraints, '[]'::jsonb),
               'indexes', COALESCE(i.indexes, '[]'::jsonb)
           )) AS items
      FROM rels r
      LEFT JOIN cols c ON c.table_name = r.relname
      LEFT JOIN cons k ON k.table_name = r.relname
      LEFT JOIN idx i ON i.table_name = r.relname
     WHERE r.relkind IN ('r', 'p')
)
SELECT jsonb_build_object(
           'tables', COALESCE((SELECT items FROM tables), '{}'::jsonb),
           'sequences', COALESCE((SELECT items FROM seqs), '[]'::jsonb),
           'views', COALESCE((SELECT items FROM views), '[]'::jsonb),
           'functions', COALESCE((SELECT items FROM funcs), '[]'::jsonb),
           'triggers', COALESCE((SELECT items FROM trigs), '[]'::jsonb),
           'types', COALESCE((SELECT items FROM types), '[]'::jsonb)
       )::text;
