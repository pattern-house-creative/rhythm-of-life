using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge;
using Knowledge.Mcp;
using Knowledge.Adapters;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge.Tests;

public sealed class StudioRuleTests
{
    [Fact] public void Controls_are_closed_typed_selections()
    {
        JsonElement J(string s)=>JsonDocument.Parse(s).RootElement.Clone();
        var tab=new DashboardControl("tab","View","tab",["Plan","History"],J("\"Plan\""));
        Assert.Null(DashboardControl.Problem([tab]));
        Assert.False(tab.Accepts(J("\"document.body.remove()\"")));
        Assert.NotNull(DashboardControl.Problem([tab with {Kind="javascript"}]));
        Assert.False(new DashboardControl("week","Week","week").Accepts(J("521")));
        var dates=new DashboardControl("dates","Dates","date_range");
        Assert.True(dates.Accepts(J("""{"from":"2026-01-01","to":"2026-01-08"}""")));
        Assert.False(dates.Accepts(J("""{"from":"2026-02-30","to":"2026-03-08"}""")));
        Assert.False(dates.Accepts(J("""{"from":"2026-01-08","to":"2026-01-01"}""")));
        Assert.False(AppStudio.Allowed(J("""{"op":"update","screen":"screen_1234567890","payload":{}}""")));
        Assert.False(AppStudio.Allowed(J("""{"op":"read","screen":"screen_1234567890","sessionId":"bad"}""")));
        var invoke=HarnessTools.Create(TestSettings.Use(),[]).Single(t=>t.ProtocolTool.Name=="invoke_dashboard_action").ProtocolTool;
        var args=J("""{"screen":"screen_1234567890","dashboardId":"aaaaaaaa-0000-4000-8000-000000000001","version":1,"selectionRevision":0,"control":"week","value":1}""")
            .EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone());
        Assert.Null(ToolArguments.Problem(invoke,args));
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class StudioServiceTests(ScopeCorpusFixture fixture):IClassFixture<ScopeCorpusFixture>
{
    [PostgresFact] public async Task Real_store_prepares_scoped_feeds_validates_and_publishes_controls()
    {
        var source=fixture.Source;var host=fixture.Load.Host;var owner=fixture.Readers["family"];var app=fixture.Readers["app"];
        var dashboards=new DashboardStore(source,host);var feeds=new DashboardFeeds(fixture.Load.Db,host,fixture.Load.Registry);
        var store=new StudioStore(source,dashboards,feeds,host);var screen="studio-service-"+Guid.NewGuid().ToString("N");var id=Guid.NewGuid();
        var star=fixture.Load.Corpus.Memories.Where(m=>m.Scope=="family")
            .Select(m=>fixture.Load.ByGraphId.Any(p=>p.Value.Contains(m.Id))?fixture.Load.GraphId(m.Id):null).OfType<string>().First();
        await new ScreenStore(source,new ScreenWaits(),host).Listen(app,screen,"Synthetic Studio",TimeSpan.Zero,null,null,default);
        await store.Call(owner,"begin",id,screen,Guid.NewGuid(),null,new(),default);
        var draft=new DashboardDraft("Synthetic service flow","DEMO","<h1>DEMO</h1><script>rhythm.onData(()=>{});</script>",[new DashboardFeed("about","contents",star)],"First received snapshot");
        var payload=new JsonObject{["draft"]=JsonSerializer.SerializeToNode(draft,DashboardDraft.Json),["controls"]=JsonSerializer.SerializeToNode(new[]{new DashboardControl("week","Week","week",Initial:JsonSerializer.SerializeToElement(0))},DashboardDraft.Json)};
        var request=Guid.NewGuid();var update=await store.Call(owner,"update",id,screen,request,0,payload.DeepClone().AsObject(),default);
        Assert.Equal(1,update["revision"]!.GetValue<int>());
        Assert.True((await store.Call(owner,"update",id,screen,request,0,payload.DeepClone().AsObject(),default))["replayed"]!.GetValue<bool>());
        var data=await store.Data(app,id,screen,1,default);Assert.NotNull(data["feeds"]!["about"]);
        await Assert.ThrowsAsync<PostgresException>(()=>store.Call(app,"update",id,screen,Guid.NewGuid(),1,payload.DeepClone().AsObject(),default));
        await store.Call(app,"validate",id,screen,Guid.NewGuid(),1,new(){["ok"]=true},default);
        var saved=await store.Call(app,"save",id,screen,Guid.NewGuid(),1,new(),default);var dashboard=saved["id"]!.GetValue<Guid>();
        Assert.Equal(1,saved["version"]!.GetValue<int>());
        Assert.Equal("week",(string?)(await store.Controls(owner,dashboard,1,default))[0]!["id"]);
        Assert.Equal(saved.ToJsonString(),(await store.Call(owner,"save",id,screen,Guid.NewGuid(),1,new(),default)).ToJsonString());
        Assert.False(JsonNode.Parse((await dashboards.Get(owner,dashboard,1,false,default))!)!["editsAllowed"]!.GetValue<bool>());
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class StudioDatabaseTests(DatabaseFixture fixture):IClassFixture<DatabaseFixture>
{
    async Task<Guid> Harness()
    {
        var folder=Path.Combine(Path.GetTempPath(),"rhythm-studio-test-"+Guid.NewGuid().ToString("N"));
        try{return await new Principals(fixture.Source).Create("studio-"+Guid.NewGuid().ToString("N")[..8],"harness",null,Path.Combine(folder,"token"),default);}
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    async Task<(Guid Owner,Guid App,string Screen,Guid Session)> Begin(Guid? dashboard=null)
    {
        var owner=await Harness();var app=(await new Principals(fixture.Source).SyncApp(Principals.NewToken(),default))!.Value;
        var screen="studio-"+Guid.NewGuid().ToString("N");
        await new ScreenStore(fixture.Source,new ScreenWaits()).Listen(app,screen,"Test Studio",TimeSpan.Zero,null,null,default);
        var id=Guid.NewGuid();await Change(owner,id,screen,"begin",0,new JsonObject{["dashboard"]=dashboard});return(owner,app,screen,id);
    }
    async Task<JsonObject> Change(Guid principal,Guid id,string screen,string op,int revision,JsonObject? payload=null,Guid? request=null,string[]? stars=null)
    {
        await using var cmd=fixture.Source.CreateCommand("SELECT studio_change(@p,@id,@screen,@op,@request,@revision,@payload::jsonb,@stars)::text");
        cmd.Parameters.AddWithValue("p",principal);cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("screen",screen);cmd.Parameters.AddWithValue("op",op);
        cmd.Parameters.AddWithValue("request",request??Guid.NewGuid());cmd.Parameters.AddWithValue("revision",revision);cmd.Parameters.AddWithValue("payload",(payload??new JsonObject()).ToJsonString());
        cmd.Parameters.AddWithValue("stars",NpgsqlDbType.Array|NpgsqlDbType.Text,(object?)stars??DBNull.Value);
        return JsonNode.Parse((string)(await cmd.ExecuteScalarAsync())!)!.AsObject();
    }
    static JsonObject Draft(string html="<h1>Training</h1>")=>new(){["draft"]=new JsonObject{["title"]="Training",["description"]="Synthetic",["html"]=html,["note"]="Test snapshot",
        ["feeds"]=new JsonArray(new JsonObject{["name"]="notes",["kind"]="log",["log"]="studio",["subject"]="household",["days"]=30}),["writes"]=new JsonArray(),["stars"]=new JsonArray(new JsonObject{["graphId"]="demo:training",["title"]="Synthetic training"})},["controls"]=new JsonArray()};

    [PostgresFact] public async Task Ownership_revisions_retries_validation_feedback_and_cancel_are_durable()
    {
        var (owner,app,screen,id)=await Begin();var request=Guid.NewGuid();
        var updated=await Change(owner,id,screen,"update",0,Draft(),request);Assert.Equal(1,(int)updated["revision"]!);
        Assert.True((bool)(await Change(owner,id,screen,"update",0,Draft(),request))["replayed"]!);
        await Assert.ThrowsAsync<PostgresException>(()=>Change(owner,id,screen,"update",1,Draft("changed"),request));
        await Assert.ThrowsAsync<PostgresException>(()=>Change(owner,id,screen,"update",0,Draft()));
        var other=await Harness();Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>Change(other,id,screen,"read",0))).SqlState);
        await Assert.ThrowsAsync<PostgresException>(()=>Change(app,id,"another-screen-123456","read",0));
        await Assert.ThrowsAsync<PostgresException>(()=>Change(owner,id,screen,"validate",1,new(){["ok"]=true}));
        var valid=await Change(app,id,screen,"validate",1,new(){["ok"]=true});Assert.Equal(1,(int)valid["valid_revision"]!);
        await Change(owner,id,screen,"update",1,Draft("broken"));
        var invalid=await Change(app,id,screen,"validate",2,new(){["ok"]=false,["message"]="Broken candidate"});
        Assert.Equal("<h1>Training</h1>",(string?)invalid["preview"]!["html"]);
        var steered=await Change(app,id,screen,"feedback",2,new(){["text"]="Show recovery first"});Assert.Equal(1,(int)steered["feedback"]![0]!["seq"]!);
        var restored=await Change(owner,id,screen,"read",2);Assert.Equal("broken",(string?)restored["candidate"]!["html"]);
        await Change(app,id,screen,"cancel",2);
        await Assert.ThrowsAsync<PostgresException>(()=>Change(owner,id,screen,"update",2,Draft("late")));
        await Assert.ThrowsAsync<PostgresException>(()=>Publish(owner,id,screen,2,Draft()["draft"]!.AsObject()));
    }
    async Task<JsonObject> Publish(Guid p,Guid id,string screen,int revision,JsonObject draft)
    {
        await using var cmd=fixture.Source.CreateCommand("SELECT studio_publish(@p,@id,@screen,@request,@revision,@version::jsonb,NULL)::text");
        cmd.Parameters.AddWithValue("p",p);cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("screen",screen);cmd.Parameters.AddWithValue("request",Guid.NewGuid());
        cmd.Parameters.AddWithValue("revision",revision);cmd.Parameters.AddWithValue("version",draft.ToJsonString());return JsonNode.Parse((string)(await cmd.ExecuteScalarAsync())!)!.AsObject();
    }
    [PostgresFact] public async Task Publish_is_atomic_idempotent_and_refuses_concurrent_saved_edits()
    {
        var(owner,app,screen,id)=await Begin();await Change(owner,id,screen,"update",0,Draft());
        await Assert.ThrowsAsync<PostgresException>(()=>Publish(owner,id,screen,1,Draft()["draft"]!.AsObject()));
        await Change(app,id,screen,"validate",1,new(){["ok"]=true});
        var saves=await Task.WhenAll(Publish(owner,id,screen,1,Draft()["draft"]!.AsObject()),Publish(app,id,screen,1,Draft()["draft"]!.AsObject()));
        Assert.Equal(saves[0].ToJsonString(),saves[1].ToJsonString());
        var dashboard=saves[0]["id"]!.GetValue<Guid>();
        await using(var count=fixture.Source.CreateCommand("SELECT count(*) FROM dashboard_versions WHERE dashboard_id=@id")){count.Parameters.AddWithValue("id",dashboard);Assert.Equal(1L,await count.ExecuteScalarAsync());}
        var next=await Begin(dashboard);await Change(next.Owner,next.Session,next.Screen,"update",0,Draft("Next"));
        await Change(next.App,next.Session,next.Screen,"validate",1,new(){["ok"]=true});
        var store=new DashboardStore(fixture.Source);await store.Save(next.Owner,dashboard,null,new DashboardDraft("Other","Synthetic","Other",[new DashboardFeed("notes","log",null!,Log:"studio",Subject:"household")],"Concurrent edit"),[new DashboardStar("demo:training","Synthetic training")],"Concurrent edit",default);
        var conflict=await Assert.ThrowsAsync<PostgresException>(()=>Publish(next.Owner,next.Session,next.Screen,1,Draft("Next")["draft"]!.AsObject()));Assert.Contains("changed",conflict.MessageText);
        await Change(next.App,next.Session,next.Screen,"discard",1);Assert.Equal(2,JsonNode.Parse((await store.Get(next.Owner,dashboard,null,false,default))!)!["version"]!.GetValue<int>());
    }
    [PostgresFact] public async Task Source_revocation_hides_code_and_expiry_fences_updates()
    {
        var(owner,app,screen,id)=await Begin();await Change(owner,id,screen,"update",0,Draft());
        try{
            await Scoped.Mode(fixture.Source,"on");
            Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>Change(app,id,screen,"read",1))).SqlState);
            Assert.NotNull((await Change(app,id,screen,"read",1,stars:["demo:training"]))["candidate"]);
        }finally{await Scoped.Mode(fixture.Source,"off");}
        await using(var expire=fixture.Source.CreateCommand("UPDATE studio_sessions SET expires_at=now()-interval '1 second' WHERE id=@id")){expire.Parameters.AddWithValue("id",id);await expire.ExecuteNonQueryAsync();}
        Assert.Equal("canceled",(string?)(await Change(owner,id,screen,"read",1))["status"]);
        await Assert.ThrowsAsync<PostgresException>(()=>Change(owner,id,screen,"update",1,Draft("late")));
    }
}
