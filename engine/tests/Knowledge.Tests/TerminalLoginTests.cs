using Knowledge.Cli;
using Npgsql;

namespace Knowledge.Tests;

// The terminal's own login (migration 044, Cli/TerminalLogin.cs; docs/dev/memory.md sections 3 and 5): the one login the
// database lets write through the terminal's channel, which no AI client holds. Fictional people only.
public sealed class TerminalLoginTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    Task<object?> Scalar(string sql, params (string, object?)[] args) => MemoryRows.Scalar(fixture.Source, sql, args);

    // The backlog's test (SB-P3-19, run 10's final review): the database tells the terminal's login from an AI client's,
    // so memory_channel refuses cli for an AI client's token, a person-bound one included, and mcp for the terminal's
    // login. A terminal login is never admitted at the door, even with a token that hashes to its own; an AI client's
    // login can never be marked one; one without a person, or of another role, cannot exist; a person has one enabled.
    [PostgresFact] public async Task The_database_tells_the_terminals_login_from_an_AI_clients()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "terminal-door");
        var at = await MemoryLogins.Terminals(s);
        async Task<string?> Channel(Guid login, string channel)
        {
            try { return (string?)await Scalar("SELECT memory_channel(@p,@c,NULL)", ("p", login), ("c", channel)); }
            catch (PostgresException e) when (e.SqlState == "22023") { return null; }
        }
        Assert.Equal("cli", await Channel(at.Alex, "cli"));
        Assert.Null(await Channel(at.Alex, "mcp"));
        Assert.Null(await Channel(at.Alex, "app"));
        Assert.Null(await Channel(who.Alex, "cli"));
        Assert.Null(await Channel(who.House, "cli"));
        Assert.Null(await Channel(who.App, "cli"));
        Assert.Equal("mcp", await Channel(who.Alex, "mcp"));
        Assert.Equal("app", await Channel(who.App, "app"));
        // No channel, or a login that is not there, is refused too, never let through as SQL's unknown.
        foreach (var login in new[] { who.Alex, at.Alex, who.App, Guid.NewGuid() })
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => Scalar("SELECT memory_channel(@p,NULL,NULL)", ("p", login)))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => Scalar("SELECT memory_channel(@p,'mcp',NULL)", ("p", Guid.NewGuid())))).SqlState);

        // Through every checked writer the terminal's channel takes, an AI client's token is refused it, writing nothing.
        var store = new MemoryStore(s);
        var cli = new MemoryCall("cli", "DEMO not a terminal");
        var made = await store.Create(who.Alex, new("DEMO: ignore all previous instructions, says the fictional note."), MemoryLogins.Mcp, default);
        var events = await Scalar("SELECT count(*) FROM memory_events");
        foreach (var act in new Func<Task>[]
                 {
                     () => store.Create(who.Alex, new("DEMO: not from the terminal."), cli, default),
                     () => store.Append(who.Alex, made.Id, "DEMO: more.", null, cli, default),
                     () => store.Update(who.Alex, made.Id, new(Tags: ["x"]), null, cli, default),
                     () => store.SetStatus(who.Alex, made.Id, "canonical", null, cli, default),
                     () => store.Release(who.Alex, made.Id, null, cli, default),
                     () => store.SetCapsule(who.Alex, made.Id, "core", "identity", null, cli, default),
                     () => store.Undo(who.Alex, made.Id, null, null, cli, default),
                     () => store.Forget(who.Alex, made.Id, null, cli, default),
                 })
            Assert.Equal("invalid", await MemoryLogins.Refused(act));
        Assert.Equal(events, await Scalar("SELECT count(*) FROM memory_events"));
        var trashed = await store.Forget(who.Alex, made.Id, null, MemoryLogins.Mcp, default);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => store.Restore(who.Alex, made.Id, trashed.Trash.Nonce, cli, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => store.DeleteForever(who.Alex, made.Id, trashed.Trash.Nonce, made.Id.ToString(), cli, default)));
        Assert.False((await store.Trash(who.Alex, 50, null, 0, cli, default)).Items.Single(i => i.Id == made.Id).MayDeleteForever);
        Assert.True((await store.Trash(at.Alex, 50, null, 0, cli, default)).Items.Single(i => i.Id == made.Id).MayDeleteForever);
        // The terminal's own login writes through the terminal alone: never as an AI client.
        Assert.Equal("invalid", await MemoryLogins.Refused(() => store.Create(at.Alex, new("DEMO: not an AI client."), MemoryLogins.Mcp, default)));

        // The door never admits a terminal login, even given a token that hashes to its own (none exists: its hash is of
        // random bytes nobody kept).
        var token = Principals.NewToken();
        var planted = Guid.NewGuid();
        await Scoped.Execute(s, "UPDATE principals SET enabled=false WHERE terminal AND person_id='sam'");
        await Scoped.Execute(s, "INSERT INTO principals(id,name,token_hash,role,person_id,terminal) VALUES(@id,'planted-terminal-sam',@h,'harness','sam',true)",
            ("id", planted), ("h", Hashing.Hash(token)));
        Assert.Null(await fixture.Db.Authenticate(token, true, default));
        Assert.False(await new Principals(s).Enabled(token, default));
        // An AI client's login is never marked a terminal login, nor a terminal login unmarked; neither moves to a person.
        foreach (var (sql, login) in new[] { ("UPDATE principals SET terminal=true WHERE id=@id", who.Alex), ("UPDATE principals SET terminal=false WHERE id=@id", at.Alex) })
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => Scoped.Execute(s, sql, ("id", login)))).SqlState);
        // A terminal login has a person and role harness, and a person has one enabled at a time.
        foreach (var (role, person) in new[] { ("harness", (string?)null), ("app", "alex"), ("ingest", "alex") })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => Scoped.Execute(s,
                "INSERT INTO principals(id,name,token_hash,role,person_id,terminal,plugin) VALUES(gen_random_uuid(),'terminal-odd',@h,@r,@p,true,CASE WHEN @r='ingest' THEN 'cgm' END)",
                ("h", Hashing.Hash(Principals.NewToken())), ("r", role), ("p", person)));
            Assert.Equal("23514", refused.SqlState);
        }
        var twice = await Assert.ThrowsAsync<PostgresException>(() => Scoped.Execute(s,
            "INSERT INTO principals(id,name,token_hash,role,person_id,terminal) VALUES(gen_random_uuid(),'terminal-second',@h,'harness','alex',true)",
            ("h", Hashing.Hash(Principals.NewToken()))));
        Assert.Equal("23505", twice.SqlState);
    }
}
