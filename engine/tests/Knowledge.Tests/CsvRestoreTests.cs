using Knowledge.Adapters;
using Knowledge.Adapters.Csv;

namespace Knowledge.Tests;

// The CSV folder is the truth for its files, also when a file comes back, in a database of its own. Reading a file
// replays the ingest result of its first read with the same contents and modified time (Windows copies and most backups
// keep that time), so a replay must never leave a file put back out of the map, or a file returned to an earlier
// version showing the later version's numbers.
public sealed class CsvRestoreTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_file_put_back_or_returned_to_an_earlier_version_reads_as_it_is()
    {
        var config = TestSettings.Use();
        var archive = new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive"));
        var import = NotesAdapterTests.Folder();
        var file = Path.Combine(import, "readings.csv");
        const string First = "time,metric,value,unit,subject,source\n2026-10-20T07:05:00Z,weight,71.6,kg,alex,Fictional scale\n2026-10-20,steps,8100,count,alex,\n";
        const string Second = "time,metric,value,unit,subject,source\n2026-10-20T07:05:00Z,weight,99.9,kg,alex,Fictional scale\n2026-10-20,steps,8100,count,alex,\n";
        var firstTime = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc); var secondTime = firstTime.AddHours(1);
        double[] firstValues = [8100, 71.6], secondValues = [8100, 99.9];  // steps, then weight
        void Write(string text, DateTime modified) { File.WriteAllText(file, text); File.SetLastWriteTimeUtc(file, modified); }
        var csv = new CsvAdapter();
        var db = new Database(fixture.Source, new AdapterRegistry([csv]).Policies(config));
        AdapterContext Context() => new(new AdapterSettings("csv", new Dictionary<string, object?> { ["enabled"] = "true", ["path"] = import }), config, db, archive);
        csv.Configure(Context());
        var ingest = new Ingest(db, archive, config);
        Task Sync(CsvAdapter adapter) => adapter.Sync(new MeasurementSink(ingest, adapter), default);
        async Task<double[]> Shown()
        {
            await using var cmd = fixture.Source.CreateCommand("""
                SELECT m.value FROM measurements m JOIN resources r ON r.id=m.resource_id WHERE r.provider='csv' AND r.status='current' ORDER BY m.metric
                """);
            var values = new List<double>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) values.Add(reader.GetDouble(0));
            return [.. values];
        }
        try
        {
            Write(First, firstTime);
            await Sync(csv);
            Assert.Equal(firstValues, await Shown());
            // Dragged out of the folder, then copied back as it was: its numbers come back.
            File.Delete(file);
            await Sync(csv);
            Assert.Empty(await Shown());
            Write(First, firstTime);
            await Sync(csv);
            Assert.Equal(firstValues, await Shown());
            // Changed, then restored from a backup that kept its time: the file's numbers, not the changed ones.
            Write(Second, secondTime);
            await Sync(csv);
            Assert.Equal(secondValues, await Shown());
            Write(First, firstTime);
            await Sync(csv);
            Assert.Equal(firstValues, await Shown());
            // An unchanged file read again after a restart still writes nothing.
            var restarted = new CsvAdapter();
            restarted.Configure(Context());
            await Sync(restarted);
            Assert.Equal(firstValues, await Shown());
            await using var cmd = fixture.Source.CreateCommand("SELECT (metadata->>'inserted')::int,(metadata->>'updated')::int,(metadata->>'unchanged')::int FROM sync_jobs WHERE provider='csv'");
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((0, 0, 2), (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        }
        finally { Directory.Delete(import, true); }
    }
}
