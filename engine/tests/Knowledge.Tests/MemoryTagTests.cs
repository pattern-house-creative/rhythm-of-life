namespace Knowledge.Tests;

// How many memories a list holds and the tags they carry (Core/MemoryTags.cs; SB-P3-17): each count equals its list,
// read through the same predicates, and a tag comes only from what the reader reads. Fictional people only: alex, sam
// and the household; every text is DEMO.
public sealed class MemoryTagTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    // A list paged through to its end.
    async Task<List<MemoryRecord>> Listed(Guid reader, MemoryFilter filter)
    {
        var all = new List<MemoryRecord>();
        MemoryCursor? after = null;
        do
        {
            var page = await Store.List(reader, filter with { Limit = 2, After = after }, default);
            all.AddRange(page.Items);
            after = page.Next;
        } while (after is not null);
        return all;
    }

    // What a count and a vocabulary must say, from the list itself: its length, and each tag of what it holds with how
    // many hold it (a held memory is listed without its tags). Then each tag's count is the list that filters by it too.
    async Task Agree(Guid reader, MemoryFilter filter)
    {
        var listed = await Listed(reader, filter);
        Assert.Equal(listed.Count, await Store.Count(reader, filter, default));
        var tags = await Store.Tags(reader, filter, default);
        var tally = listed.SelectMany(m => m.Tags).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(tally.OrderBy(p => p.Key, StringComparer.Ordinal), tags.Select(t => KeyValuePair.Create(t.Tag, t.Count)).OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(tags.OrderByDescending(t => t.Count).ThenBy(t => t.Tag, StringComparer.Ordinal), tags);
        foreach (var tag in tags)
            Assert.Equal(tag.Count, (await Listed(reader, filter with { Tags = [.. filter.Tags ?? [], tag.Tag] })).Count);
    }

    [PostgresFact] public async Task Each_count_equals_its_list_and_a_tag_comes_only_from_what_the_reader_reads()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "tags");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var since = await MemoryLogins.Now(s);
            async Task<Guid> Make(Guid by, MemoryDraft draft) => (await Store.Create(by, draft, MemoryLogins.Mcp, default)).Id;
            await Make(who.Alex, new("DEMO: the towpath walk #walks", ["garden-plot"], Kind: "episodic", Project: "lockside"));
            await Make(who.Alex, new("DEMO: the canal loop", ["walks"], Kind: "semantic"));
            await Make(who.Alex, new("DEMO: the fictional cafe #walks", ["cafe"], Scope: "shared"));
            await Make(who.Sam, new("DEMO: Sam's own fictional route", ["walks", "sams-route"]));
            await Make(who.House, new("DEMO: bins on Thursday", ["bins"], Scope: "shared"));
            var held = await Make(who.Alex, new("DEMO: held text", ["held-only", "walks"], Scope: "shared"));
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions',hold_score=0.9 WHERE id=@id", ("id", held));
            var trashed = await Make(who.Alex, new("DEMO: forgotten", ["trash-only"], Scope: "shared"));
            await Scoped.Execute(s, """
                INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after) VALUES(@id,'forget',@p,'mcp',now()+interval '14 days');
                UPDATE resources SET status='deleted' WHERE id=@id
                """, ("id", trashed), ("p", who.Alex));

            foreach (var reader in new[] { who.Alex, who.Sam, who.House, who.App })
                foreach (var filter in new MemoryFilter[] { new(), new(Scope: "private"), new(Scope: "Family"), new(Tags: ["walks"]),
                    new(Kind: "semantic"), new(Status: "draft"), new(Author: "alex"), new(Project: "lockside"), new(Before: since) })
                {
                    if (filter.Project is not null && reader != who.Alex) continue;
                    await Agree(reader, filter with { Since = since });
                }

            // Alex reads her own private memories and Family's; never Sam's private ones, a held memory's tags or the trash.
            Assert.Equal("walks:3 bins:1 cafe:1 garden-plot:1", Words(await Store.Tags(who.Alex, new(Since: since), default)));
            Assert.Equal("walks:2 bins:1 cafe:1 sams-route:1", Words(await Store.Tags(who.Sam, new(Since: since), default)));
            Assert.Equal("bins:1 cafe:1 walks:1", Words(await Store.Tags(who.House, new(Since: since), default)));
            Assert.Equal("garden-plot:1 walks:1", Words(await Store.Tags(who.Alex, new(Since: since, Project: "lockside"), default)));
            // The held memory is counted where a list holds it, unfiltered, and never by a tag it holds.
            Assert.Equal((5, 3), (await Store.Count(who.Alex, new(Since: since), default), await Store.Count(who.House, new(Since: since), default)));
            Assert.Equal(3, await Store.Count(who.Alex, new(Since: since, Tags: ["walks"]), default));
            Assert.Equal(0, await Store.Count(who.Alex, new(Since: since, Tags: ["held-only"]), default));
            Assert.Equal(0, await Store.Count(who.Alex, new(Since: since, Tags: ["trash-only"]), default));
            // A project the reader reads none of is refused, as a list refuses it.
            Assert.Equal("unknown_project", await MemoryLogins.Refused(() => Store.Count(who.Sam, new(Project: "lockside"), default)));
            Assert.Equal("unknown_project", await MemoryLogins.Refused(() => Store.Tags(who.Sam, new(Project: "lockside"), default)));

            // With scopes off the household shares everything: one vocabulary, the same for every reader.
            await Scoped.Mode(s, "off");
            foreach (var reader in new[] { who.Alex, who.Sam, who.House })
            {
                Assert.Equal("walks:4 bins:1 cafe:1 garden-plot:1 sams-route:1", Words(await Store.Tags(reader, new(Since: since), default)));
                await Agree(reader, new(Since: since));
            }
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    static string Words(IEnumerable<MemoryTagCount> tags) => string.Join(" ", tags.Select(t => $"{t.Tag}:{t.Count}"));
}
