using Accountix.RefDb;
using Accountix.TestSupport;
using Npgsql;

namespace Accountix.Tests;

/// <summary>tools/Accountix.RefDb: the reference database comes from the fixture with no Python and no Postgres client tools.</summary>
public class RefDbTests
{
    private static string Fixture => Path.Combine(Reference.DotnetRoot, "tests", "fixtures", "reference.sql.gz");

    [Fact]
    public void The_dump_is_split_into_statements_as_psql_would()
    {
        const string sql = """
            --
            -- PostgreSQL database dump
            --
            \restrict abc123
            SET standard_conforming_strings = on;
            SELECT pg_catalog.set_config('search_path', '', false);
            INSERT INTO public.t VALUES (1, 'a;b', 'it''s; fine', E'back\'slash;', "odd;name");
            INSERT INTO public.t VALUES (2, 'line one
            -- not a comment; still text
            line three');
            CREATE FUNCTION public.f() RETURNS int AS $body$ SELECT 1; $body$ LANGUAGE sql;
            /* block; comment */ SELECT $$x;y$$;
            \unrestrict abc123
            """;
        var statements = ReferenceDatabase.Statements(new StringReader(sql.Replace("\r", ""))).ToList();
        Assert.Equal(6, statements.Count);
        Assert.Equal("SET standard_conforming_strings = on", statements[0]);
        Assert.Equal("INSERT INTO public.t VALUES (1, 'a;b', 'it''s; fine', E'back\\'slash;', \"odd;name\")", statements[2]);
        Assert.Contains("-- not a comment; still text", statements[3]);
        Assert.Equal("CREATE FUNCTION public.f() RETURNS int AS $body$ SELECT 1; $body$ LANGUAGE sql", statements[4]);
        Assert.Equal("SELECT $$x;y$$", statements[5].Trim());
        Assert.DoesNotContain(statements, s => s.Contains("restrict"));
    }

    [Fact]
    public void Connection_strings_keep_the_form_tests_have_always_read()
    {
        var socket = Connection.Server("postgresql://postgres:@/accountix_ref?host=/tmp/pg");
        Assert.Equal("Host=/tmp/pg;Database=accountix_ref_hr;Username=postgres", Connection.Format(socket, "accountix_ref_hr"));
        Assert.Equal("postgresql://postgres:@/accountix_ref_hr?host=/tmp/pg", Connection.Url(socket, "accountix_ref_hr"));
        var tcp = Connection.Server("Host=db.local;Port=5433;Username=app;Password=p@ss;Database=whatever");
        Assert.Equal("Host=db.local;Database=t_accountix_ref;Username=app;Port=5433;Password=p@ss", Connection.Format(tcp, "t_accountix_ref"));
        Assert.Equal("postgresql://app:p%40ss@db.local:5433/t_accountix_ref", Connection.Url(tcp, "t_accountix_ref"));
        Assert.Equal(("t_accountix_ref", "accountix_ref_e2e"),
                     (ReferenceDatabase.DatabaseName("t_", null), ReferenceDatabase.DatabaseName("", "e2e")));
    }

    /// <summary>Builds a throwaway database from the fixture on the reference server, checks it, drops it.</summary>
    [ReferenceFact]
    public async Task The_fixture_builds_a_database_at_its_revision_with_the_recorded_people()
    {
        string server = Reference.Json.GetProperty("connection_string").GetString()!;
        string name = $"t_refdb_selftest_{Environment.ProcessId}";
        try
        {
            await ReferenceDatabase.CreateFromFixtureAsync(server, name, Fixture);
            string cs = Connection.ForDatabase(server, name);
            Assert.Equal("0018_report_indexes", await ReferenceDatabase.AlembicVersionAsync(cs));
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            foreach (var u in Reference.Json.GetProperty("users").EnumerateObject())
            {
                await using var cmd = new NpgsqlCommand("SELECT email FROM users WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("id", u.Value.GetInt32());
                Assert.Equal(u.Name, await cmd.ExecuteScalarAsync() as string);
            }
            // Sequences continue after the copied rows (an insert does not collide).
            await using (var next = new NpgsqlCommand("SELECT nextval('users_id_seq') > (SELECT max(id) FROM users)", conn))
                Assert.True((bool)(await next.ExecuteScalarAsync())!);
            // Sessions and the key ring are not part of the fixture.
            await using (var empty = new NpgsqlCommand("SELECT (SELECT count(*) FROM user_sessions) + (SELECT count(*) FROM dotnet_data_protection_keys)", conn))
                Assert.Equal(0L, await empty.ExecuteScalarAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ReferenceDatabase.DropAsync(server, name);
        }
    }
}
