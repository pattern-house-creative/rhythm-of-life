using System.Data;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge.Cli;

// The file `rhythm export` writes and `rhythm import` reads: native memories in Rhythm's own format, full fidelity
// (SB-P3-14, D-035; docs/dev/memory.md section 9; cli/README.md, Export and import). Version 1 carries, of what one
// reader reads: each project with its id, scope, slug, name, description, status, author, aliases, revision and dates,
// and, for a project a held write made that no person has let in yet (046), its hold: held and the memory it was held
// for (SB-P3-23), so an import keeps it out of every list there too; a project not held carries neither, as before;
// each memory with its id, scope, author, source, text, tags, kind, status, importance, volatility, project, capsule
// slot, hold, validity, revision and dates, its earlier versions as that reader's history shows them, its trash dates
// when it is in the trash, and for a digest (054) the key it was drawn for, with its basis and source count, from its
// audit trail, and for a commitment (058) its direction and counterparty, and for a decision (060) its confidence and
// its states from that reader's history on, and for a memory with a due date (061) its moment, kind and source, and for
// a work item (079) its type, state and priority and its states from that reader's history on; and each
// person- or AI-made link between two of those memories, with its confidence,
// source revision and checked time when it has them (051; a link with none reads as before). Never another
// reader's records, the audit trail, the logins that wrote anything, idempotency keys, passages, vectors, the map's
// stars or links the engine found: those are this install's own, rebuilt or kept here.
//
// An export reads as a client bound to its reader would: through the one read predicate (Database.Visible on each
// memory's own row, 034's history floor on its versions, the trash list's rule on what is in the trash), as a principal
// made for the export inside its own transaction, which is rolled back, so no token for it ever exists. An import writes
// only through migration 043's checked functions (import_memory_link as 051 replaced it), as the operator.
//
// One record a line, so two exports of the same records differ only in their install, export and exportedAt lines.
public sealed record ExportSummary(string Install, Guid Export, string? Person, bool Scopes, int Projects, int Memories, int Trashed,
    int Held, int Links, long LinksLeftOut, IReadOnlyDictionary<string, int> PerScope);

// What an import did (or would do, in a dry run): per kind (project, memory, link), each outcome and refusal's count.
public sealed record ImportReport(string Origin, bool SameInstall, bool DryRun, bool Scopes, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Outcomes,
    int VersionsKept, int VersionsLeftOut, int HeldHere, int AliasesLeftOut, IReadOnlyList<Guid>? Added = null)
{
    public int Count(string kind, string outcome) =>
        Outcomes.TryGetValue(kind, out var o) && o.TryGetValue(outcome, out var n) ? n : 0;
}

public sealed class ArchiveInvalid(string message) : Exception(message);

public sealed class MemoryArchive(NpgsqlDataSource source)
{
    public const string Format = "rhythm-memories";
    public const int Version = 1;

    // The memories a reader reads: through the read predicate on each one's own row, and of those in the trash, the ones
    // its trash list shows it (whoever may restore them, memory.md section 5).
    static string Readable() => $"""
        SELECT m.id FROM memories m JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
        WHERE {Database.Visible("r")}
        UNION
        SELECT m.id FROM memories m JOIN memory_trash t ON t.memory_id=m.id
        WHERE memory_may_change(@principal,m.subject_id,m.author_id,scopes_on())
        """;

    // A held project's hold (SB-P3-23) only on a held one, so every other project's line is as before.
    const string ProjectJson = """
        (jsonb_build_object('id',p.id,'subject',p.subject_id,'scope',CASE WHEN p.subject_id='household' THEN 'shared' ELSE 'private' END,
          'slug',p.slug,'name',p.name,'description',p.description,'status',p.status,'author',p.author_id,
          'aliases',to_jsonb(ARRAY(SELECT a.alias FROM memory_project_aliases a WHERE a.project_id=p.id ORDER BY a.alias)),
          'revision',p.revision,'createdAt',p.created_at,'updatedAt',p.updated_at)
          ||CASE WHEN p.held THEN jsonb_build_object('held',true,'heldFor',p.held_for) ELSE '{}'::jsonb END)
        """;

    // A memory with its versions from the reader's history floor (034) on, and its trash dates; and, only for a digest (the
    // engine's own memory, source system, whose created or imported audit row names one: 054), its digest object, the key
    // it was drawn for, its basis and source count, so an import keeps it the digest of its key; and, only for a
    // commitment (058), its direction and counterparty, so an import keeps it owed to or by the person; and, only for a
    // decision (060), its confidence and source and its states (outcome, re-asks and review date) at each revision from the
    // reader's history floor on, the state at the floor written at the floor, so an import keeps its outcome and an undo
    // there goes back through it; and, only for a memory with a due date (061), its moment (UTC), kind and source, so an
    // import keeps it due then; and, only for a standing instruction (065), standing true, so it fires there as here; and,
    // only for a work item (079), its type, state, priority and assignee (081: a person's id or an agent's label; no claim,
    // which is this install's and short-lived) and its states (the properties from each revision on, none for a stretch it
    // was no work item) from the reader's history floor on, the state at the floor written at the floor, so an
    // import keeps where it stands and an undo there goes back through it. Every other line is as before.
    const string MemoryJson = """
        (jsonb_build_object('id',m.id,'subject',m.subject_id,'scope',m.scope,'author',m.author_id,'source',m.source,
          'content',m.content,'tags',to_jsonb(m.tags),'kind',m.kind,'status',m.status,'importance',m.importance,
          'volatility',m.volatility,'project',m.project_id,'capsule',m.capsule,'capsuleSlot',m.capsule_slot,
          'hold',m.hold,'holdScore',m.hold_score,'validFrom',m.valid_from,'validUntil',m.valid_until,
          'revision',m.revision,'createdAt',m.created_at,'updatedAt',m.updated_at,
          'versions',coalesce((SELECT jsonb_agg(jsonb_build_object('revision',v.revision,'subject',v.subject_id,'scope',v.scope,
              'content',v.content,'tags',to_jsonb(v.tags),'status',v.status,'validFrom',v.valid_from,'validUntil',v.valid_until,
              'state',v.state,'writtenAt',v.written_at,'retiredAt',v.retired_at) ORDER BY v.revision)
            FROM memory_versions v WHERE v.memory_id=m.id AND v.revision>=f.floor),'[]'::jsonb),
          'trash',(SELECT jsonb_build_object('reason',t.reason,'trashedAt',t.trashed_at,'purgeAfter',t.purge_after)
            FROM memory_trash t WHERE t.memory_id=m.id))
          ||jsonb_strip_nulls(jsonb_build_object('digest',(SELECT e.detail->'digest' FROM memory_events e
            WHERE e.memory_id=m.id AND m.source='system' AND e.event IN ('created','imported') AND jsonb_typeof(e.detail->'digest')='object'
            ORDER BY e.id LIMIT 1)))
          ||jsonb_strip_nulls(jsonb_build_object('commitment',(SELECT jsonb_build_object('direction',k.direction,'counterparty',k.counterparty)
            FROM memory_commitments k WHERE k.memory_id=m.id)))
          ||jsonb_strip_nulls(jsonb_build_object('decision',(SELECT jsonb_build_object('confidence',d.confidence,'confidenceSource',d.confidence_source,
              'states',(SELECT jsonb_agg(jsonb_build_object('revision',x.revision,'outcome',x.outcome,'rearms',x.rearms,'reviewAt',x.review_at)
                ORDER BY x.revision) FROM (SELECT greatest(s.revision,f.floor) AS revision,s.outcome,s.rearms,s.review_at
                FROM memory_decision_states s WHERE s.memory_id=m.id AND (s.revision>=f.floor OR s.revision=(SELECT max(z.revision)
                  FROM memory_decision_states z WHERE z.memory_id=m.id AND z.revision<=f.floor))) x))
            FROM memory_decisions d WHERE d.memory_id=m.id)))
          ||jsonb_strip_nulls(jsonb_build_object('due',CASE WHEN m.due_at IS NOT NULL THEN jsonb_build_object(
            'at',to_char(m.due_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),'kind',m.due_kind,'source',m.due_source) END))
          ||jsonb_strip_nulls(jsonb_build_object('standing',CASE WHEN m.standing THEN true END))
          ||jsonb_strip_nulls(jsonb_build_object('work',(SELECT jsonb_build_object('type',k.work_type,'state',k.state,'priority',k.priority,
              'assignee',memory_work_assignee_json(k.assignee_person,k.assignee_label),
              'states',(SELECT jsonb_agg(jsonb_build_object('revision',x.revision,'type',x.work_type,'state',x.state,'priority',x.priority,
                  'assignee',memory_work_assignee_json(x.assignee_person,x.assignee_label))
                ORDER BY x.revision) FROM (SELECT greatest(s.revision,f.floor) AS revision,s.work_type,s.state,s.priority,s.assignee_person,s.assignee_label
                FROM memory_work_states s WHERE s.memory_id=m.id AND (s.revision>=f.floor OR s.revision=(SELECT max(z.revision)
                  FROM memory_work_states z WHERE z.memory_id=m.id AND z.revision<=f.floor))) x))
            FROM memory_work k WHERE k.memory_id=m.id))))
        """;

    // Person- and AI-made links whose two ends are memories of the export in one scope: those in made_links, and those a
    // trashed memory's trash row keeps to restore with it (named by that memory). A link's confidence, source revision and
    // checked time (051) only when it has them, so a link with none is the line it was.
    const string LinkJson = """
        SELECT j::text FROM (
          SELECT jsonb_build_object('id',l.link_id,'subject',s.subject_id,'source',l.source_id,'target',l.target_id,'type',l.type,
            'verification',l.verification,'evidence',l.evidence,'lifecycle',l.lifecycle,'createdAt',l.created_at,'trash',NULL::uuid)
            ||jsonb_strip_nulls(jsonb_build_object('confidence',l.confidence,'sourceRevision',l.source_revision,'checkedAt',l.checked_at)) AS j
          FROM made_links l JOIN memories s ON s.id=l.source_id JOIN memories t ON t.id=l.target_id
          WHERE l.source_id=ANY(@ids) AND l.target_id=ANY(@ids) AND s.subject_id=t.subject_id
          UNION ALL
          SELECT jsonb_build_object('id',l.link_id,'subject',s.subject_id,'source',l.source_id,'target',l.target_id,'type',l.type,
            'verification',l.verification,'evidence',l.evidence,'lifecycle',l.lifecycle,'createdAt',l.created_at,'trash',x.memory_id)
            ||jsonb_strip_nulls(jsonb_build_object('confidence',l.confidence,'sourceRevision',l.source_revision,'checkedAt',l.checked_at))
          FROM memory_trash x CROSS JOIN LATERAL jsonb_populate_recordset(NULL::made_links,x.links) l
          JOIN memories s ON s.id=l.source_id JOIN memories t ON t.id=l.target_id
          WHERE x.memory_id=ANY(@ids) AND l.source_id=ANY(@ids) AND l.target_id=ANY(@ids) AND s.subject_id=t.subject_id) k
        ORDER BY j->>'createdAt',j->>'id'
        """;

    // Reads as `person` would (null: the household view, a client with no person) and writes the file to `output`.
    public async Task<ExportSummary> Export(string? person, TextWriter output, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        // One snapshot for the whole file, and every date in UTC, so the same records always read the same.
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Database.Execute(c, tx, "SET LOCAL TimeZone='UTC'", ct);
        var reader = Guid.NewGuid();
        await using (var made = Database.Command(c, tx, "INSERT INTO principals(id,name,token_hash,role,person_id) VALUES(@id,@n,@h,'harness',@person)",
            ("id", reader), ("n", "export-" + reader.ToString("N")), ("h", Hashing.Hash(Principals.NewToken())), ("person", person)))
        {
            made.Parameters["person"].NpgsqlDbType = NpgsqlDbType.Text;
            await made.ExecuteNonQueryAsync(ct);
        }
        string install, at;
        bool scopes;
        await using (var head = Database.Command(c, tx,
            "SELECT (SELECT value FROM install_settings WHERE key='install.id'),to_jsonb(now())#>>'{}',scopes_on()"))
        await using (var row = await head.ExecuteReaderAsync(ct))
        {
            await row.ReadAsync(ct);
            install = row.IsDBNull(0) ? throw new InvalidOperationException("This install has no id (install_settings install.id): run ./rhythm up first.") : row.GetString(0);
            at = row.GetString(1);
            scopes = row.GetBoolean(2);
        }
        var ids = new List<Guid>();
        await using (var cmd = Database.Command(c, tx, Readable(), ("principal", reader)))
        await using (var row = await cmd.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct)) ids.Add(row.GetGuid(0));
        var export = Guid.NewGuid();
        output.Write($"{{\"format\":{Json(Format)},\"version\":{Version},\n");
        output.Write($"\"install\":{Json(install)},\n\"export\":{Json(export.ToString())},\n\"exportedAt\":{Json(at)},\n");
        output.Write($"\"reader\":{{\"person\":{(person is null ? "null" : Json(person))}}},\"scopes\":{Json(scopes ? "on" : "off")},\n");

        var projects = await Lines(c, tx, output, "projects",
            $"SELECT {ProjectJson}::text FROM memory_projects p WHERE {MemoryStore.ProjectAllowed()} ORDER BY p.subject_id,p.slug,p.id",
            ct, ("principal", reader));
        int memories = 0, trashed = 0, held = 0;
        var perScope = new SortedDictionary<string, int>(StringComparer.Ordinal);
        output.Write("\"memories\":[");
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT {MemoryJson}::text,m.subject_id,m.hold IS NOT NULL,EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
            FROM memories m CROSS JOIN LATERAL (SELECT memory_history_floor(@principal,m.id) AS floor) f
            WHERE m.id=ANY(@ids) ORDER BY m.created_at,m.id
            """, ("principal", reader), ("ids", ids.ToArray())))
        await using (var row = await cmd.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct))
            {
                output.Write(memories++ == 0 ? "\n" : ",\n");
                output.Write(row.GetString(0));
                var scope = row.GetString(1) == "household" ? "Family" : row.GetString(1);
                perScope[scope] = perScope.GetValueOrDefault(scope) + 1;
                if (row.GetBoolean(2)) held++;
                if (row.GetBoolean(3)) trashed++;
            }
        output.Write("\n],\n");
        var links = await Lines(c, tx, output, "links", LinkJson, ct, ("ids", ids.ToArray()));
        long touching;
        await using (var cmd = Database.Command(c, tx, """
            SELECT (SELECT count(*) FROM made_links l WHERE l.source_id=ANY(@ids) OR l.target_id=ANY(@ids))
              +(SELECT count(*) FROM memory_trash x,jsonb_array_elements(x.links) e WHERE x.memory_id=ANY(@ids))
            """, ("ids", ids.ToArray())))
            touching = (long)(await cmd.ExecuteScalarAsync(ct))!;
        output.Write($"\"counts\":{{\"projects\":{projects},\"memories\":{memories},\"links\":{links}}}}}\n");
        await output.FlushAsync(ct);
        await tx.RollbackAsync(ct);
        return new(install, export, person, scopes, projects, memories, trashed, held, links, touching - links, perScope);
    }

    // One array of the file, a record a line, then its count.
    static async Task<int> Lines(NpgsqlConnection c, NpgsqlTransaction tx, TextWriter output, string name, string sql, CancellationToken ct,
        params (string, object?)[] args)
    {
        var n = 0;
        output.Write($"\"{name}\":[");
        await using (var cmd = Database.Command(c, tx, sql, args))
        await using (var row = await cmd.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct))
            {
                output.Write(n++ == 0 ? "\n" : ",\n");
                output.Write(row.GetString(0));
            }
        output.Write("\n],\n");
        return n;
    }

    // A header value as JSON: escaped where JSON needs it, and no more (a time zone's + as itself).
    static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string Json(string value) => JsonSerializer.Serialize(value, Relaxed);

    // Whether every value of a record is one the database's jsonb can hold: no string, field names included, holds a NUL
    // or half a surrogate pair, which JSON can write and .NET can read but no database text holds, and no number is past
    // what numeric holds (Numeric) (SB-P7-10). Sent, any of them would be refused by the server as the record arrives,
    // which would end the whole import's transaction; so a record that holds one is refused here as invalid, as any
    // other bad record is, and the rest of the file comes in.
    public static bool Storable(JsonElement value)
    {
        try
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String: return !value.GetString()!.Contains('\0');
                case JsonValueKind.Number: return Numeric(value.GetRawText());
                case JsonValueKind.Array: return value.EnumerateArray().All(Storable);
                case JsonValueKind.Object:
                    return value.EnumerateObject().All(p => !p.Name.Contains('\0') && Storable(p.Value));
                default: return true;
            }
        }
        catch (InvalidOperationException) { return false; }
    }

    // Whether a JSON number, as written, is one PostgreSQL's numeric (which jsonb keeps numbers as) takes: its exponent
    // below 2^30 - 1, at most 16383 digits after the point as written (trailing zeros count), and its first digit that is
    // not zero at most 131071 places before the point (131072 digits). Past any of them the server answers 22003 or 22P02.
    public static bool Numeric(string text)
    {
        var e = text.AsSpan().IndexOfAny('e', 'E');
        long exponent = 0;
        if (e >= 0 && !long.TryParse(text.AsSpan(e + 1), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out exponent))
            return false;
        if (exponent >= int.MaxValue / 2 || exponent <= -(int.MaxValue / 2)) return false;
        var mantissa = (e >= 0 ? text[..e] : text).TrimStart('-');
        var point = mantissa.IndexOf('.');
        var whole = point < 0 ? mantissa : mantissa[..point];
        var fraction = point < 0 ? "" : mantissa[(point + 1)..];
        if (Math.Max(0, fraction.Length - exponent) > 16383) return false;
        var first = (whole + fraction).AsSpan().IndexOfAnyExcept('0');
        return first < 0 || whole.Length - 1 - first + exponent < 131072;
    }

    // What a file holds, checked before anything is written: the format and version, its origin, three arrays and the
    // counts it ends with (a file cut short or edited by hand is refused).
    public static (JsonElement Projects, JsonElement Memories, JsonElement Links, string Origin, string Install) Read(JsonDocument file)
    {
        // Half a surrogate pair in a field of the file's own (a record holding one is refused on its own, Storable) cannot
        // be read as text, as SbFile.Read says of a Second Brain file.
        try { return Fields(file.RootElement); }
        catch (InvalidOperationException) { throw new ArchiveInvalid("it holds text that cannot be read (half a surrogate pair)"); }
    }

    static (JsonElement Projects, JsonElement Memories, JsonElement Links, string Origin, string Install) Fields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String
            || format.GetString() != Format)
            throw new ArchiveInvalid($"not a Rhythm memory export (format {Format})");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != Version)
            throw new ArchiveInvalid($"an export of another version: this one reads version {Version}");
        string Id(string name) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && Guid.TryParseExact(e.GetString(), "D", out var g)
            ? g.ToString() : throw new ArchiveInvalid($"its {name} is not an id");
        var install = Id("install");
        Id("export");
        if (!root.TryGetProperty("exportedAt", out var at) || at.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(at.GetString(),
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new ArchiveInvalid("it does not say when it was made");
        JsonElement Array(string name) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array ? e
            : throw new ArchiveInvalid($"it has no {name} list");
        var (projects, memories, links) = (Array("projects"), Array("memories"), Array("links"));
        if (!root.TryGetProperty("counts", out var counts) || counts.ValueKind != JsonValueKind.Object)
            throw new ArchiveInvalid("it does not end with its counts, so it may be cut short");
        foreach (var (name, list) in new[] { ("projects", projects), ("memories", memories), ("links", links) })
            if (!counts.TryGetProperty(name, out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var count) || count != list.GetArrayLength())
                throw new ArchiveInvalid($"its {name} are not as many as its counts say, so it was cut short or changed");
        return (projects, memories, links, "rhythm:" + install, install);
    }

    // Imports `file` for `person` (null: the household view) into the scopes `own` (that person's private scope) and
    // `family` name, in one transaction: committed, or rolled back for a dry run.
    public async Task<ImportReport> Import(JsonDocument file, string? person, bool own, bool family, bool dryRun, CancellationToken ct)
    {
        var (projects, memories, links, origin, install) = Read(file);
        var root = file.RootElement;
        var originJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["origin"] = origin, ["export"] = root.GetProperty("export").GetString()!, ["exportedAt"] = root.GetProperty("exportedAt").GetString()!,
        });
        var outcomes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        void Count(string kind, string outcome)
        {
            var o = outcomes.TryGetValue(kind, out var x) ? x : outcomes[kind] = new(StringComparer.Ordinal);
            o[outcome] = o.GetValueOrDefault(outcome) + 1;
        }
        int versionsKept = 0, versionsLeftOut = 0, heldHere = 0, aliasesLeftOut = 0;
        bool same, scopes;
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var here = Database.Command(c, tx, "SELECT (SELECT value FROM install_settings WHERE key='install.id'),scopes_on()"))
        await using (var row = await here.ExecuteReaderAsync(ct))
        {
            await row.ReadAsync(ct);
            same = !row.IsDBNull(0) && row.GetString(0) == install;
            scopes = row.GetBoolean(1);
        }

        async Task<JsonElement> Call(string sql, string record, params (string, object?)[] more)
        {
            await using var cmd = Database.Command(c, tx, sql, [("person", person), ("own", own), ("family", family), ("record", record), .. more]);
            cmd.Parameters["person"].NpgsqlDbType = NpgsqlDbType.Text;
            cmd.Parameters.Add(new NpgsqlParameter("origin", NpgsqlDbType.Jsonb) { Value = originJson });
            cmd.Parameters["record"].NpgsqlDbType = NpgsqlDbType.Jsonb;
            foreach (NpgsqlParameter p in cmd.Parameters) if (p.ParameterName == "projects") p.NpgsqlDbType = NpgsqlDbType.Jsonb;
            using var answer = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
            return answer.RootElement.Clone();
        }
        static string Outcome(JsonElement a) => a.GetProperty("outcome").GetString() is "refused" ? "refused:" + a.GetProperty("reason").GetString()
            : a.GetProperty("outcome").GetString()!;

        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in projects.EnumerateArray())
        {
            if (!Storable(p)) { Count("project", "refused:invalid"); continue; }
            var a = await Call("SELECT import_memory_project(@person,@own,@family,@origin,@record)::text", p.GetRawText());
            Count("project", Outcome(a));
            if (a.TryGetProperty("id", out var id) && p.TryGetProperty("id", out var from) && from.ValueKind == JsonValueKind.String)
                mapped[from.GetString()!] = id.GetString()!;
            if (a.TryGetProperty("aliasesLeftOut", out var left)) aliasesLeftOut += left.GetInt32();
        }
        var map = JsonSerializer.Serialize(mapped);
        var added = new List<Guid>();
        foreach (var m in memories.EnumerateArray())
        {
            if (!Storable(m)) { Count("memory", "refused:invalid"); continue; }
            var a = await Call("SELECT import_memory(@person,@own,@family,@origin,@record,@projects)::text", m.GetRawText(), ("projects", map));
            Count("memory", Outcome(a));
            if (Outcome(a) == "added" && a.TryGetProperty("id", out var addedId)) added.Add(addedId.GetGuid());
            if (a.TryGetProperty("versions", out var kept)) versionsKept += kept.GetInt32();
            if (a.TryGetProperty("versionsLeftOut", out var left)) versionsLeftOut += left.GetInt32();
            if (a.TryGetProperty("heldHere", out var h) && h.ValueKind == JsonValueKind.True) heldHere++;
            // A commitment (058) comes in with the memory this import has just added, and only then.
            if (Outcome(a) == "added" && m.TryGetProperty("commitment", out var commitment) && commitment.ValueKind == JsonValueKind.Object)
            {
                await using var cmd = Database.Command(c, tx, "SELECT import_memory_commitment(@origin,@record)::text");
                cmd.Parameters.Add(new NpgsqlParameter("origin", NpgsqlDbType.Jsonb) { Value = originJson });
                cmd.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Jsonb) { Value = m.GetRawText() });
                using var answer = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
                Count("commitment", Outcome(answer.RootElement));
                // Its counterparty read like instructions to an AI client (058's screening): in, with no name.
                if (answer.RootElement.TryGetProperty("nameLeftOut", out var nameless) && nameless.ValueKind == JsonValueKind.True) Count("commitment", "name_left_out");
            }
            // A decision (060) comes in with the memory this import has just added, and only then.
            if (Outcome(a) == "added" && m.TryGetProperty("decision", out var decision) && decision.ValueKind == JsonValueKind.Object)
            {
                await using var cmd = Database.Command(c, tx, "SELECT import_memory_decision(@origin,@record)::text");
                cmd.Parameters.Add(new NpgsqlParameter("origin", NpgsqlDbType.Jsonb) { Value = originJson });
                cmd.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Jsonb) { Value = m.GetRawText() });
                using var answer = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
                Count("decision", Outcome(answer.RootElement));
            }
            // A due date (061) comes in with the memory this import has just added, and only then.
            if (Outcome(a) == "added" && m.TryGetProperty("due", out var due) && due.ValueKind == JsonValueKind.Object)
            {
                await using var cmd = Database.Command(c, tx, "SELECT import_memory_due(@origin,@record)::text");
                cmd.Parameters.Add(new NpgsqlParameter("origin", NpgsqlDbType.Jsonb) { Value = originJson });
                cmd.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Jsonb) { Value = m.GetRawText() });
                using var answer = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
                Count("due", Outcome(answer.RootElement));
            }
            // A standing instruction (065) likewise.
            if (Outcome(a) == "added" && m.TryGetProperty("standing", out var standing) && standing.ValueKind == JsonValueKind.True)
            {
                await using var cmd = Database.Command(c, tx, "SELECT import_memory_standing(@origin,@record)::text");
                cmd.Parameters.Add(new NpgsqlParameter("origin", NpgsqlDbType.Jsonb) { Value = originJson });
                cmd.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Jsonb) { Value = m.GetRawText() });
                using var answer = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
                Count("standing", Outcome(answer.RootElement));
            }
            // Work properties (079) likewise, after those, since a decision or a standing instruction is no work item.
            if (Outcome(a) == "added" && m.TryGetProperty("work", out var work) && work.ValueKind == JsonValueKind.Object)
            {
                await using var cmd = Database.Command(c, tx, "SELECT import_memory_work(@origin,@record)::text");
                cmd.Parameters.Add(new NpgsqlParameter("origin", NpgsqlDbType.Jsonb) { Value = originJson });
                cmd.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Jsonb) { Value = m.GetRawText() });
                using var answer = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
                Count("work", Outcome(answer.RootElement));
            }
        }
        foreach (var l in links.EnumerateArray())
            Count("link", Storable(l) ? Outcome(await Call("SELECT import_memory_link(@person,@own,@family,@origin,@record)::text", l.GetRawText()))
                : "refused:invalid");
        if (dryRun) await tx.RollbackAsync(ct);
        else await tx.CommitAsync(ct);
        return new(origin, same, dryRun, scopes, outcomes.ToDictionary(k => k.Key, k => (IReadOnlyDictionary<string, int>)k.Value, StringComparer.Ordinal),
            versionsKept, versionsLeftOut, heldHere, aliasesLeftOut, dryRun ? [] : added);
    }
}
