using Microsoft.Data.Sqlite;
namespace Luma.Core;

public sealed record LibraryExclusion(string Kind,string SourceId,string Value)
{
    public bool Matches(string source,string path)
    {
        if(Kind=="type")return Path.GetExtension(path).Equals(Value,StringComparison.OrdinalIgnoreCase);
        if(SourceId!=source)return false;
        if(Kind=="source")return true;
        var normalized=path.Replace('\\','/').ToLowerInvariant();
        return normalized==Value || normalized.StartsWith(Value+"/",StringComparison.Ordinal);
    }
}
public sealed partial class MediaCache
{
    private const string ExclusionSchema="CREATE TABLE IF NOT EXISTS exclusions(kind TEXT NOT NULL,source_id TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(kind,source_id,value));";
    // Literal comparisons avoid wildcard surprises in folder names. Rules are source-identity scoped.
    private const string AllowedMediaSql="""
        NOT EXISTS(SELECT 1 FROM exclusions e WHERE
          (e.kind='type' AND lower(substr(m.relative_path,-length(e.value)))=e.value)
          OR (e.source_id=m.source_id AND (e.kind='source' OR
            (e.kind='folder' AND (replace(m.relative_path,char(92),'/') COLLATE LUMA_PATH=e.value OR
             substr(replace(m.relative_path,char(92),'/'),1,length(e.value)+1) COLLATE LUMA_PATH=e.value||'/')))))
        """;
    public async Task<IReadOnlyList<LibraryExclusion>> ExclusionsAsync(CancellationToken ct=default)
    {
        await using var db=CreateConnection();await db.OpenAsync(ct);
        await using var cmd=db.CreateCommand();cmd.CommandText="SELECT kind,source_id,value FROM exclusions ORDER BY kind,source_id,value";
        var result=new List<LibraryExclusion>();await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2)));
        return result;
    }
    public async Task SetExclusionAsync(LibraryExclusion rule,bool exclude,CancellationToken ct=default)
    {
        if(rule.Kind is not ("source" or "folder" or "type"))throw new ArgumentException("Unknown exclusion kind.");
        var value=rule.Value.Replace('\\','/').Trim('/').ToLowerInvariant();
        if(rule.Kind=="type")
        {
            value=rule.Value.ToLowerInvariant();
            if(value.Length<2 || value[0]!='.' || !value[1..].All(char.IsAsciiLetterOrDigit))throw new ArgumentException("Use a file extension such as .jpg.");
            rule=rule with { SourceId="" };
        }
        else if(string.IsNullOrWhiteSpace(rule.SourceId))throw new ArgumentException("Choose a registered source.");
        if(rule.Kind=="folder" && (value.Length==0 || value.Split('/').Any(p=>p is "." or "..") || Path.IsPathRooted(rule.Value)))throw new ArgumentException("Choose a folder within the source.");
        if(rule.Kind=="source")value="";
        using var lease=IndexingLease();
        // Changing discovery rules invalidates saved traversal, but never the published catalog.
        await ExecuteAsync((exclude ? "INSERT OR IGNORE INTO exclusions VALUES($kind,$source,$value);" : "DELETE FROM exclusions WHERE kind=$kind AND source_id=$source AND value=$value;")+
            "DELETE FROM scan_media WHERE $source='' OR source_id=$source; DELETE FROM scan_folders WHERE $source='' OR source_id=$source; DELETE FROM scan_jobs WHERE $source='' OR source_id=$source;",ct,
            ("$kind",rule.Kind),("$source",rule.SourceId),("$value",value));
    }
}
