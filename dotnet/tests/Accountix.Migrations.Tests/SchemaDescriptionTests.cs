using System.Text.Json.Nodes;

namespace Accountix.Migrations.Tests;

/// <summary>The comparisons the other tests rely on notice small differences, and name them.</summary>
public class SchemaDescriptionTests
{
    [PostgresFact]
    public async Task One_changed_type_default_index_or_row_is_found_and_named()
    {
        await using var db = await TempDb.CreateAsync();
        await db.Migrator().MigrateAsync();
        var data = await Pg.DigestsAsync(db.Name);

        await db.ExecAsync("""
            ALTER TABLE users ALTER COLUMN full_name TYPE varchar(101);
            ALTER TABLE roles ALTER COLUMN description SET DEFAULT 'x';
            CREATE INDEX ix_extra ON roles (description);
            ALTER TABLE companies ALTER COLUMN slug DROP NOT NULL;
            UPDATE roles SET description = 'Admin role.' WHERE id = 1;
            """);
        var diffs = SchemaDescription.Differences(SchemaDescription.Expected(), await Pg.SchemaAsync(db.Name));

        Assert.Contains(diffs, d => d.StartsWith("/tables/users/columns[full_name]/type: expected \"character varying(100)\"", StringComparison.Ordinal));
        Assert.Contains(diffs, d => d.StartsWith("/tables/roles/columns[description]/default", StringComparison.Ordinal));
        Assert.Contains(diffs, d => d.StartsWith("/tables/roles/indexes[ix_extra]: unexpected", StringComparison.Ordinal));
        Assert.Contains(diffs, d => d.StartsWith("/tables/companies/columns[slug]/not_null", StringComparison.Ordinal));
        Assert.Equal(4, diffs.Count);

        var after = await Pg.DigestsAsync(db.Name);
        var changed = data.AsObject().Where(p => !JsonNode.DeepEquals(p.Value, after[p.Key])).Select(p => p.Key).ToList();
        Assert.Equal(["roles"], changed);
    }

    [Fact]
    public void Descriptions_are_written_with_sorted_keys_so_a_recapture_diffs_cleanly()
    {
        var node = JsonNode.Parse("""{"b": 1, "a": {"d": [ {"z": 1, "y": 2} ], "c": null}}""");
        Assert.Equal("{\n  \"a\": {\n    \"c\": null,\n    \"d\": [\n      {\n        \"y\": 2,\n        \"z\": 1\n      }\n    ]\n  },\n  \"b\": 1\n}\n",
            SchemaDescription.ToText(node!));
    }
}
