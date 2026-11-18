using System.Text.Json;
using Knowledge;
using Knowledge.Adapters;
using Knowledge.Cli;
using Npgsql;

namespace Knowledge.Tests;

// The install commands (engine/src/Cli/) against a temporary deploy folder, with fake adapter descriptors. Every value
// is fictional; secret-shaped ones are made at run time.
public sealed class CliTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "rhythm-cli-" + Guid.NewGuid().ToString("N")[..10]);
    readonly DeployFolder deploy;
    readonly string descriptors;

    public CliTests()
    {
        deploy = new(Path.Combine(root, "deploy"));
        descriptors = Path.Combine(root, "adapters");
        Directory.CreateDirectory(deploy.Root);
        Directory.CreateDirectory(descriptors);
    }
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    static Prompter Quiet(TextWriter o) => new(new StringReader(""), o, noInput: true, terminal: false);
    static Prompter Answers(TextWriter o, params string[] lines) => new(new StringReader(string.Join('\n', lines) + "\n"), o, noInput: false, terminal: false);
    // Init as on a host that exported nothing: the cli service then passes COMPOSE_PROJECT_NAME rhythm and RHYTHM_VERSION dev.
    static string? Host(string name) => name switch { "COMPOSE_PROJECT_NAME" => "rhythm", "RHYTHM_VERSION" => "dev", _ => null };
    string Init(params string[] args)
    {
        var o = new StringWriter();
        Assert.Equal(0, InitCommand.Run(deploy, ["--no-input", "--people", "Alex, Sam", .. args], Quiet(o), Host));
        return o.ToString();
    }
    RhythmConfig Settings() => RhythmConfig.Load(deploy.Settings);
    Dictionary<string, string> Env() => EnvFile.Read(deploy.Env);
    string Secret(string name) => File.ReadAllText(deploy.Secret(name));
    static void Mode(string path, UnixFileMode expected)
    {
        if (!OperatingSystem.IsWindows()) Assert.Equal(expected, File.GetUnixFileMode(path));
    }

    // A fake adapter with every kind of setting and secret, and its own worker profile.
    void Logbook(string extra = "") => File.WriteAllText(Path.Combine(descriptors, "logbook.json"), $$"""
        {
          "name": "logbook", "title": "Logbook", "kinds": ["MemorySource"], "summary": "A fictional diary service.",
          "profile": "logbook", "docs": "docs/adapters/logbook.md", {{extra}}
          "settings": [
            { "key": "url", "prompt": "Address", "type": "url", "required": true },
            { "key": "owner", "prompt": "Whose", "type": "person", "default": "household" },
            { "key": "tags", "prompt": "Tags", "type": "list" },
            { "key": "pollSeconds", "prompt": "Poll", "type": "int", "default": 300 }
          ],
          "secrets": [
            { "purpose": "api_key", "prompt": "Logbook key", "source": "paste" },
            { "purpose": "webhook", "prompt": "Webhook key", "source": "generate" }
          ]
        }
        """);

    // ---- YAML and .env edits

    [Fact] public void Yaml_edits_keep_comments_and_add_missing_parents()
    {
        var yaml = "version: 1\nengine:\n  url: http://engine:8080   # the app calls it\n  legacyToolNames: false\nplugins: {}\n";
        var edited = YamlText.Set(yaml, "engine.url", YamlText.Scalar("http://other:8080"));
        Assert.Contains("  url: http://other:8080    # the app calls it", edited);
        Assert.Equal(yaml.Split('\n')[2].IndexOf('#'), edited.Split('\n')[2].IndexOf('#'));
        Assert.Equal("false", YamlText.Get(edited, "engine.legacyToolNames"));
        edited = YamlText.Set(edited, "adapters.garmin.owner", "alex");
        edited = YamlText.Set(edited, "plugins.cgm.enabled", "true");
        Assert.Equal("alex", YamlText.Get(edited, "adapters.garmin.owner"));
        Assert.Equal("true", YamlText.Get(edited, "plugins.cgm.enabled"));
        Assert.Equal("http://other:8080", YamlText.Get(edited, "engine.url"));
    }

    [Fact] public void Yaml_edits_replace_a_block_value_and_leave_what_follows()
    {
        var yaml = "a:\n  roots:\n    - one\n    - two\n  # about b\n  b: 1\npeople:\n- id: x\n  name: X\nlast: true\n";
        var edited = YamlText.Set(yaml, "a.roots", YamlText.List(["three"]));
        Assert.Equal("three", YamlText.Get(edited, "a.roots"));
        Assert.Contains("  # about b\n  b: 1", edited);
        edited = YamlText.SetBlock(edited, "people", ["- id: alex", "  name: Alex"]);
        Assert.Contains("people:\n  - id: alex\n    name: Alex\nlast: true", edited);
    }

    [Theory]
    [InlineData("Rhythm of Life", true)]
    [InlineData("http://127.0.0.1:8181/mcp", true)]
    [InlineData("/data/notes", true)]
    [InlineData("yes", false)]
    [InlineData("a: b", false)]
    [InlineData("[::1]", false)]
    [InlineData("1200", false)]
    [InlineData("Zoë's #1", false)]
    public void Yaml_scalars_read_back_as_written(string value, bool plain)
    {
        var text = YamlText.Scalar(value);
        Assert.Equal(plain, text == value);
        Assert.Equal(value, YamlText.Get(YamlText.Set("x: 1\n", "key", text), "key"));
    }

    [Fact] public void Env_edits_set_append_quote_and_keep_profiles_as_a_set()
    {
        var text = "# names only\nRHYTHM_BIND=\nCOMPOSE_PROFILES=\n";
        text = EnvFile.Set(text, "RHYTHM_BIND", "0.0.0.0");
        text = EnvFile.Set(text, "RHYTHM_NOTES_DIR", "/srv/my notes");
        text = EnvFile.Profiles(EnvFile.Profiles(EnvFile.Profiles(text, "garmin", true), "titles", true), "garmin", false);
        Assert.StartsWith("# names only\nRHYTHM_BIND=0.0.0.0\n", text);
        Assert.Contains("RHYTHM_NOTES_DIR='/srv/my notes'", text);
        var parsed = EnvFile.Parse(text);
        Assert.Equal("/srv/my notes", parsed["RHYTHM_NOTES_DIR"]);
        Assert.Equal("titles", parsed["COMPOSE_PROFILES"]);
    }

    // ---- init

    [Fact] public void Init_writes_settings_env_and_private_secrets_from_the_examples()
    {
        var output = Init("--app-port", "18080", "--mcp-port", "18181");
        var config = Settings();
        Assert.Equal(["alex", "sam", "household"], config.Subjects);
        Assert.Equal("UTC", config.Timezone);
        Assert.True(config.Demo.Enabled);
        Assert.False(config.AdapterEnabled("notes"));
        Assert.Equal("http://127.0.0.1:18181/mcp", config.Engine.PublicMcpUrl);
        Assert.Equal("http://127.0.0.1:18080", config.App.PublicUrl);
        // A new install ranks keyword search; only settings written before ranking, with no such line, keep the old one.
        Assert.False(config.Engine.LegacySearch);
        // Memory scopes off, said in words (docs/dev/memory.md section 6.1), and no adapter on beyond the local ones.
        Assert.False(MemorySettings.From(config).Scopes);
        Assert.Equal(["csv", "documents", "embeddings"], config.Adapters.Keys.Where(config.AdapterEnabled));
        var yaml = File.ReadAllText(deploy.Settings);
        Assert.Contains("\nmemory:", yaml);
        Assert.Contains("\n  scopes: off ", yaml);
        Assert.StartsWith("# rhythm.yaml: the settings of this install", yaml);
        Assert.Contains("# everyone listed sees everything", yaml);
        var env = Env();
        Assert.Equal("./rhythm.yaml", env["RHYTHM_CONFIG_FILE"]);
        Assert.Equal(("rhythm", "dev"), (env["COMPOSE_PROJECT_NAME"], env["RHYTHM_VERSION"]));
        Assert.Equal("127.0.0.1", env["RHYTHM_BIND"]);
        Assert.Equal("18080", env["RHYTHM_APP_PORT"]);
        Assert.Equal("./data/backups", env["RHYTHM_BACKUP_DIR"]);

        var secrets = DeployFolder.CoreSecrets.Select(Secret).ToArray();
        Assert.Equal(4, secrets.Distinct().Count());
        Assert.Matches("^[A-Za-z0-9_-]{43}$", Secret("app_token"));
        var db = new NpgsqlConnectionStringBuilder(Secret("engine_db"));
        Assert.Equal(("postgres", "rhythm", "rhythm_engine"), (db.Host, db.Database, db.Username));
        Assert.Equal(43, db.Password!.Length);
        Assert.DoesNotContain(Secret("app_token"), output);
        Assert.Contains("Keep a copy of deploy/compose/secrets/backup_password", output);
        Mode(deploy.Secret("engine_db"), Owners.Private);
        Mode(deploy.Secret("app_token"), Owners.Shared);
        Mode(deploy.Secrets, Owners.PrivateFolder);
        Assert.All(new[] { "notes", "import", "backups", "backup-staging" }, f => Assert.True(Directory.Exists(Path.Combine(deploy.Root, "data", f))));
    }

    [Fact] public void Init_gives_the_examples_personal_adapters_to_the_first_person_listed()
    {
        Assert.Equal(0, InitCommand.Run(deploy, ["--no-input", "--people", "Robin, Kim"], Quiet(new StringWriter()), Host));
        var settings = Settings();
        Assert.Equal(["robin", "robin", "household"], new[] { "garmin", "nightscout", "drive" }.Select(a => settings.Adapter(a).Owner));
        Assert.Contains("owner: robin                   # the person whose Garmin it is", File.ReadAllText(deploy.Settings));
    }

    [Fact] public void Init_runs_once_and_force_keeps_every_secret()
    {
        Init();
        var before = DeployFolder.CoreSecrets.Select(Secret).ToArray();
        Assert.Throws<ArgumentException>(() => InitCommand.Run(deploy, ["--no-input", "--people", "Alex"], Quiet(new StringWriter())));
        var output = Init("--force", "--name", "Second try");
        Assert.Equal(before, DeployFolder.CoreSecrets.Select(Secret).ToArray());
        Assert.Equal("Second try", Settings().Instance.Name);
        Assert.Contains("4 kept", output);
    }

    [Fact] public void Forced_init_preserves_the_effective_compose_settings_and_only_overrides_requested_values()
    {
        Init("--app-port", "18080");
        var custom = File.ReadAllText(deploy.Env) + """
            # site settings
            COMPOSE_FILE=compose.yaml:extra.yaml
            COMPOSE_PROFILES=notes
            RHYTHM_BACKUP_DIR='/backup disk/nightly'
            RHYTHM_IMPORT_DIR=/srv/imports
            ANOTHER_SETTING=keep
            """ + "\n";
        File.WriteAllText(deploy.Env, custom);
        Init("--force");
        Assert.Equal(custom, File.ReadAllText(deploy.Env));
        Assert.EndsWith(":18080", Settings().App.PublicUrl);
        Init("--force", "--app-port", "19080", "--data", "/srv/changed");
        Assert.Equal("19080", Env()["RHYTHM_APP_PORT"]);
        Assert.Equal("/srv/changed/backups", Env()["RHYTHM_BACKUP_DIR"]);
        Assert.Equal(("notes", "compose.yaml:extra.yaml", "keep"), (Env()["COMPOSE_PROFILES"], Env()["COMPOSE_FILE"], Env()["ANOTHER_SETTING"]));
    }

    [Fact] public void Starter_is_independent_of_demo_and_preserves_existing_scope_and_tool_choices()
    {
        Init("--demo", "no", "--starter", "no");
        string? Flag(string name) => YamlText.Get(File.ReadAllText(deploy.Settings), "memory." + name);
        Assert.Null(Flag("work"));
        var settings = YamlText.Set(File.ReadAllText(deploy.Settings), "memory.scopes", "on");
        File.WriteAllText(deploy.Settings, YamlText.Set(settings, "memory.capture", "on"));
        Init("--force", "--starter", "yes", "--demo", "no");
        Assert.Equal("yes", Env()["RHYTHM_STARTER_WORKSPACE"]);
        Assert.Equal("on", Flag("tools"));
        Assert.Equal("on", Flag("work"));
        Assert.Equal("on", Flag("capsules"));
        Assert.Null(Flag("capsuleWrites"));
        File.WriteAllText(deploy.Settings, YamlText.Set(File.ReadAllText(deploy.Settings), "memory.work", "off"));
        Init("--force");
        Assert.Equal("off", Flag("work"));
        Init("--force", "--starter", "no");
        Assert.Equal("no", Env()["RHYTHM_STARTER_WORKSPACE"]);
        Assert.Equal("on", Flag("scopes"));
        Assert.Equal("on", Flag("capture"));
        Assert.Equal("on", Flag("tools"));
    }

    [Fact] public void Init_asks_each_question_when_answers_are_piped_in()
    {
        var o = new StringWriter();
        Assert.Equal(0, InitCommand.Run(deploy, [], Answers(o, "Our home", "Alex, sam=Samantha", "Europe/London", "", "lan", "rhythm.local", "n", "/srv/notes",
            "rhythm-home"), Host));
        var config = Settings();
        Assert.Equal("Our home", config.Instance.Name);
        Assert.Equal(["alex", "sam"], config.People.Select(p => p.Id));
        Assert.Equal("Samantha", config.People[1].Name);
        Assert.Equal("Europe/London", config.Timezone);
        Assert.False(config.Demo.Enabled);
        Assert.True(config.AdapterEnabled("notes"));
        Assert.Contains("rhythm.local", config.Engine.AllowedHosts);
        Assert.Equal("http://rhythm.local:8181/mcp", config.Engine.PublicMcpUrl);
        Assert.Equal(("0.0.0.0", "/srv/notes"), (Env()["RHYTHM_BIND"], Env()["RHYTHM_NOTES_DIR"]));
        Assert.Equal("rhythm-home", Env()["COMPOSE_PROJECT_NAME"]);
        Assert.Contains("Docker Compose project name (a second install on this computer needs its own) [rhythm]", o.ToString());
        Assert.Contains("anyone on it can open the app", o.ToString());
    }

    [Fact] public void Init_refuses_a_wrong_answer_naming_the_flag()
    {
        var e = Assert.Throws<ArgumentException>(() => InitCommand.Run(deploy, ["--no-input", "--people", "Alex", "--timezone", "Mars/Olympus"], Quiet(new StringWriter())));
        Assert.Contains("--timezone", e.Message);
        Assert.Throws<ArgumentException>(() => InitCommand.Run(deploy, ["--no-input"], Quiet(new StringWriter())));
        Assert.Throws<ArgumentException>(() => InitCommand.Run(deploy, ["--no-input", "--people", "household=Shared"], Quiet(new StringWriter())));
        Assert.False(File.Exists(deploy.Settings));
    }

    [Fact] public void Init_proxied_exposures_use_https_and_the_host()
    {
        // Cloudflare: the app's host name has a login in front, so /mcp has a host name of its own, and the engine answers to it.
        var said = Init("--exposure", "cloudflare", "--host", "rhythm.example.invalid");
        var config = Settings();
        Assert.Equal("https://rhythm.example.invalid", config.App.PublicUrl);
        Assert.Equal("https://rhythm-mcp.example.invalid/mcp", config.Engine.PublicMcpUrl);
        Assert.Equal("rhythm.example.invalid", config.Adapter("cloudflare").String("hostname"));
        Assert.Equal("rhythm-mcp.example.invalid", config.Adapter("cloudflare").String("mcpHostname"));
        Assert.Contains("rhythm-mcp.example.invalid", config.Engine.AllowedHosts);
        Assert.DoesNotContain("rhythm.example.invalid", config.Engine.AllowedHosts);
        Assert.False(config.AdapterEnabled("cloudflare"));
        Assert.Equal("127.0.0.1", Env()["RHYTHM_BIND"]);
        Assert.Contains("/mcp alone at https://rhythm-mcp.example.invalid", said);
    }

    [Fact] public void Init_takes_a_host_name_for_mcp_with_cloudflare_and_never_the_apps_own()
    {
        Init("--exposure", "cloudflare", "--host", "home.example.invalid", "--mcp-host", "ai.example.invalid");
        var config = Settings();
        Assert.Equal("https://ai.example.invalid/mcp", config.Engine.PublicMcpUrl);
        Assert.Equal("ai.example.invalid", config.Adapter("cloudflare").String("mcpHostname"));
        Assert.Contains("ai.example.invalid", config.Engine.AllowedHosts);
        Assert.Equal("rhythm-mcp", InitCommand.McpHostDefault("rhythm"));
        Assert.Equal("a-mcp.b.example.invalid", InitCommand.McpHostDefault("a.b.example.invalid"));
    }

    [Fact] public void Init_refuses_the_apps_host_name_for_mcp_with_cloudflare()
    {
        var e = Assert.Throws<ArgumentException>(() => InitCommand.Run(deploy,
            ["--no-input", "--people", "Alex", "--exposure", "cloudflare", "--host", "rhythm.example.invalid", "--mcp-host", "RHYTHM.example.invalid"], Quiet(new StringWriter())));
        Assert.Contains("--mcp-host", e.Message);
        Assert.False(File.Exists(deploy.Settings));
    }

    [Fact] public void Init_replaces_the_empty_folders_Docker_makes_for_missing_secrets()
    {
        Directory.CreateDirectory(deploy.Secret("app_token"));
        var output = Init();
        Assert.True(File.Exists(deploy.Secret("app_token")));
        Assert.Contains("Replaced 1 empty folders", output);
    }

    [Fact] public void Init_records_the_compose_project_and_image_tag_for_later_commands()
    {
        // A second install names its own project; the tag is the one this command runs under, passed in by the cli service.
        var o = new StringWriter();
        Assert.Equal(0, InitCommand.Run(deploy, ["--no-input", "--people", "Alex", "--project-name", "rhythm-b"], Quiet(o),
            name => name == "RHYTHM_VERSION" ? "0.2.0" : null));
        Assert.Equal(("rhythm-b", "0.2.0"), (Env()["COMPOSE_PROJECT_NAME"], Env()["RHYTHM_VERSION"]));
        Assert.Contains("rhythm-b_internal", o.ToString());
        // Without the flag the project this runs under is kept (an exported COMPOSE_PROJECT_NAME, or one already in .env).
        Assert.Equal(0, InitCommand.Run(deploy, ["--no-input", "--people", "Alex", "--force"], Quiet(new StringWriter()),
            name => name == "COMPOSE_PROJECT_NAME" ? "rhythm-b" : null));
        Assert.Equal(("rhythm-b", "0.2.0"), (Env()["COMPOSE_PROJECT_NAME"], Env()["RHYTHM_VERSION"]));
        // Compose's own rules for a project name and an image tag.
        Assert.Contains("--project-name", Assert.Throws<ArgumentException>(() => InitCommand.Run(deploy,
            ["--no-input", "--people", "Alex", "--force", "--project-name", "Rhythm B"], Quiet(new StringWriter()), Host)).Message);
        Assert.Contains("RHYTHM_VERSION", Assert.Throws<ArgumentException>(() => InitCommand.Run(deploy,
            ["--no-input", "--people", "Alex", "--force"], Quiet(new StringWriter()), name => name == "RHYTHM_VERSION" ? "a tag" : null)).Message);
    }

    [Fact] public void People_ids_come_from_names_and_stay_distinct()
    {
        Assert.Equal(["zoe-brown", "zoe-brown-2", "p-2nd", "sam"],
            InitCommand.ParsePeople("Zoë Brown, Zoe Brown, 2nd, sam=Sam").Select(p => p.Id));
    }

    // ---- connect, disconnect

    [Fact] public async Task Connect_lists_adapters_and_turns_one_on_with_settings_secrets_and_profile()
    {
        Init();
        Logbook();
        var listing = new StringWriter();
        Assert.Equal(0, await ConnectCommand.Run(deploy, [], Quiet(listing), descriptors: descriptors));
        Assert.Contains("logbook", listing.ToString());
        Assert.Contains("off", listing.ToString());

        var pasted = "lb-" + Principals.NewToken();
        var o = new StringWriter();
        Assert.Equal(0, await ConnectCommand.Run(deploy, ["logbook", "--set", "url=https://logbook.example.invalid/"],
            Answers(o, "", "walks, sleep", "", pasted), descriptors: descriptors));
        var section = Settings().Adapter("logbook");
        Assert.True(section.Enabled);
        Assert.Equal("https://logbook.example.invalid/", section.String("url"));
        Assert.Equal("household", section.String("owner"));
        Assert.Equal(["walks", "sleep"], section.List("tags"));
        Assert.Equal(300, section.Int("pollSeconds", 0));
        Assert.Equal(pasted, File.ReadAllText(deploy.AdapterSecret("logbook", "api_key")));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", File.ReadAllText(deploy.AdapterSecret("logbook", "webhook")));
        Mode(deploy.AdapterSecret("logbook", "api_key"), Owners.Private);
        Assert.Equal("logbook", Env()["COMPOSE_PROFILES"]);
        Assert.DoesNotContain(pasted, o.ToString());
        Assert.Contains("# everyone listed sees everything", File.ReadAllText(deploy.Settings));

        // Again without input: every answer kept, the secrets too.
        var webhook = File.ReadAllText(deploy.AdapterSecret("logbook", "webhook"));
        Assert.Equal(0, await ConnectCommand.Run(deploy, ["logbook", "--no-input"], Quiet(new StringWriter()), descriptors: descriptors));
        Assert.Equal((pasted, webhook), (File.ReadAllText(deploy.AdapterSecret("logbook", "api_key")), File.ReadAllText(deploy.AdapterSecret("logbook", "webhook"))));
        Assert.Equal(["walks", "sleep"], Settings().Adapter("logbook").List("tags"));

        Assert.Equal(0, ConnectCommand.Disconnect(deploy, ["logbook"], new StringWriter(), descriptors));
        Assert.False(Settings().AdapterEnabled("logbook"));
        Assert.Equal("", Env()["COMPOSE_PROFILES"]);
        Assert.True(File.Exists(deploy.AdapterSecret("logbook", "api_key")));
    }

    [Fact] public async Task Connect_refuses_unknown_adapters_wrong_answers_and_secret_keys_in_descriptors()
    {
        Init();
        Logbook();
        await Assert.ThrowsAsync<ArgumentException>(() => ConnectCommand.Run(deploy, ["nothing"], Quiet(new StringWriter()), descriptors: descriptors));
        await Assert.ThrowsAsync<ArgumentException>(() => ConnectCommand.Run(deploy, ["logbook", "--no-input", "--set", "url=ftp://x"], Quiet(new StringWriter()), descriptors: descriptors));
        await Assert.ThrowsAsync<ArgumentException>(() => ConnectCommand.Run(deploy, ["logbook", "--no-input", "--set", "url=https://logbook.example.invalid/", "--set", "owner=nobody"],
            Quiet(new StringWriter()), descriptors: descriptors));
        // A paste secret has to be typed or piped in.
        await Assert.ThrowsAsync<ArgumentException>(() => ConnectCommand.Run(deploy, ["logbook", "--no-input", "--set", "url=https://logbook.example.invalid/"],
            Quiet(new StringWriter()), descriptors: descriptors));
        Assert.False(Settings().AdapterEnabled("logbook"));

        File.WriteAllText(Path.Combine(descriptors, "leaky.json"), """
            { "name": "leaky", "title": "Leaky", "kinds": ["Writer"], "summary": "s", "docs": "docs/x.md",
              "settings": [{ "key": "accessToken", "prompt": "p", "type": "string" }], "secrets": [] }
            """);
        Assert.Throws<ConfigException>(() => AdapterDescriptor.Load(descriptors));
        File.WriteAllText(Path.Combine(descriptors, "leaky.json"), """
            { "name": "leaky", "title": "Leaky", "kinds": ["Writer"], "summary": "s", "docs": "docs/x.md",
              "settings": [{ "key": "account", "prompt": "p", "type": "string", "pattern": "^(" }], "secrets": [] }
            """);
        Assert.Throws<ConfigException>(() => AdapterDescriptor.Load(descriptors));
    }

    [Fact] public async Task A_list_settings_pattern_checks_each_item_and_doctor_takes_them_for_settings()
    {
        // Folder IDs are long and random-looking, but they are settings, not secrets.
        Init();
        File.WriteAllText(Path.Combine(descriptors, "shelf.json"), """
            { "name": "shelf", "title": "Shelf", "kinds": ["DocumentSource"], "summary": "A fictional shelf.", "docs": "docs/adapters/shelf.md",
              "settings": [{ "key": "folders", "prompt": "Folder IDs", "type": "list", "pattern": "^[A-Za-z0-9_-]{1,200}$" }], "secrets": [] }
            """);
        var folder = "f" + Guid.NewGuid().ToString("N") + "Qz";
        await Assert.ThrowsAsync<ArgumentException>(() => ConnectCommand.Run(deploy, ["shelf", "--no-input", "--set", "folders=" + folder + ",not a folder"],
            Quiet(new StringWriter()), descriptors: descriptors));
        Assert.Equal(0, await ConnectCommand.Run(deploy, ["shelf", "--no-input", "--set", "folders=" + folder], Quiet(new StringWriter()), descriptors: descriptors));
        Assert.Equal([folder], Settings().Adapter("shelf").List("folders"));
        var (code, output) = await Doctor();
        Assert.Equal(0, code);
        Assert.Contains("no secret-shaped values", output);
    }

    [Fact] public async Task Connect_takes_an_optional_secret_left_empty()
    {
        // As Garmin's key with exported files only, or the token of a Nightscout site anyone can read.
        Init();
        File.WriteAllText(Path.Combine(descriptors, "diary.json"), """
            { "name": "diary", "title": "Diary", "kinds": ["MemorySource"], "summary": "A fictional diary.", "docs": "docs/adapters/diary.md",
              "settings": [], "secrets": [{ "purpose": "token", "prompt": "Diary token (leave empty if none)", "source": "paste", "required": false }] }
            """);
        Assert.Equal(0, await ConnectCommand.Run(deploy, ["diary"], Answers(new StringWriter(), ""), descriptors: descriptors));
        Assert.True(Settings().AdapterEnabled("diary"));
        Assert.False(File.Exists(deploy.AdapterSecret("diary", "token")));
    }

    // A fake exposure container that calls /mcp with its own harness token and takes an ID of one form.
    void Relay() => File.WriteAllText(Path.Combine(descriptors, "relay.json"), """
        {
          "name": "relay", "title": "Relay", "kinds": ["Exposure"], "summary": "A fictional outbound relay.",
          "profile": "relay", "docs": "docs/adapters/relay.md", "harnessToken": true,
          "settings": [{ "key": "relayId", "prompt": "Relay ID", "type": "string", "required": true, "pattern": "^relay_[a-z0-9]{32}$" }],
          "secrets": [{ "purpose": "key", "prompt": "Relay key", "source": "paste" }]
        }
        """);

    [Fact] public async Task Connect_wants_the_containers_own_token_first_and_doctor_takes_its_ID_for_a_setting()
    {
        Init();
        Relay();
        var id = "relay_" + Guid.NewGuid().ToString("N");
        var key = "rk-" + Principals.NewToken();
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => ConnectCommand.Run(deploy, ["relay", "--set", "relayId=" + id],
            Answers(new StringWriter(), key), descriptors: descriptors));
        Assert.Contains("./rhythm token create relay", refused.Message);
        Assert.False(File.Exists(deploy.AdapterSecret("relay", "key")));

        Owners.WriteNew(deploy.Secret("harness_relay_token"), Principals.NewToken(), Owners.Person(deploy, Owners.Private));
        // A key pasted where the ID belongs is refused, so it never lands in rhythm.yaml.
        await Assert.ThrowsAsync<ArgumentException>(() => ConnectCommand.Run(deploy, ["relay", "--set", "relayId=" + key],
            Answers(new StringWriter(), key), descriptors: descriptors));
        Assert.Equal(0, await ConnectCommand.Run(deploy, ["relay", "--set", "relayId=" + id], Answers(new StringWriter(), key), descriptors: descriptors));
        Assert.Equal(id, Settings().Adapter("relay").String("relayId"));
        Assert.Equal("relay", Env()["COMPOSE_PROFILES"]);

        var (code, output) = await Doctor();
        Assert.Equal(0, code);
        Assert.Contains("no secret-shaped values", output);
        File.Delete(deploy.Secret("harness_relay_token"));
        (code, output) = await Doctor();
        Assert.Equal(1, code);
        Assert.Contains("harness_relay_token is missing: needed by the enabled adapter relay: ./rhythm token create relay", output);
        Assert.DoesNotContain(key, output);
    }

    sealed class Consent(int code) : IAdapterConnect
    {
        public string Name => "consent";
        public IReadOnlyDictionary<string, ProviderPolicy> Providers => new Dictionary<string, ProviderPolicy>();
        public Task<int> Connect(ConnectContext context, CancellationToken ct)
        {
            context.Output.WriteLine($"Pretending to ask {context.Settings.String("account")} for consent.");
            File.WriteAllText(Path.Combine(context.SecretsDir, "consent_grant"), "grant-" + Guid.NewGuid());
            return Task.FromResult(code);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public async Task Connect_runs_the_adapters_own_step_and_writes_settings_only_when_it_succeeds(int code, bool on)
    {
        Init();
        File.WriteAllText(Path.Combine(descriptors, "consent.json"), """
            { "name": "consent", "title": "Consent", "kinds": ["DocumentSource"], "summary": "A fictional browser consent.", "docs": "docs/x.md",
              "connect": true, "settings": [{ "key": "account", "prompt": "Account", "type": "string", "default": "demo-account" }],
              "secrets": [{ "purpose": "grant", "prompt": "Grant", "source": "connect" }] }
            """);
        var o = new StringWriter();
        Assert.Equal(code, await ConnectCommand.Run(deploy, ["consent", "--no-input"], Quiet(o), [() => new Consent(code)], descriptors));
        Assert.Contains("Pretending to ask demo-account", o.ToString());
        Assert.Equal(on, Settings().AdapterEnabled("consent"));
        Mode(deploy.AdapterSecret("consent", "grant"), Owners.Private);
    }

    // ---- doctor, preflight

    static StackCommands.Probe Stack(int? health, int? mcp = 401, int? app = 200) => (method, url, _) =>
        Task.FromResult(url.EndsWith("/health", StringComparison.Ordinal) && url.Contains("engine") ? health : url.EndsWith("/mcp", StringComparison.Ordinal) ? mcp : app);
    async Task<(int Code, string Output)> Doctor(StackCommands.Probe? probe = null)
    {
        var o = new StringWriter();
        var code = await DoctorCommand.Run(deploy, o, probe ?? Stack(null), descriptors);
        return (code, o.ToString());
    }

    [Fact] public async Task Doctor_passes_a_fresh_install_and_checks_the_stack_when_it_is_up()
    {
        // A long project name and tag with digits are not secret-shaped (cli/e2e.sh runs with such names).
        Assert.Equal(0, InitCommand.Run(deploy, ["--no-input", "--people", "Alex, Sam", "--project-name", "rhythm-second-install-e2e"],
            Quiet(new StringWriter()), name => name == "RHYTHM_VERSION" ? "second-install-e2e-2026" : null));
        var (code, output) = await Doctor();
        Assert.Equal(0, code);
        Assert.Contains("skip  the stack is not running", output);
        Assert.Contains("no secret-shaped values", output);
        (code, output) = await Doctor(Stack(200));
        Assert.Equal(0, code);
        Assert.Contains("MCP answers and asks for a token", output);
        (code, output) = await Doctor(Stack(503));
        Assert.Equal(1, code);
        Assert.DoesNotContain(Secret("app_token"), output);
    }

    [Theory]
    [InlineData(401, 0)]
    [InlineData(302, 1)]
    [InlineData(null, 1)]
    public async Task Doctor_in_running_engine_checks_only_its_mounts_and_probes_public_MCP(int? publicStatus, int expected)
    {
        Init();
        const string url = "https://rhythm-mcp.example.invalid/mcp";
        File.WriteAllText(deploy.Settings, YamlText.Set(File.ReadAllText(deploy.Settings), "engine.publicMcpUrl", YamlText.Scalar(url)));
        var emptyCheckout = new DeployFolder(Path.Combine(deploy.Root, "unmounted"));
        var mounts = new DoctorCommand.EngineMounts(deploy.Settings, deploy.Secret("engine_db"), deploy.Secret("app_token"));
        var calls = new List<string>();
        var output = new StringWriter();
        var code = await DoctorCommand.Run(emptyCheckout, output, (method, address, ct) =>
        {
            calls.Add(address);
            if (address == url) { Assert.Equal(HttpMethod.Post, method); return Task.FromResult(publicStatus); }
            return Stack(200)(method, address, ct);
        }, descriptors, mounts: mounts);
        Assert.Contains(url, calls);
        Assert.Equal(expected, code);
        Assert.DoesNotContain(".env is missing", output.ToString());
        Assert.DoesNotContain("rhythm.yaml is missing", output.ToString());
    }

    // SB-P2-01: with the stack up, the engine's version and each model's state from /health (ModelStatus), the contract's
    // example of app_status as the engine answers it. Notes only: an optional model never fails the check.
    [Fact] public async Task Doctor_shows_the_engine_version_and_each_models_state()
    {
        Init();
        var status = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "app_status.example.json")))!.AsObject();
        string Health(Action<System.Text.Json.Nodes.JsonObject>? change = null)
        {
            var health = new System.Text.Json.Nodes.JsonObject { ["status"] = "ok", ["embeddings"] = "on", ["adapters"] = new System.Text.Json.Nodes.JsonArray() };
            foreach (var (key, value) in status) health[key] = value?.DeepClone();
            change?.Invoke(health);
            return health.ToJsonString();
        }
        async Task<(int, string)> Run(string? body)
        {
            var o = new StringWriter();
            var code = await DoctorCommand.Run(deploy, o, Stack(200), descriptors,
                read: (url, _) => Task.FromResult(url == "http://engine:8080/health" ? body : null));
            return (code, o.ToString());
        }
        var (code, output) = await Run(Health());
        Assert.Equal(0, code);
        Assert.Contains("ok    engine 1.0.0, schema 26", output);
        Assert.Contains("ok    embeddings model minilm (live) is on: 1204 passages; the index job has 3 rows left", output);
        Assert.Contains("note  titles model fictional-model:1b is failing: timeout at 2026-10-28T07:00:00.0000000+00:00", output);
        // A model that could not be downloaded, and a paused index job, are notes; the check still passes.
        (code, output) = await Run(Health(h => h["models"]![0]!["state"] = "download-failed"));
        Assert.Equal(0, code);
        Assert.Contains("note  embeddings model minilm (live) could not be downloaded: search matches words only", output);
        (code, output) = await Run(Health(h => h["models"]![0]!["index"]!["pausedUntil"] = "2026-10-28T08:00:00+00:00"));
        Assert.Contains("the index job is paused until 2026-10-28T08:00:00+00:00", output);
        Assert.Contains("note  embeddings model minilm (live)'s index job is paused", output);
        // The titles model off, as every install has it unless it turns one on.
        (code, output) = await Run(Health(h => h["models"]![1] = System.Text.Json.Nodes.JsonNode.Parse(
            """{"job":"titles","role":"live","name":null,"id":null,"state":"off","index":null,"failure":null}""")));
        Assert.Contains("ok    titles model is off: stars keep their excerpts as titles", output);
        // An engine older than this command answers /health without models.
        (code, output) = await Run("""{"status":"ok","embeddings":"on","adapters":[]}""");
        Assert.Equal(0, code);
        Assert.Contains("note  the engine reports no model states", output);
        // Without a body reader (the stack's tests), nothing about models is said.
        output = (await Doctor(Stack(200))).Output;
        Assert.DoesNotContain("embeddings model", output);
        Assert.DoesNotContain("titles model", output);
    }

    [Fact] public async Task Doctor_finds_secrets_in_settings_missing_adapter_secrets_and_folders()
    {
        Init();
        var token = Secret("app_token");
        File.AppendAllText(deploy.Env, "GARMIN_API_KEY=" + Principals.NewToken() + "\n");
        File.WriteAllText(deploy.Settings, File.ReadAllText(deploy.Settings).Replace("name: Rhythm of Life", "name: " + token));
        Logbook();
        File.WriteAllText(deploy.Settings, YamlText.Set(File.ReadAllText(deploy.Settings), "adapters.logbook.enabled", "true"));
        File.Delete(deploy.Secret("backup_password"));
        Directory.CreateDirectory(deploy.Secret("backup_password"));
        var (code, output) = await Doctor();
        Assert.Equal(1, code);
        Assert.Contains(".env sets GARMIN_API_KEY", output);
        Assert.Contains("rhythm.yaml holds the value of one of the secret files", output);
        Assert.Contains("logbook_api_key is missing", output);
        Assert.Contains("profile logbook is not in COMPOSE_PROFILES", output);
        Assert.Contains("backup_password is a folder", output);
        Assert.DoesNotContain(token, output);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(deploy.Secret("engine_db"), Owners.Readable);
            Assert.Contains("engine_db is readable or writable by others", (await Doctor()).Output);
        }
    }

    // A capture token's file (rhythm capture connect) is a token's like any other: doctor checks its mode and looks for its
    // value where a secret must not be, and up makes it private. With no such file its answers are as they were.
    [Fact] public async Task Doctor_and_up_take_a_capture_tokens_file_as_every_tokens()
    {
        Init();
        var file = deploy.Secret("capture_demo-hook_token");
        var (_, without) = await Doctor();
        Assert.DoesNotContain("capture_", without);
        var token = Principals.NewToken();
        Owners.WriteNew(file, token, Owners.Token(deploy, file));
        Assert.Contains(file, deploy.Tokens);
        Assert.Equal(Owners.Person(deploy, Owners.Private), Owners.Token(deploy, file));

        var (code, output) = await Doctor();
        Assert.Equal(0, code);
        Assert.Contains("capture_demo-hook_token is there and private", output);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, Owners.Readable);
            (code, output) = await Doctor();
            Assert.Equal(1, code);
            Assert.Contains("capture_demo-hook_token is readable or writable by others", output);
            Assert.Equal(0, StackCommands.Preflight(deploy, new StringWriter()));
            Mode(file, Owners.Private);
        }

        // Its value in a settings file is a leak, named by where it is and never shown.
        File.AppendAllText(deploy.Env, "RHYTHM_DEMO_NOTE=" + token + "\n");
        (code, output) = await Doctor();
        Assert.Equal(1, code);
        Assert.Contains(".env holds the value of one of the secret files", output);
        Assert.DoesNotContain(token, output);
    }

    [Fact] public async Task Doctor_names_an_adapter_that_keeps_a_persons_records_under_the_household()
    {
        // The Second Brain personal workspace with owner household: private rows that Family reads once memory scopes
        // are on (docs/dev/memory.md section 2). A note, never a failure; a person as owner makes them that person's.
        Init();
        File.WriteAllText(deploy.Settings, YamlText.Set(File.ReadAllText(deploy.Settings), "adapters.second-brain.enabled", "true"));
        var (code, output) = await Doctor();
        Assert.Equal(0, code);
        Assert.Contains("note  adapter second-brain keeps its personal records as private rows under the household", output);
        File.WriteAllText(deploy.Settings, YamlText.Set(File.ReadAllText(deploy.Settings), "adapters.second-brain.owner", "alex"));
        Assert.DoesNotContain("adapter second-brain keeps", (await Doctor()).Output);
    }

    [Fact] public void Preflight_stops_before_init_and_prepares_empty_data_folders_after()
    {
        var o = new StringWriter();
        Assert.Equal(1, StackCommands.Preflight(deploy, o));
        Assert.Contains("run ./rhythm init first", o.ToString());
        Init("--data", "/srv/rhythm");
        var mounts = Path.Combine(root, "mounts");
        Directory.CreateDirectory(Path.Combine(mounts, "notes"));
        File.WriteAllText(Path.Combine(mounts, "notes", "kept.md"), "# A fictional note");
        Assert.Equal(0, StackCommands.Preflight(deploy, new StringWriter(), mounts));
        Assert.True(Directory.Exists(Path.Combine(mounts, "backups")));
        Assert.True(File.Exists(Path.Combine(mounts, "notes", "kept.md")));
        Mode(Path.Combine(mounts, "backups"), Owners.PrivateFolder);
        Assert.Equal("/srv/rhythm/import", Env()["RHYTHM_IMPORT_DIR"]);
    }

    [Fact] public void The_notes_descriptor_in_contracts_is_valid()
    {
        var shipped = AdapterDescriptor.Load(Path.Combine(AppContext.BaseDirectory, "adapters"));
        var notes = Assert.Single(shipped, d => d.Name == "notes");
        Assert.Empty(notes.Secrets);
        Assert.Equal(JsonValueKind.String, notes.Settings.Single(s => s.Key == "path").Default!.Value.ValueKind);
    }

    // Adapter secrets are the engine's, except the exposure containers' own: cloudflared's tunnel token and the OpenAI
    // tunnel client's key.
    [Fact] public void Each_adapter_secret_belongs_to_its_reader()
    {
        Assert.Equal(new Owners.Rule(Owners.Engine, Owners.Engine, Owners.Private), Owners.AdapterSecret("secrets/adapters/drive_read_token"));
        Assert.Equal(new Owners.Rule(Owners.Engine, Owners.Engine, Owners.Private), Owners.AdapterSecret("second-brain"));
        Assert.Equal(new Owners.Rule(Owners.Cloudflared, Owners.Cloudflared, Owners.Private), Owners.AdapterSecret("secrets/adapters/cloudflare_tunnel_token"));
        Assert.Equal(Owners.Cloudflared, Owners.AdapterSecret("cloudflare").Uid);
        Assert.Equal(new Owners.Rule(Owners.OpenAiTunnel, Owners.OpenAiTunnel, Owners.Private), Owners.AdapterSecret("secrets/adapters/openai-tunnel_key"));
    }

    // A harness token is the person's to hand to a client; a plugin's token belongs to the plugin container (uid 1000),
    // and the OpenAI tunnel's to the tunnel client that sends it.
    [Fact] public void A_plugin_token_belongs_to_the_plugin_container()
    {
        var deploy = new DeployFolder(Path.GetTempPath());
        Assert.Equal(new Owners.Rule(Owners.Plugin, Owners.Plugin, Owners.Private), Owners.Token(deploy, deploy.Secret("plugin_carelink_token")));
        Assert.Equal(Owners.Person(deploy, Owners.Private), Owners.Token(deploy, deploy.Secret("harness_assistant_token")));
        Assert.Equal(new Owners.Rule(Owners.OpenAiTunnel, Owners.OpenAiTunnel, Owners.Private), Owners.Token(deploy, deploy.Secret("harness_openai-tunnel_token")));
    }

    [Fact] public void The_openai_tunnel_descriptor_takes_a_tunnel_ID_a_key_and_its_own_token()
    {
        var tunnel = Assert.Single(AdapterDescriptor.Load(Path.Combine(AppContext.BaseDirectory, "adapters")), d => d.Name == "openai-tunnel");
        Assert.True(tunnel.HarnessToken);
        Assert.Equal("openai-tunnel", tunnel.Profile);
        var id = Assert.Single(tunnel.Settings);
        Assert.Matches(id.Pattern!, "tunnel_" + Guid.NewGuid().ToString("N"));
        Assert.DoesNotMatch(id.Pattern!, "sk-" + Principals.NewToken());
        Assert.Equal("key", Assert.Single(tunnel.Secrets).Purpose);
    }
}

// `rhythm ready` against a disposable database: the first harness token is made once, and a disabled one is reported.
public sealed class CliDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IDisposable
{
    readonly DeployFolder deploy = new(Path.Combine(Path.GetTempPath(), "rhythm-ready-" + Guid.NewGuid().ToString("N")[..10]));
    public void Dispose() { try { Directory.Delete(deploy.Root, true); } catch (IOException) { } }

    [PostgresFact] public async Task Ready_makes_one_first_token_and_says_when_a_token_file_stopped_working()
    {
        Directory.CreateDirectory(deploy.Root);
        Assert.Equal(0, InitCommand.Run(deploy, ["--no-input", "--people", "Alex"], new Prompter(new StringReader(""), new StringWriter(), true, false)));
        StackCommands.Probe up = (_, url, _) => Task.FromResult<int?>(url.EndsWith("/mcp", StringComparison.Ordinal) ? 401 : 200);
        var principals = new Principals(fixture.Source);
        var o = new StringWriter();
        Assert.Equal(0, await StackCommands.Ready(deploy, o, up, () => principals));
        var file = deploy.Secret("harness_assistant_token");
        var token = File.ReadAllText(file);
        Assert.True(await principals.Enabled(token, default));
        Assert.Contains("Harness token file: deploy/compose/secrets/harness_assistant_token", o.ToString());
        Assert.Contains("MCP:  http://127.0.0.1:8181/mcp", o.ToString());
        Assert.DoesNotContain(token, o.ToString());

        Assert.Equal(0, await StackCommands.Ready(deploy, new StringWriter(), up, () => principals));
        Assert.Equal(token, File.ReadAllText(file));
        await principals.Revoke("assistant", default);
        o = new StringWriter();
        Assert.Equal(0, await StackCommands.Ready(deploy, o, up, () => principals));
        Assert.Contains("no longer valid", o.ToString());
        // The OpenAI tunnel's token is listed as its container's, not one to copy into a client.
        await principals.Create("openai-tunnel", "harness", null, deploy.Secret(Owners.TunnelToken), default);
        o = new StringWriter();
        Assert.Equal(0, await StackCommands.Ready(deploy, o, up, () => principals));
        Assert.Contains("harness_openai-tunnel_token (the OpenAI tunnel's own: its container sends it)", o.ToString());
        Assert.Equal(1, await StackCommands.Ready(deploy, new StringWriter(), (_, _, _) => Task.FromResult<int?>(null), () => principals));
    }

}
