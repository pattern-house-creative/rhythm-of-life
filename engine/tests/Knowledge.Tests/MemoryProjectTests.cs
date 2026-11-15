using Knowledge.Adapters;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Tests;

// The project registry (migration 035, Core/MemoryProjects.cs; SB-P3-07, docs/dev/memory.md section 5): a project made
// in its writer's scope, renamed, given aliases, archived and deleted with its memories kept; a Family project changed
// only by its creator or an admin; a slug or alias naming one project per scope; an alias finding its project's memories;
// and a map view of one project's members. Fictional people only: alex, sam and the household; every text is DEMO.
// Nothing outside these tests writes a project yet.

public sealed class MemoryProjectInputTests
{
    [Fact] public void A_slug_comes_from_the_name_as_upstream_derives_one()
    {
        Assert.Equal("canal-walks", MemoryProjectInput.Slug("Canal Walks!"));
        Assert.Equal("my-app", MemoryProjectInput.Slug("  My   App  "));
        Assert.Equal("kitchen-2026", MemoryProjectInput.Slug("--Kitchen__2026--"));
        Assert.Equal("caf-plans", MemoryProjectInput.Slug("Café plans"));
        Assert.Null(MemoryProjectInput.Slug("!!!"));
        Assert.Null(MemoryProjectInput.Slug("   "));
        var long_ = MemoryProjectInput.Slug(string.Join(' ', Enumerable.Repeat("towpath", 20)))!;
        Assert.True(long_.Length <= 64 && !long_.EndsWith('-'), long_);
    }

    [Fact] public void Names_descriptions_and_aliases_keep_to_the_registrys_limits()
    {
        static string Code(Action act) => Assert.Throws<MemoryRefused>(act).Code;
        Assert.Equal("Canal walks", MemoryProjectInput.Name("  Canal walks "));
        Assert.Equal("invalid", Code(() => MemoryProjectInput.Name(" ")));
        Assert.Equal("invalid", Code(() => MemoryProjectInput.Name(new string('n', 121))));
        Assert.Equal("invalid", Code(() => MemoryProjectInput.Name("bell\u0007")));
        Assert.Equal(1000, MemoryProjectInput.Description(new string('d', 1000)).Length);
        Assert.Equal("invalid", Code(() => MemoryProjectInput.Description(new string('d', 1001))));
        // Lower-case tags once each; the project's own slug is no other name and is left off.
        Assert.Equal(["towpath", "lockside"], MemoryProjectInput.Aliases([" Towpath ", "towpath", "LOCKSIDE", "canal-walks", ""], "canal-walks"));
        Assert.Equal("invalid", Code(() => MemoryProjectInput.Aliases(["project:canal"], "canal-walks")));
        Assert.Equal("invalid", Code(() => MemoryProjectInput.Aliases(["Status:draft"], "canal-walks")));
        Assert.Equal("invalid", Code(() => MemoryProjectInput.Aliases(Enumerable.Range(0, 17).Select(i => $"a{i}"), "canal-walks")));
        Assert.Equal("invalid", Code(() => MemoryProjectInput.Aliases([new string('a', 129)], "canal-walks")));
        Assert.Equal(16, MemoryProjectInput.Aliases(Enumerable.Range(0, 16).Select(i => $"a{i}"), "canal-walks").Count);
    }
}

public sealed class MemoryProjectTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall Mcp = MemoryLogins.Mcp;

    async Task<string> Line(string sql, params (string, object?)[] args) => (string)(await MemoryLogins.Scalar(fixture.Source, sql, args))!;

    [PostgresFact] public async Task With_scopes_off_a_project_is_the_households_and_a_private_one_is_refused()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "projects-off");
        await Scoped.Mode(s, "off");
        var made = await Store.CreateProject(who.Alex, new("DEMO Canal walks", Description: " The towpath, written up. ", Aliases: ["Towpath"]), Mcp, default);
        var p = made.Project;
        Assert.Equal(("household", "shared", "demo-canal-walks", "DEMO Canal walks", "The towpath, written up.", "active", "alex", 1, "off"),
            (p.Subject, p.Scope, p.Slug, p.Name, p.Description, p.Status, p.Author, p.Revision, made.Scopes));
        Assert.Equal(["towpath"], p.Aliases);
        Assert.Equal("created|mcp|DEMO client|1|{\"slug\": \"demo-canal-walks\"}",
            await Line("SELECT event||'|'||channel||'|'||client||'|'||revision||'|'||detail::text FROM memory_events WHERE project_id=@id", ("id", p.Id)));
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.CreateProject(who.Alex, new("DEMO private plan", Scope: "private"), Mcp, default)));
        // The household shares everything: every admitted login reads and changes it, today's model.
        Assert.Contains((await Store.Projects(who.Sam, new(), default)), x => x.Id == p.Id);
        Assert.Equal(["name"], (await Store.UpdateProject(who.Sam, p.Id, new(Name: "DEMO Canal walks, by Sam"), null, Mcp, default)).Changed);
        Assert.Equal(["description"], (await Store.UpdateProject(who.App, p.Id, new(Description: "DEMO."), null, new("app"), default)).Changed);
        // Not an AI client or the app: no project writes at all.
        var plugin = await Scoped.Principal(s, "projects-off-plugin", "ingest", plugin: "cgm");
        var legacy = await Scoped.Principal(s, "projects-off-legacy", "legacy");
        foreach (var login in new[] { plugin, legacy })
        {
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.CreateProject(login, new("DEMO refused"), Mcp, default)));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.UpdateProject(login, p.Id, new(Name: "DEMO refused"), null, Mcp, default)));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.DeleteProject(login, p.Id, null, Mcp, default)));
        }
        Assert.Equal(3, (await Store.Projects(who.Alex, new(Name: "towpath"), default)).Single().Revision);
    }

    [PostgresFact] public async Task With_scopes_on_a_project_lands_in_its_writers_scope_unless_the_call_names_Family()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "projects-on");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var written = await Store.CreateProject(who.Alex, new("DEMO Garden plan"), Mcp, default);
            var mine = written.Project;
            Assert.Equal(("alex", "private", "alex", "on"), (mine.Subject, mine.Scope, mine.Author, written.Scopes));
            var family = (await Store.CreateProject(who.Alex, new("DEMO Garden plan", Scope: "Family"), Mcp, default)).Project;
            Assert.Equal(("household", "shared", "alex"), (family.Subject, family.Scope, family.Author));
            Assert.Equal(mine.Slug, family.Slug);
            // A client with no person has no private scope; in Family its project has no person author.
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.CreateProject(who.House, new("DEMO Bins"), Mcp, default)));
            var bins = (await Store.CreateProject(who.House, new("DEMO Bins", Scope: "shared"), Mcp, default)).Project;
            Assert.Equal("household", bins.Subject);
            Assert.Null(bins.Author);
            // Each reader lists its own scopes' projects; Sam never learns Alex's private one.
            Assert.Equal(new[] { family.Id, mine.Id }.Order(), (await Store.Projects(who.Alex, new(Name: "demo-garden-plan"), default)).Select(x => x.Id).Order());
            Assert.Equal([family.Id], (await Store.Projects(who.Sam, new(Name: "demo-garden-plan"), default)).Select(x => x.Id));
            Assert.DoesNotContain(await Store.Projects(who.Sam, new(), default), x => x.Subject == "alex");
            Assert.Equal([family.Id], (await Store.Projects(who.Alex, new(Scope: "shared", Name: "demo-garden-plan"), default)).Select(x => x.Id));
            // Outside Family a person names Family in vain.
            await Scoped.Execute(s, "DELETE FROM memberships WHERE person_id='sam'");
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.CreateProject(who.Sam, new("DEMO Sam's family plan", Scope: "shared"), Mcp, default)));
            Assert.Empty(await Store.Projects(who.Sam, new(Name: "demo-garden-plan"), default));
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    [PostgresFact] public async Task A_slug_or_an_alias_names_one_project_in_each_scope()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "projects-names");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var walks = (await Store.CreateProject(who.Alex, new("DEMO Walks", Slug: "demo-walks", Aliases: ["demo-towpath"]), Mcp, default)).Project;
            Assert.Equal("taken", await MemoryLogins.Refused(() => Store.CreateProject(who.Alex, new("DEMO Walks again", Slug: "demo-walks"), Mcp, default)));
            Assert.Equal("taken", await MemoryLogins.Refused(() => Store.CreateProject(who.Alex, new("DEMO Towpath", Slug: "demo-towpath"), Mcp, default)));
            Assert.Equal("taken", await MemoryLogins.Refused(() => Store.CreateProject(who.Alex, new("DEMO Paths", Aliases: ["demo-walks"]), Mcp, default)));
            var paths = (await Store.CreateProject(who.Alex, new("DEMO Paths"), Mcp, default)).Project;
            Assert.Equal("taken", await MemoryLogins.Refused(() => Store.UpdateProject(who.Alex, paths.Id, new(Aliases: ["demo-towpath"]), null, Mcp, default)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.CreateProject(who.Alex, new("DEMO bad", Slug: "Not A Slug"), Mcp, default)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.CreateProject(who.Alex, new("!!!"), Mcp, default)));
            // Another scope's registry is its own: Family may have the same names.
            Assert.Equal("demo-walks", (await Store.CreateProject(who.Sam, new("DEMO Walks", Slug: "demo-walks", Scope: "shared", Aliases: ["demo-towpath"]), Mcp, default)).Project.Slug);
            // A memory naming the alias joins the project it names, in its own scope.
            Assert.Equal(walks.Id, (await Store.Create(who.Alex, new("DEMO: the towpath at dawn.", Project: "demo-towpath"), Mcp, default)).Project!.Id);
            // Nothing written by a refusal: one created event per project.
            Assert.Equal(3L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_events WHERE project_id IN (SELECT id FROM memory_projects WHERE slug IN ('demo-walks','demo-paths'))"));
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    [PostgresFact] public async Task A_change_changes_only_what_it_names_and_archive_hides_a_project_from_the_list()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "projects-change");
        await Scoped.Mode(s, "off");
        var p = (await Store.CreateProject(who.Alex, new("DEMO Kitchen", Aliases: ["demo-oven"]), Mcp, default)).Project;
        var renamed = await Store.UpdateProject(who.Alex, p.Id, new(Name: "DEMO Kitchen refit", Aliases: ["demo-oven", "demo-tiles"]), p.Revision, Mcp, default);
        Assert.Equal(["name", "aliases"], renamed.Changed);
        Assert.Equal(("demo-kitchen", "DEMO Kitchen refit", 2), (renamed.Project.Slug, renamed.Project.Name, renamed.Project.Revision));
        Assert.Equal(["demo-oven", "demo-tiles"], renamed.Project.Aliases);
        var same = await Store.UpdateProject(who.Alex, p.Id, new(Name: "DEMO Kitchen refit"), null, Mcp, default);
        Assert.True(same.Unchanged);
        Assert.Equal(2, same.Project.Revision);
        // A stale revision is refused with the current one, and nothing changes.
        var stale = await Assert.ThrowsAsync<MemoryRefused>(() => Store.UpdateProject(who.Alex, p.Id, new(Name: "DEMO stale"), 1, Mcp, default));
        Assert.Equal(("conflict", 2), (stale.Code, stale.Revision));
        // Archived: listed only on request, and still taken by a memory that names it (D-145).
        var archived = await Store.UpdateProject(who.Alex, p.Id, new(Status: "archived"), null, Mcp, default);
        Assert.Equal(("archived", 3), (archived.Project.Status, archived.Project.Revision));
        Assert.DoesNotContain(await Store.Projects(who.Sam, new(), default), x => x.Id == p.Id);
        Assert.Contains(await Store.Projects(who.Sam, new(IncludeArchived: true), default), x => x.Id == p.Id);
        Assert.Equal(p.Id, (await Store.Create(who.Sam, new("DEMO: tiles chosen.", Project: "demo-tiles"), Mcp, default)).Project!.Id);
        Assert.Equal("active", (await Store.UpdateProject(who.Alex, p.Id, new(Status: "active"), null, Mcp, default)).Project.Status);
        Assert.Equal("created,updated,archived,restored", await Line(
            "SELECT string_agg(event,',' ORDER BY id) FROM memory_events WHERE project_id=@id AND memory_id IS NULL", ("id", p.Id)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.UpdateProject(who.Alex, p.Id, new(Status: "done"), null, Mcp, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.UpdateProject(who.Alex, p.Id, new(), null, Mcp, default)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.UpdateProject(who.Alex, Guid.NewGuid(), new(Name: "DEMO"), null, Mcp, default)));
    }

    // memory.md section 5: a project changes by its owner (private), its creator or an admin (Family), any reader of Family
    // (a Family project no person made); one the caller does not read answers as an unknown one. Stricter than upstream,
    // whose members may edit or delete any shared project (D-031).
    [PostgresFact] public async Task With_scopes_on_a_project_changes_only_as_section_five_says()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "projects-lock");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        async Task<string> Change(Guid by, Guid project)
        {
            try { await Store.UpdateProject(by, project, new(Description: $"DEMO {Guid.NewGuid():N}"), null, by == who.App ? new("app") : Mcp, default); return "changed"; }
            catch (MemoryRefused refused) { return refused.Code; }
        }
        try
        {
            var mine = (await Store.CreateProject(who.Alex, new("DEMO Alex's plan"), Mcp, default)).Project;
            var shared = (await Store.CreateProject(who.Alex, new("DEMO Shared plan", Scope: "shared"), Mcp, default)).Project;
            var bins = (await Store.CreateProject(who.House, new("DEMO Bins rota", Scope: "shared"), Mcp, default)).Project;
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            Assert.Equal(["unknown", "unknown", "unknown", "changed"], [await Change(who.Sam, mine.Id), await Change(who.House, mine.Id), await Change(who.App, mine.Id), await Change(who.Alex, mine.Id)]);
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DeleteProject(who.Sam, mine.Id, null, Mcp, default)));
            await Scoped.Execute(s, "UPDATE memberships SET role='member' WHERE person_id='sam'");
            Assert.Equal(["forbidden", "forbidden", "forbidden", "changed"], [await Change(who.Sam, shared.Id), await Change(who.House, shared.Id), await Change(who.App, shared.Id), await Change(who.Alex, shared.Id)]);
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.DeleteProject(who.Sam, shared.Id, null, Mcp, default)));
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            Assert.Equal("changed", await Change(who.Sam, shared.Id));
            Assert.Equal(["changed", "changed", "changed", "changed"], [await Change(who.Alex, bins.Id), await Change(who.Sam, bins.Id), await Change(who.House, bins.Id), await Change(who.App, bins.Id)]);
            // Out of Family, its creator reads it no more: as for an unknown project.
            await Scoped.Execute(s, "DELETE FROM memberships WHERE person_id='alex'");
            Assert.Equal("unknown", await Change(who.Alex, shared.Id));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DeleteProject(who.Alex, shared.Id, null, Mcp, default)));
            await MemoryLogins.Family(s);
            Assert.Equal(0, (await Store.DeleteProject(who.Alex, shared.Id, null, Mcp, default)).Memories);
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    // Deleting a project removes the grouping only (memory.md section 5, as upstream): each memory in it leaves it at a new
    // revision, keeping the state it had as a version, its text and passages untouched; one in its capsule slots leaves
    // those; one tagged with an alias keeps its tags.
    [PostgresFact] public async Task Deleting_a_project_keeps_its_memories()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "projects-delete");
        await Scoped.Mode(s, "off");
        var p = (await Store.CreateProject(who.Alex, new("DEMO Shed", Aliases: ["demo-shed-door"]), Mcp, default)).Project;
        var inside = (await Store.Create(who.Alex, new("DEMO: the shed roof needs felt.", Project: "demo-shed"), Mcp, default)).Id;
        var tagged = (await Store.Create(who.Alex, new("DEMO: the shed door sticks.", ["demo-shed-door"]), Mcp, default)).Id;
        var slot = (await Store.Create(who.Alex, new("DEMO: shed state, half painted.", Project: "demo-shed"), Mcp, default)).Id;
        await Scoped.Execute(s, "UPDATE memories SET capsule='project',capsule_slot='current-state' WHERE id=@id", ("id", slot));
        var stale = await Assert.ThrowsAsync<MemoryRefused>(() => Store.DeleteProject(who.Alex, p.Id, p.Revision + 1, Mcp, default));
        Assert.Equal(("conflict", 1), (stale.Code, stale.Revision));
        var gone = await Store.DeleteProject(who.Sam, p.Id, p.Revision, Mcp, default);
        Assert.Equal((p.Id, "demo-shed", 2), (gone.Id, gone.Slug, gone.Memories));
        Assert.Empty(await Store.Projects(who.Alex, new(IncludeArchived: true, Name: "demo-shed"), default));
        Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_project_aliases WHERE project_id=@id", ("id", p.Id)));
        foreach (var id in new[] { inside, slot })
        {
            var m = (await Store.Get(who.Alex, id, default))!;
            Assert.Null(m.Project);
            Assert.Equal(2, m.Revision);
            Assert.StartsWith("DEMO: ", m.Content);
            Assert.Equal($"1|{p.Id}|1", await Line(
                "SELECT revision||'|'||(state->>'project_id')||'|'||(SELECT text_revision FROM memories WHERE id=@id) FROM memory_versions WHERE memory_id=@id", ("id", id)));
        }
        Assert.Equal("||", await Line("SELECT coalesce(capsule,'')||'|'||coalesce(capsule_slot,'')||'|'||coalesce(project_id::text,'') FROM memories WHERE id=@id", ("id", slot)));
        Assert.Equal("updated|[\"project\", \"capsule\"]|project_deleted", await Line(
            "SELECT event||'|'||(detail->'fields')::text||'|'||(detail->>'cause') FROM memory_events WHERE memory_id=@id ORDER BY id DESC LIMIT 1", ("id", slot)));
        var kept = (await Store.Get(who.Alex, tagged, default))!;
        Assert.Equal(1, kept.Revision);
        Assert.Equal(["demo-shed-door"], kept.Tags);
        Assert.Equal("deleted|2", await Line(
            "SELECT event||'|'||(detail->>'memories') FROM memory_events WHERE project_id=@id AND memory_id IS NULL ORDER BY id DESC LIMIT 1", ("id", p.Id)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DeleteProject(who.Alex, p.Id, null, Mcp, default)));
    }

    // A memory write naming a project that is being deleted at the same moment waits for the delete and makes the project
    // anew, never a foreign-key error: memory_project key-share locks the project it finds.
    [PostgresFact] public async Task A_write_naming_a_project_being_deleted_makes_it_anew()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "projects-race");
        await Scoped.Mode(s, "off");
        var p = (await Store.CreateProject(who.Alex, new("DEMO Race shed"), Mcp, default)).Project;
        await using (var deleting = await s.OpenConnectionAsync())
        await using (var tx = await deleting.BeginTransactionAsync())
        {
            await using (var cmd = Database.Command(deleting, tx, "SELECT delete_memory_project(@p,@id,NULL,'mcp')", ("p", who.Alex), ("id", p.Id)))
                await cmd.ExecuteScalarAsync();
            var write = Store.Create(who.Sam, new("DEMO: the race shed's key is on the hook.", Project: p.Slug), Mcp, default);
            Assert.NotSame(write, await Task.WhenAny(write, Task.Delay(500)));
            await tx.CommitAsync();
            var made = await write;
            Assert.NotNull(made.Project);
            Assert.NotEqual(p.Id, made.Project!.Id);
            Assert.Equal((p.Slug, true), (made.Project.Slug, made.Project.Created));
        }
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_projects WHERE slug=@s", ("s", p.Slug)));
    }

    // CTX-07: a project is found by its slug or an alias, and its members are the memories in it and those of its scope
    // tagged with an alias (upstream's membership by tag, adopted without rewriting the tags); a held memory never by
    // either (D-149). An unknown name lists up to 10 of the reader's own known ones.
    [PostgresFact] public async Task An_alias_finds_its_projects_memories_and_an_unknown_name_lists_known_ones()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "projects-alias");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var p = (await Store.CreateProject(who.Alex, new("DEMO Allotment", Aliases: ["demo-veg"], Scope: "shared"), Mcp, default)).Project;
            var inside = (await Store.Create(who.Alex, new("DEMO: the allotment shed.", Scope: "shared", Project: "demo-allotment"), Mcp, default)).Id;
            var tagged = (await Store.Create(who.Sam, new("DEMO: beans sown.", ["demo-veg"], Scope: "shared"), Mcp, default)).Id;
            var other = (await Store.Create(who.Alex, new("DEMO: Alex's own beans.", ["demo-veg"]), Mcp, default)).Id;
            var held = (await Store.Create(who.Sam, new("DEMO: held veg note.", ["demo-veg"], Scope: "shared"), Mcp, default)).Id;
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", held));
            async Task<Guid[]> Members(Guid reader, string name) =>
                [.. (await Store.List(reader, new(Project: name, Limit: 50), default)).Items.Select(i => i.Id).Order()];
            Assert.Equal(new[] { inside, tagged }.Order(), await Members(who.Sam, "demo-veg"));
            Assert.Equal(new[] { inside, tagged }.Order(), await Members(who.Sam, "demo-allotment"));
            // Alex's private memory tagged demo-veg is in no Family project, and Alex has no private project of that name.
            Assert.Equal(new[] { inside, tagged }.Order(), await Members(who.Alex, "demo-veg"));
            var unknown = await Assert.ThrowsAsync<MemoryRefused>(() => Store.List(who.Sam, new(Project: "demo-nothing"), default));
            Assert.Equal("unknown_project", unknown.Code);
            Assert.Contains("demo-allotment", unknown.Known!);
            Assert.True(unknown.Known!.Count <= 10);
            // Alex's private project is never among the names Sam is told.
            await Store.CreateProject(who.Alex, new("DEMO Alex surprise"), Mcp, default);
            Assert.DoesNotContain("demo-alex-surprise", (await Assert.ThrowsAsync<MemoryRefused>(() => Store.List(who.Sam, new(Project: "demo-nothing"), default))).Known!);
            Assert.Contains("demo-alex-surprise", (await Assert.ThrowsAsync<MemoryRefused>(() => Store.List(who.Alex, new(Project: "demo-nothing"), default))).Known!);
            Assert.NotEqual(Guid.Empty, other);
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }
}

// Every project writer takes the recorded mode's row FOR SHARE before it writes (memory.md 6.2), so rhythm scopes off
// never interleaves with one, in a database of its own: the switch then refuses only for the project the race made.
public sealed class MemoryProjectScopesOffLockTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task Every_project_writer_waits_for_rhythm_scopes_off_and_the_switch_for_one()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "projects-switch");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var timing = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB"))
        { Database = fixture.Name, Options = "-c lock_timeout=300" }.ConnectionString);
        try
        {
            Assert.Empty((await new Principals(s).OffPreview(default)).Private);
            await using (var writer = await s.OpenConnectionAsync())
            await using (var tx = await writer.BeginTransactionAsync())
            {
                await using (var cmd = Database.Command(writer, tx, "SELECT create_memory_project(@p,'{\"slug\":\"demo-race\",\"name\":\"DEMO race\"}','mcp')", ("p", who.Sam)))
                    await cmd.ExecuteScalarAsync();
                var off = new Principals(s).SwitchOff(default);
                Assert.NotSame(off, await Task.WhenAny(off, Task.Delay(500)));
                await tx.CommitAsync();
                Assert.Equal([("sam", "private memory projects", 1L)], await off);
            }
            Assert.Equal("on", await MemoryLogins.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
            var store = new MemoryStore(s);
            var family = (await store.CreateProject(who.Alex, new("DEMO family race", Scope: "shared"), MemoryLogins.Mcp, default)).Project;
            var waiting = new MemoryStore(timing);
            await using (var hold = await s.OpenConnectionAsync())
            await using (var tx = await hold.BeginTransactionAsync())
            {
                await Database.Execute(hold, tx, "SELECT 1 FROM install_settings WHERE key='memory.scopes' FOR UPDATE", default);
                foreach (var write in new Func<Task>[] {
                    () => waiting.CreateProject(who.Alex, new("DEMO waits"), MemoryLogins.Mcp, default),
                    () => waiting.UpdateProject(who.Alex, family.Id, new(Name: "DEMO waits"), null, MemoryLogins.Mcp, default),
                    () => waiting.DeleteProject(who.Alex, family.Id, null, MemoryLogins.Mcp, default) })
                    Assert.Equal("55P03", (await Assert.ThrowsAsync<PostgresException>(write)).SqlState);
                await tx.RollbackAsync();
            }
            Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_projects WHERE slug='demo-waits'"));
        }
        finally
        {
            await timing.DisposeAsync();
            await Scoped.Mode(s, "off");
        }
    }
}

// A map view of one project's members (API-24): the project's star, the stars in it (an in_project link to it) and what
// they link to, as upstream's project graph seeds from the edges with a member at one end; nothing for an unknown star
// or one that is not a project. Note and Second Brain projects are stars of their adapters, as before.
internal sealed class ProjectNotes : IMemorySource
{
    public const string Provider = "notes-projects";
    public string Name => Provider;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.AuthorizedArchive, "household") };
    public static ResourceKey Key(string id) => new("household", Provider, Provider, id);
    public Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        Reference In(string project) => new(Key(project), null, "notes:folder", "In folder", "in_project", true, "verified", 1, true);
        DiscoveryRecord Note(string id, string title, params Reference[] links) => new(Key(id), "shared", "note", title, null, "r1", null, now, links);
        return Task.FromResult(new SourceListing([
            new(Key("folder:garden"), "shared", "project", "garden", null, "p1", null, now, []),
            new(Key("folder:kitchen"), "shared", "project", "kitchen", null, "p1", null, now, []),
            Note("garden/beans.md", "DEMO Beans", In("folder:garden"), new(Key("shopping.md"), null, "text:1")),
            Note("garden/shed.md", "DEMO Shed", In("folder:garden")),
            Note("kitchen/oven.md", "DEMO Oven", In("folder:kitchen")),
            Note("shopping.md", "DEMO Shopping"),
            Note("loose.md", "DEMO Loose")], true));
    }
    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) => Task.FromResult<string?>(null);
}

[Collection(SharedStateCollection.Name)]
public sealed class ProjectMapTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_projects_view_holds_its_members_and_what_they_link_to()
    {
        var config = TestSettings.Use();
        var registry = new AdapterRegistry([new ProjectNotes()]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
        try
        {
            Assert.Equal("complete", await host.Refresh(false, default));
            string Id(string id) => ProjectNotes.Key(id).GraphId;
            async Task<string[]> View(string star) => [.. (await host.Read(app, null, null, default, new GraphQuery("project", star))).nodes.Select(n => n.id)];
            var garden = await View(Id("folder:garden"));
            Assert.Equal(Id("folder:garden"), garden[0]);
            Assert.Equal(new[] { Id("garden/beans.md"), Id("garden/shed.md") }.Order(), garden[1..3].Order());
            Assert.Equal([Id("shopping.md")], garden[3..]);
            var page = await host.Read(app, null, null, default, new GraphQuery("project", Id("folder:garden")));
            Assert.Contains(page.edges, e => e.source == Id("garden/beans.md") && e.target == Id("shopping.md"));
            Assert.All(page.edges, e => Assert.Contains(e.source, garden));
            Assert.Equal([Id("folder:kitchen"), Id("kitchen/oven.md")], await View(Id("folder:kitchen")));
            // Not a project, or no star at all: nothing.
            Assert.Empty(await View(Id("garden/beans.md")));
            Assert.Empty(await View(MapAnswers.Unknown));
            // Paged as every operation is.
            var first = await host.Read(app, null, null, default, new GraphQuery("project", Id("folder:garden")));
            Assert.Null(first.nextCursor);
        }
        finally { Directory.Delete(state, true); }
    }
}
