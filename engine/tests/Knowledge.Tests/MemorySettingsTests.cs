using System.Net;
using System.Text.Json;
using Knowledge.Cli;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Knowledge.Tests;

// The safe defaults and settings of SB-P1-09 (docs/dev/memory.md section 6): memory.scopes off unless rhythm.yaml says
// on, on refused until phase 1's exit, a wrong value refused at start naming it, and the defaults new memories take.
// Every person and value is fictional.
public sealed class MemorySettingsTests
{
    const string Head = "version: 1\ntimezone: UTC\npeople: [{ id: alex, name: Alex }, { id: sam, name: Sam }]\n";
    static readonly string Example = Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml");

    [Fact] public void Memory_scopes_are_off_unless_the_settings_say_on()
    {
        // An install whose rhythm.yaml predates the key (TestSettings' is one) keeps the model it had: off.
        foreach (var config in new[]
        {
            TestSettings.Use(), RhythmConfig.Parse(Head), RhythmConfig.Load(Example), RhythmConfig.Parse(Head + "memory:\n"),
            RhythmConfig.Parse(Head + "memory: {}\n"), RhythmConfig.Parse(Head + "memory: { scopes: off }\n"),
            RhythmConfig.Parse(Head + "memory: { scopes: \"off\" }\n"), RhythmConfig.Parse(Head + "memory: { scopes: null }\n"),
        })
            Assert.False(MemorySettings.From(config).Scopes);
        // The example says so in words, for rhythm init to copy.
        Assert.Contains("\nmemory:", File.ReadAllText(Example));
        Assert.Contains("  scopes: off ", File.ReadAllText(Example));
    }

    [Theory]
    [InlineData("memory: { scopes: true }", "memory.scopes is off or on")]
    [InlineData("memory: { scopes: false }", "memory.scopes is off or on")]
    [InlineData("memory: { scopes: yes }", "memory.scopes is off or on")]
    [InlineData("memory: { scopes: On }", "memory.scopes is off or on")]
    [InlineData("memory: { scopes: private }", "memory.scopes is off or on")]
    [InlineData("memory: { scopes: 1 }", "memory.scopes is off or on")]
    [InlineData("memory: { scopes: [on] }", "memory.scopes is off or on")]
    [InlineData("memory: { scopes: { on: true } }", "memory.scopes is off or on")]
    [InlineData("memory: { scope: off }", "memory.scope is not a setting")]
    [InlineData("memory: { defaultScope: family }", "memory.defaultScope is not a setting")]
    [InlineData("memory: { share: true }", "memory.share is off or on")]
    [InlineData("memory: { share: yes }", "memory.share is off or on")]
    [InlineData("memory: { share: On }", "memory.share is off or on")]
    [InlineData("memory: { share: [on] }", "memory.share is off or on")]
    [InlineData("memory: { sharing: on }", "memory.sharing is not a setting")]
    [InlineData("memory: off", "memory is a section")]
    [InlineData("memory: [scopes]", "memory is a section")]
    public void A_wrong_memory_setting_fails_at_start_naming_it(string line, string expected)
    {
        var e = Assert.Throws<ConfigException>(() => RhythmConfig.Parse(Head + line + "\n"));
        Assert.Contains(expected, e.Message);
    }

    [Fact] public void On_is_accepted_since_phase_one_exited_and_off_is_unchanged()
    {
        // Phase 1's exit (docs/dev/memory.md 6.4, D-131): the settings load takes on, so check-config, doctor and a start
        // accept it. Off, and a rhythm.yaml without the key (every existing install's), read as off, as before.
        Assert.True(MemorySettings.ScopesAccepted);
        Assert.True(MemorySettings.From(RhythmConfig.Parse(Head + "memory: { scopes: on }\n")).Scopes);
        Assert.Null(new MemorySettings(true).Refusal());
        Assert.False(MemorySettings.From(RhythmConfig.Parse(Head + "memory: { scopes: off }\n")).Scopes);
        Assert.False(MemorySettings.From(RhythmConfig.Parse(Head)).Scopes);
        Assert.Null(MemorySettings.Off.Refusal());
        Assert.Null(MemorySettings.Off.Refusal(accepted: false));
        // What a version before the exit said, which a test still reaches by asking as that version.
        var before = new MemorySettings(true).Refusal(accepted: false)!;
        Assert.StartsWith("memory.scopes: on is refused by this version", before);
        Assert.Contains("docs/dev/memory.md section 6.4", before);
    }

    [Fact] public void A_memory_lands_private_by_default_when_scopes_are_on()
    {
        // Scopes on: the writer's own private scope unless the call names Family (D-021); upstream's names are aliases.
        Assert.Equal(new("alex", "private", null), MemorySettings.Lands(true, "alex", null));
        Assert.Equal(new("alex", "private", null), MemorySettings.Lands(true, "alex", "personal"));
        Assert.Equal(new("household", "shared", null), MemorySettings.Lands(true, "alex", "Family"));
        Assert.Equal(new("household", "shared", null), MemorySettings.Lands(true, "sam", "company"));
        // A client that speaks for no one has no private scope: Family only, and only when it says so.
        Assert.Contains("no person", MemorySettings.Lands(true, null, null).Refusal);
        Assert.Contains("no person", MemorySettings.Lands(true, null, "private").Refusal);
        Assert.Equal(new("household", "shared", null), MemorySettings.Lands(true, null, "shared"));
        // Scopes off: a household memory, visible to everyone, whoever writes it; a private one is refused (D-048).
        foreach (var writer in new[] { "alex", null })
        {
            Assert.Equal(new("household", "shared", null), MemorySettings.Lands(false, writer, null));
            Assert.Equal(new("household", "shared", null), MemorySettings.Lands(false, writer, "shared"));
            Assert.Contains("wait until per-person scopes are on", MemorySettings.Lands(false, writer, "private").Refusal);
        }
        Assert.Throws<ArgumentException>(() => MemorySettings.Lands(true, "alex", "team"));
    }

    [Fact] public void What_an_AI_client_saves_is_a_draft_and_no_capture_lane_is_on()
    {
        Assert.Equal("draft", MemorySettings.NewStatus);
        // Capture lanes: none is built, and each one to come is an adapter, off unless its section says enabled. The
        // adapters a new install has on are the local ones below; a new adapter on by default fails here.
        var example = RhythmConfig.Load(Example);
        Assert.Equal(["notes", "csv", "documents", "embeddings"], example.Adapters.Keys.Where(example.AdapterEnabled));
        Assert.DoesNotContain(example.Plugins.Values, p => p.Enabled);
    }

    [Fact] public void The_settings_schema_takes_off_or_on_and_nothing_else_under_memory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "contracts", "rhythm.schema.json");
        var memory = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("properties").GetProperty("memory");
        Assert.False(memory.GetProperty("additionalProperties").GetBoolean());
        // The keys the engine takes under memory (MemorySettings.Keys, which its refusal lists), each off, on or empty, off
        // by default: a key the schema leaves out would be flagged in a file the engine takes.
        var keys = memory.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["scopes", "capsules", "tools", "share", "secondBrainImport", "capture"], keys);
        var refused = Assert.Throws<ConfigException>(() => MemorySettings.From(RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { nosuch: on }\n")));
        Assert.EndsWith("memory takes " + string.Join(", ", keys), refused.Message);
        foreach (var name in keys)
            foreach (var value in new[] { "off", "on", "" })
                MemorySettings.From(RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { " + name + ": " + value + " }\n"));
        // An empty memory section and an empty value read as off, as the engine reads them (MemorySettings.From).
        Assert.Equal(["object", "null"], memory.GetProperty("type").EnumerateArray().Select(v => v.GetString()));
        foreach (var name in keys)
        {
            var key = memory.GetProperty("properties").GetProperty(name);
            Assert.Equal(["off", "on", null], key.GetProperty("enum").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? null : v.GetString()));
            Assert.Equal("off", key.GetProperty("default").GetString());
        }
        // memory.capsules: on, the schema's other value, is what the engine takes too.
        Assert.True(MemorySettings.CapsulesOn(RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { capsules: on }\n")));
        Assert.False(MemorySettings.CapsulesOn(RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { capsules: }\n")));
        // memory.tools: on likewise (SB-P3-09); empty is off.
        Assert.True(MemorySettings.ToolsOn(RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { tools: on }\n")));
        Assert.False(MemorySettings.ToolsOn(RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { tools: }\n")));
        // memory.share: on (SB-P3-10) counts only beside memory.tools: on; empty, off or alone it is off.
        Assert.True(MemorySettings.ShareOn(RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { tools: on, share: on }\n")));
        foreach (var section in new[] { "share: on", "tools: off, share: on", "tools: on, share:", "tools: on, share: off", "tools: on" })
            Assert.False(MemorySettings.ShareOn(RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { " + section + " }\n")), section);
        Assert.False(MemorySettings.ShareOn(TestSettings.Use()));
        Assert.False(MemorySettings.ShareOn(RhythmConfig.Load(Example)));
    }
}

// The start-time checks and the switch's first start (Principals.Install, docs/dev/memory.md sections 6.3 and 6.4),
// doctor's memory scope reports, and person-bound tokens (SB-P1-12: rhythm token and rhythm person, section 3), on a
// throwaway database of their own.
public sealed class MemorySettingsDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IDisposable
{
    readonly DeployFolder deploy = new(Path.Combine(Path.GetTempPath(), "rhythm-tokens-" + Guid.NewGuid().ToString("N")[..10]));
    public void Dispose() { try { Directory.Delete(deploy.Root, true); } catch (IOException) { } }

    // Settings with scopes on, as the switch reads them (accepted since phase 1's exit), built as the parser would.
    static RhythmConfig ScopesOn(params string[] people) => new()
    {
        Timezone = "Europe/London",
        People = [.. people.Select(p => new RhythmConfig.Person { Id = p, Name = char.ToUpperInvariant(p[0]) + p[1..] })],
        Memory = new Dictionary<object, object?> { ["scopes"] = "on" },
    };

    [PostgresFact] public async Task The_first_start_with_scopes_on_records_it_and_adds_everyone_then_in_people_to_Family()
    {
        var s = fixture.Source;
        var principals = new Principals(s);
        await Scoped.Execute(s, "DELETE FROM memberships");
        await Scoped.Execute(s, "DELETE FROM install_settings WHERE key=@k", ("k", Principals.JoinedKey));
        try
        {
            // A version before phase 1's exit refused it, whatever the database records, and changed nothing.
            Assert.StartsWith("memory.scopes: on is refused", (await Assert.ThrowsAsync<ConfigException>(() => principals.Install(ScopesOn("alex", "sam"), default, acceptScopesOn: false))).Message);
            Assert.Equal("off", await Scoped.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
            Assert.Equal(0L, await Scoped.Scalar(s, "SELECT count(*) FROM memberships"));

            // Accepted since phase 1's exit, with no test override: the first start records on and adds everyone in
            // people, a suspended person included (who reads nothing while suspended), and says so for the log.
            await Scoped.Execute(s, "UPDATE subjects SET status='suspended' WHERE id='sam'");
            var first = await principals.Install(ScopesOn("alex", "sam"), default);
            Assert.Equal(new Principals.Started(0, 0, 0, true, 2, 0), first with { Tokens = 0 });
            Assert.Equal("on", await Scoped.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
            Assert.Equal(2L, await Scoped.Scalar(s, "SELECT count(*) FROM memberships WHERE person_id IN ('alex','sam') AND role='member'"));
            Assert.Equal("suspended", await Scoped.Scalar(s, "SELECT status FROM subjects WHERE id='sam'"));

            // A later start adds nobody: a person listed afterwards joins Family only when the operator adds them.
            Assert.Equal(new Principals.Started(0, 0, 0, false, 0, 0), await principals.Install(ScopesOn("alex", "sam", "jo"), default, acceptScopesOn: true));
            Assert.Equal(0L, await Scoped.Scalar(s, "SELECT count(*) FROM memberships WHERE person_id='jo'"));
            Assert.Equal("current", await Scoped.Scalar(s, "SELECT status FROM subjects WHERE id='jo'"));

            // rhythm.yaml lost, or an older copy without the key: off against a database that records on stops the start.
            var off = await Assert.ThrowsAsync<ConfigException>(() => principals.Install(TestSettings.Use(), default, acceptScopesOn: true));
            Assert.Contains("Set memory.scopes: on again", off.Message);
            // So does this version with no override; a version before phase 1's exit, which ran off only, refused such a
            // database whatever rhythm.yaml said.
            Assert.Contains("Set memory.scopes: on again", (await Assert.ThrowsAsync<ConfigException>(() => principals.Install(TestSettings.Use(), default))).Message);
            Assert.Contains("runs with scopes off only", (await Assert.ThrowsAsync<ConfigException>(() => principals.Install(TestSettings.Use(), default, acceptScopesOn: false))).Message);
            Assert.Equal("on", await Scoped.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));

            // Switched off on purpose and on again, the second switch-on adds nobody: someone the operator took out of
            // Family stays out (section 6.3: only the switch's first start adds people).
            await Scoped.Execute(s, "UPDATE subjects SET status='current' WHERE id='sam'");
            Assert.Equal("member", (await principals.Leave("sam", default)).Role);
            Assert.Empty(await principals.SwitchOff(default));
            await principals.Install(new RhythmConfig
            {
                Timezone = "Europe/London",
                People = [.. new[] { "alex", "sam", "jo" }.Select(p => new RhythmConfig.Person { Id = p, Name = char.ToUpperInvariant(p[0]) + p[1..] })],
                Memory = new Dictionary<object, object?> { ["scopes"] = "off" },
            }, default);
            var again = await principals.Install(ScopesOn("alex", "sam", "jo"), default, acceptScopesOn: true);
            Assert.Equal((true, 0), (again.SwitchedOn, again.Joined));
            Assert.Null(await Scoped.Scalar(s, "SELECT role FROM memberships WHERE person_id='sam'"));
            Assert.Equal("member", await Scoped.Scalar(s, "SELECT role FROM memberships WHERE person_id='alex'"));
        }
        finally
        {
            await Scoped.Mode(s, "off");
            await Scoped.Execute(s, "DELETE FROM install_settings WHERE key=@k", ("k", Principals.JoinedKey));
            await Scoped.Execute(s, "DELETE FROM memberships");
            await Scoped.Execute(s, "UPDATE subjects SET status='current' WHERE id='sam'");
        }
        Assert.Equal(new Principals.Started(1, 0, 0, false, 0, 0), (await principals.Install(TestSettings.Use(), default)) with { Tokens = 0 });
    }

    [PostgresFact] public async Task A_database_without_the_scope_tables_is_refused_at_start_naming_them()
    {
        var server = Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")!;
        var name = "rhythm_test_bare_" + Guid.NewGuid().ToString("N")[..10];
        await using (var admin = NpgsqlDataSource.Create(server))
        await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var bare = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
            foreach (var config in new[] { TestSettings.Use(), ScopesOn("alex") })
                Assert.Contains("no scope tables yet (migration 023)",
                    (await Assert.ThrowsAsync<ConfigException>(() => new Principals(bare).Install(config, default, acceptScopesOn: true))).Message);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = NpgsqlDataSource.Create(server);
            await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact] public async Task A_new_memory_is_a_draft_in_the_table_too()
    {
        var column = await Scoped.Scalar(fixture.Source,
            "SELECT column_default FROM information_schema.columns WHERE table_name='memories' AND column_name='status'");
        Assert.StartsWith($"'{MemorySettings.NewStatus}'", (string)column!);
    }

    [PostgresFact] public async Task Doctor_reports_the_mode_shares_and_what_each_token_would_stop_reading_and_changes_nothing()
    {
        Directory.CreateDirectory(deploy.Root);
        var s = fixture.Source;
        Assert.Equal(0, InitCommand.Run(deploy, ["--no-input", "--people", "Alex, Sam"], new Prompter(new StringReader(""), new StringWriter(), true, false)));
        await Scoped.Row(s, "alex", "notes", "private", "doctor-alex");
        await Scoped.Row(s, "sam", "csv", "private", "doctor-sam");
        await Scoped.Row(s, "household", "notes", "shared", "doctor-family");
        var house = await Scoped.Principal(s, "doctor-house", "harness");
        await Scoped.Principal(s, "doctor-alex", "harness", "alex");
        // Alex's terminal login (044), as rhythm remember makes it: no token exists for it, so it is counted apart.
        var terminal = await TerminalLogin.For(s, "alex", default);
        var terminalName = (string)(await Scoped.Scalar(s, "SELECT name FROM principals WHERE id=@id", ("id", terminal)))!;
        await Scoped.Execute(s, "INSERT INTO source_shares(person_id,provider) VALUES('sam','csv')");
        // Viewer sessions of Alex's (app logins bound to her, D-070): one within its 30 days, refused while app.viewers is
        // off, and one past them, refused whatever the setting. Neither reads anything now, so neither has anything to lose.
        await Scoped.Principal(s, "doctor-viewer-now", "app", "alex");
        var lapsed = await Scoped.Principal(s, "doctor-viewer-old", "app", "alex");
        await Scoped.Execute(s, "UPDATE principals SET created_at=now()-interval '31 days' WHERE id=@id", ("id", lapsed));
        // Family dashboards made by the household's client, each version's log feed as saved: naming the household, naming
        // the first person (as one saved with scopes off whose log named none is), naming none (from before 027), and one
        // re-saved: its first version Alex's log, its newest the household's. The newest version decides.
        var boards = new List<Guid>();
        async Task<Guid> Board(string title, params string[] logs)
        {
            var id = Guid.NewGuid();
            boards.Add(id);
            await Scoped.Execute(s, "INSERT INTO dashboards(id,created_by) VALUES(@id,@by)", ("id", id), ("by", house));
            for (var v = 0; v < logs.Length; v++)
                await Scoped.Execute(s, """
                    INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,note,saved_by,writes)
                    VALUES(@id,@v,@t,'DEMO','<p>DEMO</p>',@f::jsonb,'[{"star":"demo","why":"DEMO"}]','DEMO version.',@by,@w::jsonb)
                    """, ("id", id), ("v", v + 1), ("t", title), ("by", house),
                    ("f", $$"""[{"name":"checks","kind":"log","log":"home-checks"{{logs[v]}}}]"""),
                    ("w", $$"""[{"kind":"log","log":"home-checks"{{logs[v]}}}]"""));
            return id;
        }
        await Board("DEMO · Family log, named", ",\"subject\":\"household\"");
        var first = await Board("DEMO · Family log, first person", ",\"subject\":\"alex\"");
        var unnamed = await Board("DEMO · Family log, unnamed", "");
        await Board("DEMO · Family log, re-saved", ",\"subject\":\"alex\"", ",\"subject\":\"household\"");
        // A token still enabled but bound to someone taken out of people: refused at the door, and pointed out.
        await Scoped.Execute(s, "INSERT INTO subjects(id,kind) VALUES('jo','person') ON CONFLICT DO NOTHING");
        await Scoped.Execute(s, "UPDATE subjects SET status='former' WHERE id='jo'");
        await Scoped.Principal(s, "doctor-jo", "harness", "jo");
        StackCommands.Probe up = (_, url, _) => Task.FromResult<int?>(url.EndsWith("/mcp", StringComparison.Ordinal) ? 401 : 200);
        StackCommands.Probe down = (_, _, _) => Task.FromResult<int?>(null);
        async Task<(int Code, string Output)> Doctor(StackCommands.Probe? probe = null)
        {
            var o = new StringWriter();
            var code = await DoctorCommand.Run(deploy, o, probe ?? up, Path.Combine(deploy.Root, "no-descriptors"), default, () => s);
            return (code, o.ToString());
        }
        try
        {
            var (code, output) = await Doctor();
            Assert.Equal(0, code);
            Assert.Contains("ok    memory scopes are off in rhythm.yaml and the database: the household shares everything", output);
            Assert.Contains("; 1 terminal login(s) for rhythm remember, recall and list, which hold no token", output);
            Assert.Contains("note  source share: sam's csv records are read by Family while memory scopes are on", output);
            Assert.Contains("note  token doctor-jo speaks for jo, who is former: it is refused; ./rhythm token revoke doctor-jo", output);
            // The preview counts per person and provider, never a title. The household view keeps Family and Sam's
            // shared csv and loses Alex's private note. Alex's own client keeps her note, Family and Sam's shared csv
            // (she is a member once the switch adds everyone), so it loses nothing.
            Assert.Contains("note  with memory scopes on, harness token doctor-house (household view) would stop reading alex: notes 1", output);
            Assert.DoesNotContain("doctor-house (household view) would stop reading alex: notes 1; sam", output);
            Assert.DoesNotContain("token doctor-alex (speaks for alex) would stop reading", output);
            Assert.DoesNotContain("DEMO · scope fixture", output);
            // Viewer sessions refused at the door are pointed out, and left out of the preview.
            Assert.Contains("note  viewer session doctor-viewer-old of alex is past its 30 days: it is refused; ./rhythm viewer list disables it", output);
            Assert.Contains("viewer session(s) still enabled: with app.viewers off they are refused at the door", output);
            Assert.DoesNotContain("token doctor-viewer", output);
            // The Family dashboards that would leave the household view at the switch, by their newest version's log, with
            // their ids and titles (Family's own); the one naming the household and the one re-saved so are not among them.
            Assert.Contains("Family dashboard(s) would be read only by the person their log names, or by no one when it names none", output);
            Assert.Contains($"{first} \"DEMO · Family log, first person\" (log: alex)", output);
            Assert.Contains($"{unnamed} \"DEMO · Family log, unnamed\" (log: no subject)", output);
            Assert.DoesNotContain("DEMO · Family log, named", output);
            Assert.DoesNotContain("DEMO · Family log, re-saved", output);
            // Nothing was recorded: the switch ran inside a transaction that was rolled back.
            Assert.Equal("off", await Scoped.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
            Assert.Equal(0L, await Scoped.Scalar(s, "SELECT count(*) FROM memberships"));
            // Once scopes have been on (the first switch-on recorded) a later switch-on adds nobody, and the preview
            // says so: Alex, out of Family, would stop reading Family's records.
            await Scoped.Execute(s, "INSERT INTO install_settings(key,value) VALUES(@k,'yes') ON CONFLICT DO NOTHING", ("k", Principals.JoinedKey));
            (code, output) = await Doctor();
            Assert.Equal(0, code);
            Assert.Contains("note  with memory scopes on, harness token doctor-alex (speaks for alex) would stop reading household: notes", output);
            Assert.Contains($"note  with memory scopes on, terminal login {terminalName} (speaks for alex) would stop reading household: notes", output);
            Assert.DoesNotContain($"harness token {terminalName}", output);
            await Scoped.Execute(s, "DELETE FROM install_settings WHERE key=@k", ("k", Principals.JoinedKey));

            // A database that records another mode than rhythm.yaml stops the engine's start, so doctor fails on it. It
            // reads the database itself: on a real stack the engine is then down (it does not start), and Postgres is not.
            await Scoped.Mode(s, "on");
            (code, output) = await Doctor();
            Assert.Equal(1, code);
            Assert.Contains("FAIL  the database records memory.scopes on and rhythm.yaml says off", output);
            (code, output) = await Doctor(down);
            Assert.Equal(1, code);
            Assert.Contains("skip  the stack is not running", output);
            Assert.Contains("FAIL  the database records memory.scopes on and rhythm.yaml says off", output);
        }
        finally
        {
            await Scoped.Mode(s, "off");
            await Scoped.Execute(s, "DELETE FROM install_settings WHERE key=@k", ("k", Principals.JoinedKey));
            await Scoped.Execute(s, "DELETE FROM source_shares");
            await Scoped.Execute(s, "DELETE FROM resources WHERE external_id LIKE 'doctor-%'");
            await Scoped.Execute(s, "UPDATE principals SET enabled=false WHERE name='doctor-jo' OR name LIKE 'doctor-viewer-%' OR id=@t", ("t", terminal));
            await Scoped.Execute(s, "DELETE FROM dashboards WHERE id=ANY(@ids)", ("ids", boards.ToArray()));
        }
        // The stack down and no database to reach: the scope checks are skipped, which is no failure.
        await using var nowhere = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=nobody;Password=none;Timeout=2");
        var unreachable = new StringWriter();
        Assert.Equal(0, await DoctorCommand.Run(deploy, unreachable, down, Path.Combine(deploy.Root, "no-descriptors"), default, () => nowhere));
        Assert.Contains("skip  the database could not be read from here", unreachable.ToString());
        Assert.DoesNotContain("memory scopes are", unreachable.ToString());
    }

    async Task<string> Run(params string[] args)
    {
        var o = new StringWriter();
        Assert.Equal(0, args[0] == "person"
            ? await TokenCommand.Person(args[1..], o, new Principals(fixture.Source))
            : await TokenCommand.Token(deploy, args[1..], o, new Principals(fixture.Source)));
        return o.ToString();
    }
    string File(string name) => System.IO.File.ReadAllText(deploy.Secret($"harness_{name}_token"));

    [PostgresFact] public async Task A_token_made_for_a_person_is_listed_with_them_and_refused_once_they_are_suspended()
    {
        Directory.CreateDirectory(deploy.Root);
        var db = fixture.Db;
        var made = await Run("token", "create", "alex-phone", "--person", "alex");
        Assert.Contains("speaks for alex", made);
        Assert.DoesNotContain(File("alex-phone"), made);
        Assert.Contains("household view", await Run("token", "create", "kitchen"));
        var alexPhone = File("alex-phone");
        Assert.Equal("alex", await Scoped.Scalar(fixture.Source, "SELECT person_id FROM principals WHERE name='alex-phone'"));
        Assert.Equal(DBNull.Value, await Scoped.Scalar(fixture.Source, "SELECT person_id FROM principals WHERE name='kitchen'"));
        Assert.Equal("harness", (await db.Authenticate(alexPhone, default))!.Role);
        var list = await Run("token", "list");
        Assert.Contains("alex-phone\tharness\talex\t", list);
        Assert.Contains("kitchen\tharness\thousehold view\t", list);

        // Only a current person this install knows, only on a harness token, and never the household.
        foreach (var (args, why) in new[]
        {
            (new[] { "create", "nobody-phone", "--person", "nobody" }, "not a person this install knows"),
            (new[] { "create", "house-phone", "--person", "household" }, "not a person this install knows"),
            (new[] { "create", "--role", "ingest", "--plugin", "cgm", "--person", "alex" }, "--person is for a harness token"),
            (new[] { "create", "empty-phone", "--person" }, "--person names a person id"),
        })
        {
            var e = await Assert.ThrowsAsync<ArgumentException>(() => TokenCommand.Token(deploy, args, new StringWriter(), new Principals(fixture.Source)));
            Assert.Contains(why, e.Message);
        }
        Assert.False(System.IO.File.Exists(deploy.Secret("harness_nobody-phone_token")));

        // Suspending Alex disables every token bound to her at once; the household's stays.
        Assert.Contains("Suspended alex: 1 token(s) disabled.", await Run("person", "suspend", "alex"));
        Assert.Null(await db.Authenticate(alexPhone, default));
        Assert.False(await new Principals(fixture.Source).Enabled(alexPhone, default));
        Assert.NotNull(await db.Authenticate(File("kitchen"), default));
        Assert.DoesNotContain("alex-phone", await Run("token", "list"));
        Assert.Contains("alex\tsuspended\t", await Run("person", "list"));
        Assert.Contains("already suspended", await Run("person", "suspend", "alex"));
        Assert.Contains("is suspended", (await Assert.ThrowsAsync<ArgumentException>(() =>
            TokenCommand.Token(deploy, ["create", "alex-laptop", "--person", "alex"], new StringWriter(), new Principals(fixture.Source)))).Message);
        // Lifted: Alex is current again, her old token stays disabled, and a new one works.
        Assert.Contains("no longer suspended", await Run("person", "unsuspend", "alex"));
        Assert.Null(await db.Authenticate(alexPhone, default));
        await Run("token", "create", "alex-laptop", "--person", "alex");
        Assert.NotNull(await db.Authenticate(File("alex-laptop"), default));
        Assert.Contains("was not suspended", await Run("person", "unsuspend", "alex"));
        Assert.Throws<ArgumentException>(() => TokenCommand.Person(["suspend", "nobody"], new StringWriter(), new Principals(fixture.Source)).GetAwaiter().GetResult());

        // Taken out of people: the start that finds them gone disables every token bound to them, for good, so listing them
        // again (or giving the id to someone new) never lets an old device back in.
        var withJo = RhythmConfig.Parse(TestSettings.Yaml.Replace("people:\n", "people:\n  - { id: jo, name: Jo }\n", StringComparison.Ordinal));
        var principals = new Principals(fixture.Source);
        async Task<object?> Status(string person) => await Scoped.Scalar(fixture.Source, "SELECT status FROM subjects WHERE id=@p", ("p", person));
        await principals.Install(withJo, default);
        await Run("token", "create", "jo-phone", "--person", "jo");
        var joPhone = File("jo-phone");
        Assert.NotNull(await db.Authenticate(joPhone, default));
        Assert.Equal(1, (await principals.Install(TestSettings.Use(), default)).Tokens);
        Assert.Equal(false, await Scoped.Scalar(fixture.Source, "SELECT enabled FROM principals WHERE name='jo-phone'"));
        Assert.Null(await db.Authenticate(joPhone, default));
        await principals.Install(withJo, default);
        Assert.Equal("current", await Status("jo"));
        Assert.Null(await db.Authenticate(joPhone, default));
        await principals.Install(TestSettings.Use(), default);
        // And whatever its enabled flag says (a hand edit, or an older engine that left it on), it is refused while its
        // person is former, and listed as such.
        await Scoped.Execute(fixture.Source, "UPDATE principals SET enabled=true WHERE name='jo-phone'");
        Assert.Null(await db.Authenticate(joPhone, default));
        Assert.False(await principals.Enabled(joPhone, default));
        Assert.Contains("jo-phone\tharness\tjo (former: refused)\t", await Run("token", "list"));
        // Suspending someone no longer in people disables their tokens but leaves them former.
        Assert.Contains("so they stay former; 1 token(s) bound to them disabled", await Run("person", "suspend", "jo"));
        Assert.Equal("former", await Status("jo"));
        Assert.Contains("was not suspended", await Run("person", "unsuspend", "jo"));
        Assert.Equal("former", await Status("jo"));
        Assert.Contains("no longer in people", (await Assert.ThrowsAsync<ArgumentException>(() =>
            TokenCommand.Token(deploy, ["create", "jo-laptop", "--person", "jo"], new StringWriter(), new Principals(fixture.Source)))).Message);
        // Suspended first, then taken out of people: still suspended after the restart, and lifting the suspension makes
        // them former, not current, so no token can be made for them until they are listed again and the engine starts.
        await principals.Install(withJo, default);
        await Run("token", "create", "jo-tablet", "--person", "jo");
        Assert.Contains("Suspended jo: 1 token(s) disabled.", await Run("person", "suspend", "jo"));
        await principals.Install(TestSettings.Use(), default);
        Assert.Equal("suspended", await Status("jo"));
        Assert.Contains("so they are former", await Run("person", "unsuspend", "jo"));
        Assert.Equal("former", await Status("jo"));
        Assert.Contains("no longer in people", (await Assert.ThrowsAsync<ArgumentException>(() =>
            TokenCommand.Token(deploy, ["create", "jo-laptop", "--person", "jo"], new StringWriter(), new Principals(fixture.Source)))).Message);
        // Listed again and started: current, and only a token made now works.
        await principals.Install(withJo, default);
        Assert.Equal("current", await Status("jo"));
        await Run("token", "create", "jo-laptop", "--person", "jo");
        Assert.NotNull(await db.Authenticate(File("jo-laptop"), default));
        Assert.Null(await db.Authenticate(File("jo-tablet"), default));
        Assert.Null(await db.Authenticate(joPhone, default));
        Assert.Equal(1, (await principals.Install(TestSettings.Use(), default)).Tokens);
        Assert.Null(await db.Authenticate(File("jo-laptop"), default));
        // A suspended person's token that is still enabled (suspended outside rhythm person) is refused all the same.
        await Run("token", "create", "sam-phone", "--person", "sam");
        var samPhone = File("sam-phone");
        Assert.NotNull(await db.Authenticate(samPhone, default));
        await Scoped.Execute(fixture.Source, "UPDATE subjects SET status='suspended' WHERE id='sam'");
        try
        {
            Assert.Equal(true, await Scoped.Scalar(fixture.Source, "SELECT enabled FROM principals WHERE name='sam-phone'"));
            Assert.Null(await db.Authenticate(samPhone, default));
            Assert.False(await new Principals(fixture.Source).Enabled(samPhone, default));
        }
        finally { await Scoped.Execute(fixture.Source, "UPDATE subjects SET status='current' WHERE id='sam'"); }
        Assert.NotNull(await db.Authenticate(samPhone, default));
    }

    [PostgresFact] public async Task Revoke_all_disables_every_token_at_once_after_a_leak_and_the_door_refuses_them()
    {
        Directory.CreateDirectory(deploy.Root);
        var s = fixture.Source;
        var db = fixture.Db;
        await Run("token", "create", "leak-house");
        await Run("token", "create", "leak-sam", "--person", "sam");
        await Run("token", "create", "--role", "ingest", "--plugin", "cgm");
        var plugin = System.IO.File.ReadAllText(deploy.Secret("plugin_cgm_token"));
        // A viewer session's credential (an app principal bound to its person, D-070) and a legacy login.
        var viewer = Principals.NewToken();
        await Scoped.Execute(s, "INSERT INTO principals(id,name,token_hash,role,person_id) VALUES(gen_random_uuid(),'leak-viewer',@h,'app','alex')", ("h", Hashing.Hash(viewer)));
        var legacy = Principals.NewToken();
        await Scoped.Execute(s, "INSERT INTO principals(id,name,token_hash,role) VALUES(gen_random_uuid(),'leak-legacy',@h,'legacy')", ("h", Hashing.Hash(legacy)));
        var app = Principals.NewToken();
        await new Principals(s).SyncApp(app, default);
        // A viewer credential is revoked by name too, as token list and doctor name it; the shared app token's never.
        var second = Principals.NewToken();
        await Scoped.Execute(s, "INSERT INTO principals(id,name,token_hash,role,person_id) VALUES(gen_random_uuid(),'leak-viewer-2',@h,'app','sam')", ("h", Hashing.Hash(second)));
        Assert.NotNull(await db.Authenticate(second, true, default));
        Assert.Contains("Revoked 1 token(s)", await Run("token", "revoke", "leak-viewer-2"));
        Assert.Null(await db.Authenticate(second, true, default));
        Assert.Contains("Revoked 0 token(s)", await Run("token", "revoke", Principals.AppName));
        Assert.Equal("app", (await db.Authenticate(app, default))!.Role);
        var old = new[] { File("leak-house"), File("leak-sam"), plugin, viewer, legacy };

        await using var door = await Door.Open(db);
        Assert.Equal(HttpStatusCode.OK, await door.Call(File("leak-sam")));
        Assert.Equal(HttpStatusCode.OK, await door.Call(File("leak-house")));
        var enabled = (long)(await Scoped.Scalar(s, "SELECT count(*) FROM principals WHERE enabled AND role<>'system' AND NOT (role='app' AND person_id IS NULL)"))!;
        var said = await Run("token", "revoke", "--all");
        Assert.Contains($"Revoked {enabled} token(s)", said);
        Assert.Contains("secrets/app_token", said);
        foreach (var token in old) Assert.Null(await db.Authenticate(token, true, default));
        Assert.Equal(HttpStatusCode.Unauthorized, await door.Call(File("leak-sam")));
        Assert.Equal(HttpStatusCode.Unauthorized, await door.Call(File("leak-house")));
        Assert.Equal(0L, await Scoped.Scalar(s, "SELECT count(*) FROM principals WHERE enabled AND role<>'system' AND NOT (role='app' AND person_id IS NULL)"));
        // The app's own token comes from its file, and the engine's jobs keep theirs; a new token works at once.
        Assert.Equal("app", (await db.Authenticate(app, default))!.Role);
        Assert.Contains("Revoked 0 token(s)", await Run("token", "revoke", "--all"));
        await Run("token", "create", "leak-after", "--person", "sam");
        Assert.Equal(HttpStatusCode.OK, await door.Call(File("leak-after")));
        await Assert.ThrowsAsync<ArgumentException>(() => TokenCommand.Token(deploy, ["revoke", "--all", "now"], new StringWriter(), new Principals(s)));
    }

    // rhythm family, rhythm share and rhythm scopes off (SB-P1-18, docs/dev/memory.md sections 4 and 6.2), each answer as
    // the operator reads it, and its exit code.
    async Task<(int Code, string Output)> Host(string command, params string[] args)
    {
        var o = new StringWriter();
        var store = new Principals(fixture.Source);
        var code = command switch
        {
            "family" => await ScopeCommands.Family(args, o, store),
            "share" => await ScopeCommands.Share(args, o, store),
            _ => await ScopeCommands.Scopes(deploy, args, new StringReader(command == "scopes off, typing off" ? "off\n" : command == "scopes off, typing yes" ? "yes\n" : ""), o, store),
        };
        return (code, o.ToString());
    }
    async Task<string> Said(string command, params string[] args)
    {
        var (code, output) = await Host(command, args);
        Assert.True(code == 0, output);
        return output;
    }
    async Task<string?> Role(string person) => await Scoped.Scalar(fixture.Source, "SELECT role FROM memberships WHERE person_id=@p", ("p", person)) as string;

    // Family's members and admins are recorded by the operator on the host, and only for someone in people: joining, leaving
    // and the admin role change at once what a person's tokens read once scopes are on. Taking someone out while scopes
    // are off is refused until the switch's first start has been, since that start adds everyone in people again.
    [PostgresFact] public async Task Family_members_and_admins_are_the_operators_and_only_for_someone_in_people()
    {
        var s = fixture.Source;
        var principals = new Principals(s);
        await principals.Install(TestSettings.Use(), default);
        await Scoped.Execute(s, "DELETE FROM memberships");
        await Scoped.Execute(s, "INSERT INTO subjects(id,kind) VALUES('jo','person') ON CONFLICT DO NOTHING");
        await Scoped.Execute(s, "UPDATE subjects SET status='former' WHERE id='jo'");
        var sam = await Scoped.Principal(s, "family-sam", "harness", "sam");
        var note = await Scoped.Row(s, "household", "notes", "shared", "family-note");
        async Task<bool> SamReadsFamily() => (await Scoped.Readable(fixture.Db, sam, [note])).Count == 1;
        try
        {
            // With scopes off: joining and admins are recorded for when they are on; taking someone out would be undone.
            var added = await Said("family", "add", "sam");
            Assert.Contains("Added sam to Family", added);
            Assert.Contains("changes nothing anyone reads yet", added);
            Assert.Equal("member", await Role("sam"));
            var (code, refused) = await Host("family", "remove", "sam");
            Assert.Equal(1, code);
            Assert.Contains("would be undone", refused);
            Assert.Equal("member", await Role("sam"));
            Assert.Contains("alex is a Family admin (added to Family)", await Said("family", "admin", "alex"));
            Assert.Equal("admin", await Role("alex"));

            await Scoped.Mode(s, "on");
            Assert.True(await SamReadsFamily());
            Assert.Contains("Took sam out of Family", await Said("family", "remove", "sam"));
            Assert.Null(await Role("sam"));
            Assert.False(await SamReadsFamily());
            Assert.Contains("sam was not in Family", await Said("family", "remove", "sam"));
            var back = await Said("family", "add", "sam");
            Assert.Contains("Added sam to Family", back);
            Assert.DoesNotContain("changes nothing", back);
            Assert.True(await SamReadsFamily());
            Assert.Contains("sam was already in Family", await Said("family", "add", "sam"));
            // The admin role: given to a member, taken back leaving them a member, and a Family left without one said so.
            Assert.Contains("sam is a Family admin: they may change Family records others wrote", await Said("family", "admin", "sam"));
            Assert.Contains("sam was already a Family admin", await Said("family", "admin", "sam"));
            Assert.Equal("admin", await Role("sam"));
            var taken = await Said("family", "unadmin", "alex");
            Assert.Contains("alex is no longer a Family admin, and stays in Family", taken);
            Assert.DoesNotContain("no admin now", taken);
            Assert.Equal("member", await Role("alex"));
            Assert.Contains("Family has no admin now", await Said("family", "unadmin", "sam"));
            Assert.Contains("sam was not a Family admin", await Said("family", "unadmin", "sam"));
            await Said("family", "admin", "sam");
            var gone = await Said("family", "remove", "sam");
            Assert.Contains("their admin role with it", gone);
            Assert.Contains("Family has no admin now", gone);

            // Scopes switched on once (the first start recorded it) and off again: no later start adds anyone, so taking
            // someone out with scopes off stands, and says why.
            Assert.Contains("Added sam to Family", await Said("family", "add", "sam"));
            await Scoped.Execute(s, "INSERT INTO install_settings(key,value) VALUES(@k,'yes') ON CONFLICT DO NOTHING", ("k", Principals.JoinedKey));
            await Scoped.Mode(s, "off");
            var stands = await Said("family", "remove", "sam");
            Assert.Contains("Took sam out of Family", stands);
            Assert.Contains("once memory scopes are on again", stands);
            Assert.Contains("switching them on again adds nobody to Family", stands);
            Assert.Null(await Role("sam"));
            await Scoped.Execute(s, "DELETE FROM install_settings WHERE key=@k", ("k", Principals.JoinedKey));
            Assert.Equal(1, (await Host("family", "remove", "alex")).Code);
            await Scoped.Mode(s, "on");

            // A suspended person still in people may join (they read nothing while suspended); nobody else but people does.
            await Scoped.Execute(s, "UPDATE subjects SET status='suspended' WHERE id='sam'");
            Assert.Contains("sam is suspended", await Said("family", "add", "sam"));
            Assert.False(await SamReadsFamily());
            foreach (var (args, why) in new[]
            {
                (new[] { "add", "nobody" }, "not a person this install knows"), (new[] { "add", "household" }, "not a person this install knows"),
                (new[] { "add", "jo" }, "no longer in people"), (new[] { "admin", "jo" }, "no longer in people"),
                (new[] { "remove", "nobody" }, "not a person this install knows"),
            })
                Assert.Contains(why, (await Assert.ThrowsAsync<ArgumentException>(() => Host("family", args))).Message);
            // Suspended and then taken out of people: no longer listed, so no longer someone who joins.
            await Scoped.Execute(s, "UPDATE subjects SET status='suspended' WHERE id='jo'");
            Assert.Contains("no longer in people", (await Assert.ThrowsAsync<ArgumentException>(() => Host("family", "add", "jo"))).Message);
            foreach (var wrong in new[] { new[] { "add" }, ["promote", "alex"], ["add", "alex", "sam"], [] })
                Assert.Equal(ScopeCommands.FamilyUsage, (await Assert.ThrowsAsync<ArgumentException>(() => Host("family", wrong))).Message);
        }
        finally
        {
            await Scoped.Mode(s, "off");
            await Scoped.Execute(s, "DELETE FROM install_settings WHERE key=@k", ("k", Principals.JoinedKey));
            await Scoped.Execute(s, "DELETE FROM memberships");
            await Scoped.Execute(s, "UPDATE subjects SET status='current' WHERE id='sam'");
            await Scoped.Execute(s, "UPDATE subjects SET status='former' WHERE id='jo'");
            await Scoped.Execute(s, "DELETE FROM resources WHERE external_id='family-note'");
        }
    }

    // A source share is recorded and ended by the operator for one person's records of one adapter or plugin, never for
    // the native records of the core's own providers or the map's catalog, and Family reads them at once while scopes are on.
    [PostgresFact] public async Task A_source_share_is_the_operators_for_one_adapters_records_of_someone_in_people()
    {
        var s = fixture.Source;
        await new Principals(s).Install(TestSettings.Use(), default);
        await Scoped.Members(s, "alex", "sam");
        var csv = await Scoped.Row(s, "sam", "csv", "private", "share-sam-csv");
        var alex = await Scoped.Principal(s, "share-alex", "harness", "alex");
        var house = await Scoped.Principal(s, "share-house", "harness");
        async Task<int> Readers() => (await Scoped.Readable(fixture.Db, alex, [csv])).Count + (await Scoped.Readable(fixture.Db, house, [csv])).Count;
        try
        {
            var early = await Said("share", "add", "sam", "csv");
            Assert.Contains("Shared sam's csv records with Family", early);
            Assert.Contains("changes nothing anyone reads yet", early);
            Assert.Contains("There was no share", await Said("share", "revoke", "sam", "garmin"));
            Assert.Contains("Ended the share of sam's csv records", await Said("share", "revoke", "sam", "csv"));

            await Scoped.Mode(s, "on");
            Assert.Equal(0, await Readers());
            var shared = await Said("share", "add", "sam", "csv");
            Assert.Contains("Shared sam's csv records with Family: with memory scopes on, Family reads them (1 now)", shared);
            Assert.DoesNotContain("changes nothing", shared);
            Assert.Equal(2, await Readers());
            // Recorded by the operator: no principal shared it.
            Assert.Equal(DBNull.Value, await Scoped.Scalar(s, "SELECT shared_by FROM source_shares WHERE person_id='sam' AND provider='csv'"));
            Assert.Matches("^sam\tcsv\toperator\t\\d{4}-\\d{2}-\\d{2}\r?\n$", await Said("share", "list"));
            Assert.Contains("were already shared with Family (1 now)", await Said("share", "add", "sam", "csv"));
            Assert.Contains("sam has no garmin records yet", await Said("share", "add", "sam", "garmin"));
            Assert.Contains("Ended the share of sam's csv records", await Said("share", "revoke", "sam", "csv"));
            Assert.Equal(0, await Readers());
            Assert.Contains("There was no share of sam's csv records", await Said("share", "revoke", "sam", "csv"));

            foreach (var (args, why) in new[]
            {
                (new[] { "add", "sam", "memory" }, "is not a source a person shares"), (new[] { "add", "sam", "documents" }, "is not a source a person shares"),
                (new[] { "add", "sam", "log" }, "is not a source a person shares"), (new[] { "add", "sam", CatalogStars.Provider }, "is not a source a person shares"),
                (new[] { "add", "sam", "Not_A_Provider" }, "is not a source a person shares"),
                (new[] { "add", "nobody", "csv" }, "not a person this install knows"), (new[] { "add", "household", "csv" }, "not a person this install knows"),
            })
                Assert.Contains(why, (await Assert.ThrowsAsync<ArgumentException>(() => Host("share", args))).Message);
            await Scoped.Execute(s, "INSERT INTO subjects(id,kind) VALUES('jo','person') ON CONFLICT DO NOTHING");
            await Scoped.Execute(s, "UPDATE subjects SET status='former' WHERE id='jo'");
            Assert.Contains("no longer in people", (await Assert.ThrowsAsync<ArgumentException>(() => Host("share", "add", "jo", "csv"))).Message);
            foreach (var wrong in new[] { new[] { "add", "sam" }, ["list", "sam"], ["give", "sam", "csv"], [] })
                Assert.Equal(ScopeCommands.ShareUsage, (await Assert.ThrowsAsync<ArgumentException>(() => Host("share", wrong))).Message);
        }
        finally
        {
            await Scoped.Mode(s, "off");
            await Scoped.Execute(s, "DELETE FROM source_shares");
            await Scoped.Execute(s, "DELETE FROM memberships");
            await Scoped.Execute(s, "DELETE FROM resources WHERE external_id='share-sam-csv'");
        }
    }

    // Switching scopes off on purpose (memory.md 6.2): only with rhythm.yaml saying off, refused while Rhythm holds
    // anything private of its own, and only after a preview of what everyone would read (counts per person and provider,
    // never a title) and the operator typing off. Memberships, admins and shares are kept.
    [PostgresFact] public async Task Switching_scopes_off_shows_what_everyone_would_read_and_is_refused_while_anything_private_exists()
    {
        var s = fixture.Source;
        await new Principals(s).Install(TestSettings.Use(), default);
        Assert.Contains("already off", await Said("scopes", "off"));
        await Scoped.Mode(s, "on");
        await Scoped.Members(s, "alex");
        await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='alex'");
        await Scoped.Execute(s, "INSERT INTO source_shares(person_id,provider) VALUES('sam','csv')");
        Guid[] rows = [await Scoped.Row(s, "alex", "notes", "private", "off-alex-1"), await Scoped.Row(s, "alex", "notes", "private", "off-alex-2"),
            await Scoped.Row(s, "sam", "csv", "private", "off-sam"), await Scoped.Row(s, "household", "notes", "shared", "off-family")];
        Guid[] memories = [];
        var project = Guid.NewGuid();
        try
        {
            // rhythm.yaml missing, or one that does not say off: the next start would switch scopes on again.
            Directory.CreateDirectory(deploy.Root);
            var (code, output) = await Host("scopes off, typing off", "off");
            Assert.Equal((1, true), (code, output.Contains("rhythm.yaml is missing")));
            Assert.Equal(0, InitCommand.Run(deploy, ["--no-input", "--people", "Alex, Sam"], new Prompter(new StringReader(""), new StringWriter(), true, false)));
            var settings = System.IO.File.ReadAllText(deploy.Settings);
            // An .env that starts the engine from another settings file: rhythm.yaml saying off proves nothing then.
            var env = System.IO.File.ReadAllText(deploy.Env);
            System.IO.File.WriteAllText(deploy.Env, env.Replace("RHYTHM_CONFIG_FILE=./rhythm.yaml", "RHYTHM_CONFIG_FILE=./other.yaml"));
            (code, output) = await Host("scopes off, typing off", "off");
            Assert.Equal((1, true), (code, output.Contains(".env does not set RHYTHM_CONFIG_FILE=./rhythm.yaml")));
            System.IO.File.WriteAllText(deploy.Env, env);
            System.IO.File.WriteAllText(deploy.Settings, System.Text.RegularExpressions.Regex.Replace(settings, "(?m)^  scopes: off", "  scopes: on"));
            (code, output) = await Host("scopes off, typing off", "off");
            Assert.Equal((1, true), (code, output.Contains("Memory scopes stay on: rhythm.yaml says memory.scopes: on")));
            // A rhythm.yaml that does not load says so.
            System.IO.File.WriteAllText(deploy.Settings, System.Text.RegularExpressions.Regex.Replace(settings, "(?m)^  scopes: off", "  scopes: maybe"));
            (code, output) = await Host("scopes off, typing off", "off");
            Assert.Equal((1, true), (code, output.Contains("Memory scopes stay on: rhythm.yaml does not load (memory.scopes is off or on")));
            System.IO.File.WriteAllText(deploy.Settings, settings);

            // Anything private of Rhythm's own refuses it, named by person and kind, never by title.
            memories = [await MemoryRows.Memory(s, "alex", "alex", "DEMO: a private memory"), await MemoryRows.Memory(s, "household", "alex", "DEMO: shared since")];
            await MemoryRows.Run(s, """
                INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,status,written_at) VALUES(@m,1,'alex','private','DEMO: before the share','canonical',now()-interval '1 day')
                """, ("m", memories[1]));
            await MemoryRows.Run(s, "INSERT INTO memory_projects(id,subject_id,slug,name,author_id) VALUES(@id,'alex','sabbatical','DEMO: a private project','alex')", ("id", project));
            (code, output) = await Host("scopes off, typing off", "off");
            Assert.Equal(1, code);
            Assert.Contains("alex: 1 earlier private versions of memories now in Family, 1 private memories, 1 private memory projects", output);
            Assert.DoesNotContain("DEMO", output);
            Assert.Equal("on", await Scoped.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
            await MemoryRows.Run(s, "DELETE FROM memories WHERE id=ANY(@ids)", ("ids", memories));
            await MemoryRows.Run(s, "DELETE FROM resources WHERE id=ANY(@ids)", ("ids", memories));
            await MemoryRows.Run(s, "DELETE FROM memory_projects WHERE id=@id", ("id", project));

            // The preview, then anything but off changes nothing. A dashboard of Alex's own scope (migration 027) and a
            // write made from it, whose before and after values everyone would read too, and an insight of hers with a
            // feedback whose note everyone would read through insight_history (migration 066), are counted with her
            // records, never named.
            var maker = await Scoped.Principal(s, "preview-dashboard", "harness", "alex");
            await Scoped.Execute(s, "INSERT INTO dashboards(id,created_by,subject_id,scope) VALUES(@id,@by,'alex','private')", ("id", project), ("by", maker));
            await Scoped.Execute(s, """
                INSERT INTO dashboard_writes(id,dashboard_id,version,name,kind,target,summary,after,written_by,subject_id)
                VALUES(gen_random_uuid(),@id,1,'add','sheet','{}'::jsonb,'DEMO · a fictional edit','{}'::jsonb,@by,'alex')
                """, ("id", project), ("by", maker));
            await Scoped.Execute(s, """
                INSERT INTO insights(id,kind,title,summary,why_it_matters,next_step,points,evidence_level,limitations,stars,sources,generated_by,generated_at,sources_as_of,saved_by,subject_id,scope,sources_checked)
                VALUES(@id,'focus','DEMO preview','DEMO · a fictional insight.','','','[]','weak','','[]','[]','manual',now(),now(),@by,'alex','private','{}');
                INSERT INTO insight_events(insight_id,subject_id,event,value,note,principal_id,channel) VALUES(@id,'alex','feedback','interesting','DEMO · a fictional note',@by,'mcp')
                """, ("id", project), ("by", maker));
            (code, output) = await Host("scopes off, typing yes", "off");
            await Scoped.Execute(s, "DELETE FROM insights WHERE id=@id", ("id", project));
            await Scoped.Execute(s, "DELETE FROM dashboards WHERE id=@id", ("id", project));
            await Scoped.Execute(s, "DELETE FROM principals WHERE id=@id", ("id", maker));
            Assert.Equal(1, code);
            Assert.Contains("  alex: dashboard writes 1, dashboards 1, insight feedback and reviews 1, insights 1, notes 2", output);
            Assert.DoesNotContain("DEMO preview", output);
            Assert.DoesNotContain("a fictional note", output);
            Assert.Contains("  sam: csv 1 (Family reads these already: a source share)", output);
            Assert.Matches("  Family's \\d+ records, to sam, not in Family now", output);
            Assert.Contains("Nothing changed: memory scopes stay on.", output);
            Assert.DoesNotContain("scope fixture", output);
            Assert.Equal("on", await Scoped.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
            // Typing off records it, and what the operator recorded for scopes stays.
            (code, output) = await Host("scopes off, typing off", "off");
            Assert.Equal(0, code);
            Assert.Contains("Memory scopes are off: the household shares everything again", output);
            Assert.Equal("off", await Scoped.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
            Assert.Equal("admin", await Role("alex"));
            Assert.Equal(1L, await Scoped.Scalar(s, "SELECT count(*) FROM source_shares"));
            await new Principals(s).Install(TestSettings.Use(), default);
            // A database that records no mode (which the start refuses) is put back the same way.
            await Scoped.Execute(s, "DELETE FROM install_settings WHERE key='memory.scopes'");
            Assert.Equal(0, (await Host("scopes off, typing off", "off")).Code);
            Assert.Equal("off", await Scoped.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
            Assert.Equal(ScopeCommands.ScopesUsage, (await Assert.ThrowsAsync<ArgumentException>(() => Host("scopes", "on"))).Message);
        }
        finally
        {
            await MemoryRows.Run(s, "DELETE FROM memories WHERE id=ANY(@ids)", ("ids", memories));
            await MemoryRows.Run(s, "DELETE FROM resources WHERE id=ANY(@ids)", ("ids", memories));
            await MemoryRows.Run(s, "DELETE FROM memory_projects WHERE id=@id", ("id", project));
            await Scoped.Execute(s, "INSERT INTO install_settings(key,value) VALUES('memory.scopes','off') ON CONFLICT(key) DO UPDATE SET value='off'");
            await Scoped.Execute(s, "DELETE FROM source_shares");
            await Scoped.Execute(s, "DELETE FROM memberships");
            await Scoped.Execute(s, "DELETE FROM resources WHERE id=ANY(@ids)", ("ids", rows));
        }
    }

    // The engine's door (Mcp/Perimeter.cs) on an in-memory server, with this database's Authenticate: a harness call
    // reaches a stand-in for the MCP route or is refused.
    sealed class Door : IAsyncDisposable
    {
        WebApplication app = null!;
        HttpClient client = null!;

        public static async Task<Door> Open(Database db)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Use(new Perimeter(["engine"], () => true, db.Authenticate, new(App: (c, _) => c.Response.WriteAsync("app"),
                Ingest: (c, _) => c.Response.WriteAsync("ingest"), Document: (c, _, _) => c.Response.WriteAsync("document"))).Handle);
            app.MapPost("/mcp", () => "harness");
            await app.StartAsync();
            return new Door { app = app, client = app.GetTestClient() };
        }

        public async Task<HttpStatusCode> Call(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://engine/mcp") { Content = new StringContent("{}") };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }
}
