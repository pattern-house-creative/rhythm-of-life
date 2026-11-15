using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using Npgsql;

namespace Knowledge;

// Lossless field values, not clinical interpretations. Original bytes remain authoritative.
public static class SourceProjection
{
    public const int Version = 1;
    public static (string Payload, string Fields) Project(byte[] bytes, string format)
    {
        string payload;
        if(format=="csv")
        {
            // A header row, then one object per row keyed by column (a repeated column name gets #column=<n>).
            using var parser=new TextFieldParser(new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('﻿')));
            parser.SetDelimiters(",");parser.HasFieldsEnclosedInQuotes=true;
            var records=new List<object>();string[]? header=null;int line=0;
            while(!parser.EndOfData)
            {
                var row=parser.ReadFields()!;line++;
                if(header is null){header=row;records.Add(new{line,header});continue;}
                if(row.Length==header.Length)
                    records.Add(new{line,fields=header.Select((key,i)=>(key:header.Count(h=>h==key)>1?key+"#column="+i:key,value:row[i])).Where(x=>x.value!="").ToDictionary(x=>x.key,x=>x.value)});
                else records.Add(new{line,cells=row});
            }
            payload=JsonSerializer.Serialize(records);
        }
        else if(format=="json") {using var json=JsonDocument.Parse(bytes);payload=json.RootElement.GetRawText();}
        else throw new ArgumentException("Unsupported projection format");
        using var doc=JsonDocument.Parse(payload);
        var fields=new SortedDictionary<string,int>(StringComparer.Ordinal);
        Walk(doc.RootElement,"");
        return(payload,JsonSerializer.Serialize(fields.Select(f=>new{path=f.Key,occurrences=f.Value})));
        void Walk(JsonElement e,string path)
        {
            if(fields.Count>4096)throw new InvalidDataException("Source field inventory limit");
            if(e.ValueKind==JsonValueKind.Object)foreach(var p in e.EnumerateObject())Walk(p.Value,path+"/"+Escape(p.Name));
            else if(e.ValueKind==JsonValueKind.Array)foreach(var v in e.EnumerateArray())Walk(v,path+"/*");
            else fields[path]=fields.GetValueOrDefault(path)+1;
        }
    }
    public static string Escape(string value)=>value.Replace("~","~0").Replace("/","~1");
    public static async Task Save(Guid id,string checksum,byte[] bytes,string format,NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken ct)
    {
        var projection=Project(bytes,format);
        await using var cmd=Database.Command(c,tx,"""
            INSERT INTO source_payloads(resource_id,checksum,format,adapter_version,payload,fields)
            VALUES(@id,@hash,@format,@version,@payload::jsonb,@fields::jsonb)
            ON CONFLICT(resource_id) DO UPDATE SET checksum=excluded.checksum,format=excluded.format,
              adapter_version=excluded.adapter_version,payload=excluded.payload,fields=excluded.fields,projected_at=now()
            """,("id",id),("hash",checksum),("format",format),("version",Version),("payload",projection.Payload),("fields",projection.Fields));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
