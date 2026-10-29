-- The rows Alembic 0016 rewrites, readable: each membership's module
-- switches, each company's owner, every login's address, every invitation's
-- address. Kept next to the digests so a failing upgrade test says what moved.
SELECT jsonb_build_object(
    'memberships', (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                        'id', m.id, 'company_id', m.company_id, 'user_id', m.user_id,
                        'status', m.status, 'module_flags', m.module_flags) ORDER BY m.id), '[]'::jsonb)
                      FROM company_memberships m),
    'companies', (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                      'id', c.id, 'slug', c.slug, 'created_by', c.created_by) ORDER BY c.id), '[]'::jsonb)
                    FROM companies c),
    'users', (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                  'id', u.id, 'email', u.email, 'is_super_admin', u.is_super_admin) ORDER BY u.id), '[]'::jsonb)
                FROM users u),
    'invitations', (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                        'id', i.id, 'company_id', i.company_id, 'email', i.email, 'status', i.status) ORDER BY i.id), '[]'::jsonb)
                      FROM company_invitations i),
    'alembic_version', (SELECT COALESCE(jsonb_agg(v.version_num ORDER BY v.version_num), '[]'::jsonb)
                          FROM alembic_version v)
)::text;
