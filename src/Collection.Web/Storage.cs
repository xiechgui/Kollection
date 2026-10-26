using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Collection.Web;

public sealed partial class LibraryStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string connectionString;
    public LibraryStore(string folder)
    {
        Directory.CreateDirectory(folder);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "collection.db") }.ToString();
        using var db = Open();
        var legacy = Convert.ToInt32(Scalar(db, "SELECT count(*) FROM sqlite_master WHERE name='library'")) != 0;
        // SQLite's backup API includes committed WAL content; never overwrite an earlier backup.
        if (legacy)
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, $"collection-v1-{Guid.NewGuid():N}.db") }.ToString());
            backup.Open(); db.BackupDatabase(backup);
        }
        using var tx = Begin(db);
        Exec(db, """
            CREATE TABLE IF NOT EXISTS meta(id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS items(id TEXT PRIMARY KEY, name TEXT NOT NULL, name_search TEXT NOT NULL, draft INTEGER NOT NULL, updated TEXT NOT NULL, version INTEGER NOT NULL, path_key TEXT, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS items_order ON items(draft,updated DESC,id);
            CREATE INDEX IF NOT EXISTS items_path ON items(path_key);
            CREATE TABLE IF NOT EXISTS tags(id TEXT PRIMARY KEY,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS item_tags(item_id TEXT NOT NULL,tag_id TEXT NOT NULL,PRIMARY KEY(item_id,tag_id));
            CREATE INDEX IF NOT EXISTS tags_lookup ON item_tags(tag_id,item_id);
            CREATE TABLE IF NOT EXISTS properties(item_id TEXT NOT NULL,tag_id TEXT NOT NULL,value_search TEXT NOT NULL,PRIMARY KEY(item_id,tag_id));
            CREATE TABLE IF NOT EXISTS refs(item_id TEXT NOT NULL,tag_id TEXT NOT NULL,position INTEGER NOT NULL,target_id TEXT NOT NULL,PRIMARY KEY(item_id,tag_id,position));
            CREATE INDEX IF NOT EXISTS refs_inbound ON refs(target_id,item_id);
            """);
        if (Convert.ToInt32(Scalar(db,"SELECT count(*) FROM meta")) == 0)
        {
            var initial = legacy ? JsonSerializer.Deserialize<LibraryState>((string)Scalar(db,"SELECT json FROM library WHERE id=1")!, Json)! : Seed();
            Validate(initial);
            Exec(db,"INSERT INTO meta VALUES(1,$v)",("$v",initial.Revision));
            foreach (var tag in initial.Tags) WriteTag(db,tag);
            foreach (var item in initial.Items) { item.Version = Math.Max(1,item.Version); WriteItem(db,item); }
        }
        if (legacy) Exec(db,"DROP TABLE library");
        Exec(db,"PRAGMA user_version=2");
        tx.Commit();
    }
    [ThreadStatic] private static SqliteTransaction? activeTransaction;
    private static SqliteTransaction Begin(SqliteConnection db) => activeTransaction=db.BeginTransaction();
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private static SqliteCommand Command(SqliteConnection db,string sql,params (string,object?)[] args)
    {
        var cmd=db.CreateCommand(); cmd.CommandText=sql;
        if(activeTransaction?.Connection==db)cmd.Transaction=activeTransaction;
        foreach(var (key,value) in args) cmd.Parameters.AddWithValue(key,value??DBNull.Value);
        return cmd;
    }
    private static object? Scalar(SqliteConnection db,string sql,params (string,object?)[] args) { using var cmd=Command(db,sql,args); return cmd.ExecuteScalar(); }
    private static void Exec(SqliteConnection db,string sql,params (string,object?)[] args) { using var cmd=Command(db,sql,args);cmd.ExecuteNonQuery(); }
    private static List<T> Rows<T>(SqliteConnection db,string sql,params (string,object?)[] args)
    {
        using var cmd=Command(db,sql,args);using var reader=cmd.ExecuteReader();var result=new List<T>();
        while(reader.Read())result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0),Json)!);return result;
    }
    private static long Revision(SqliteConnection db)=>Convert.ToInt64(Scalar(db,"SELECT revision FROM meta WHERE id=1"));
    private static void CheckRevision(SqliteConnection db,long revision) { if(Revision(db)!=revision)throw new ConflictException("数据已被其他操作更新，请刷新后重试。"); }
    private static List<Tag> Tags(SqliteConnection db)=>Rows<Tag>(db,"SELECT json FROM tags ORDER BY rowid");
    private static Item? Find(SqliteConnection db,string id)=>Rows<Item>(db,"SELECT json FROM items WHERE id=$id",("$id",id)).FirstOrDefault();
    private static LibraryState Load(SqliteConnection db)=>new(){Revision=Revision(db),Tags=Tags(db),Items=Rows<Item>(db,"SELECT json FROM items ORDER BY rowid")};
    public LibraryState Read() { lock(gate){using var db=Open();return Load(db);} }
    public bool IsFile(Item item){lock(gate){using var db=Open();return EffectiveTags(new LibraryState{Tags=Tags(db)},item).Contains("file");}}
    public Item? Get(string id) { lock(gate){using var db=Open();return Find(db,id);} }
    public object Catalog()
    {
        lock(gate)
        {
            using var db=Open();var tags=Tags(db);var counts=tags.ToDictionary(t=>t.Id,_=>0);
            using var cmd=Command(db,"SELECT item_id,tag_id FROM item_tags JOIN items ON items.id=item_id WHERE draft=0");using var reader=cmd.ExecuteReader();
            var groups=new Dictionary<string,List<string>>();
            while(reader.Read()){var id=reader.GetString(0);if(!groups.ContainsKey(id))groups[id]=[];groups[id].Add(reader.GetString(1));}
            reader.Close();var state=new LibraryState{Tags=tags};
            foreach(var ids in groups.Values)foreach(var id in EffectiveTags(state,new Item{Tags=ids}))if(counts.ContainsKey(id))counts[id]++;
            return new {revision=Revision(db),tags,counts,count=Convert.ToInt32(Scalar(db,"SELECT count(*) FROM items WHERE draft=0")),draftCount=Convert.ToInt32(Scalar(db,"SELECT count(*) FROM items WHERE draft=1"))};
        }
    }
    public ItemPage Query(bool? draft,string? query,string? tag,int offset,int limit,string? inbound=null)
    {
        offset=Math.Max(0,offset);limit=Math.Clamp(limit,1,100);
        lock(gate)
        {
            using var db=Open();var tags=Tags(db);var state=new LibraryState{Tags=tags};
            var filterIds=string.IsNullOrEmpty(tag)?new List<string>():tags.Where(t=>EffectiveTags(state,new Item{Tags=[t.Id]}).Contains(tag)).Select(t=>t.Id).ToList();
            var matchingTags=string.IsNullOrEmpty(query)?new List<string>():tags.Where(t=>EffectiveTags(state,new Item{Tags=[t.Id]}).Any(id=>tags.Any(x=>x.Id==id&&x.Name.Contains(query,StringComparison.OrdinalIgnoreCase)))).Select(t=>t.Id).ToList();
            var where="($draft IS NULL OR draft=$draft)";
            if(!string.IsNullOrEmpty(tag))where+=" AND EXISTS(SELECT 1 FROM item_tags it WHERE it.item_id=i.id AND it.tag_id IN(SELECT value FROM json_each($tags)))";
            if(!string.IsNullOrEmpty(query))where+="""
                 AND (instr(i.name_search,$q)>0
                 OR EXISTS(SELECT 1 FROM properties p WHERE p.item_id=i.id AND instr(p.value_search,$q)>0)
                 OR EXISTS(SELECT 1 FROM refs r JOIN items target ON target.id=r.target_id WHERE r.item_id=i.id AND instr(target.name_search,$q)>0)
                 OR EXISTS(SELECT 1 FROM item_tags it WHERE it.item_id=i.id AND it.tag_id IN(SELECT value FROM json_each($matching))))
                """;
            if(inbound!=null)where+=" AND EXISTS(SELECT 1 FROM refs r WHERE r.item_id=i.id AND r.target_id=$inbound)";
            (string,object?)[] args=[("$draft",draft.HasValue?(object)(draft.Value?1:0):null),("$tags",JsonSerializer.Serialize(filterIds)),("$matching",JsonSerializer.Serialize(matchingTags)),("$q",query?.ToLowerInvariant()),("$inbound",inbound),("$offset",offset),("$limit",limit)];
            var total=Convert.ToInt32(Scalar(db,"SELECT count(*) FROM items i WHERE "+where,args));
            return new(Rows<Item>(db,"SELECT i.json FROM items i WHERE "+where+" ORDER BY updated DESC,id LIMIT $limit OFFSET $offset",args),total,offset,limit);
        }
    }
    public List<Item> Related(string id)
    {
        lock(gate){using var db=Open();return Rows<Item>(db,"SELECT DISTINCT i.json FROM items i JOIN refs r ON r.target_id=i.id WHERE r.item_id=$id",("$id",id));}
    }
    private static string? PathKey(Item item)
    {
        var path=item.Values.GetValueOrDefault("path")?.Text;
        if(string.IsNullOrWhiteSpace(path))return null;
        try { var key=Path.GetFullPath(path); return OperatingSystem.IsWindows()?key.ToUpperInvariant():key; }catch(ArgumentException){return null;}
    }
    private static void WriteTag(SqliteConnection db,Tag tag)=>Exec(db,"INSERT INTO tags(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json",("$id",tag.Id),("$json",JsonSerializer.Serialize(tag,Json)));
    private static void WriteItem(SqliteConnection db,Item item)
    {
        Exec(db,"""
            INSERT INTO items(id,name,name_search,draft,updated,version,path_key,json) VALUES($id,$name,$search,$draft,$updated,$version,$path,$json)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,name_search=excluded.name_search,draft=excluded.draft,updated=excluded.updated,version=excluded.version,path_key=excluded.path_key,json=excluded.json
            """,("$id",item.Id),("$name",item.Name),("$search",item.Name.ToLowerInvariant()),("$draft",item.Draft?1:0),("$updated",item.UpdatedAt.ToUniversalTime().ToString("O")),("$version",item.Version),("$path",PathKey(item)),("$json",JsonSerializer.Serialize(item,Json)));
        foreach(var table in new[]{"item_tags","properties","refs"})Exec(db,$"DELETE FROM {table} WHERE item_id=$id",("$id",item.Id));
        foreach(var tag in item.Tags.Distinct())Exec(db,"INSERT INTO item_tags VALUES($id,$tag)",("$id",item.Id),("$tag",tag));
        foreach(var (tag,value) in item.Values)
        {
            Exec(db,"INSERT INTO properties VALUES($id,$tag,$value)",("$id",item.Id),("$tag",tag),("$value",(value.Text??value.Number?.ToString(CultureInfo.InvariantCulture)??"").ToLowerInvariant()));
            for(int n=0;n<value.Refs.Count;n++)Exec(db,"INSERT INTO refs VALUES($id,$tag,$n,$target)",("$id",item.Id),("$tag",tag),("$n",n),("$target",value.Refs[n]));
        }
    }
    private static void ValidateItems(SqliteConnection db,List<Item> changes)
    {
        var state=new LibraryState{Tags=Tags(db),Items=[..changes]};var ids=changes.Select(i=>i.Id).ToHashSet();
        foreach(var target in changes.SelectMany(i=>i.Values.Values).SelectMany(v=>v.Refs).Distinct())
            if(!ids.Contains(target)&&Find(db,target) is {} found)state.Items.Add(new Item{Id=found.Id,Name=found.Name,Draft=found.Draft});
        Validate(state);
    }
    public Item Save(ItemRequest request,string? id=null,bool confirm=false)
    {
        lock(gate)
        {
            using var db=Open();using var tx=Begin(db);var item=request.Item;
            if(id==null)
            {
                CheckRevision(db,request.Revision);
                if(Find(db,item.Id)!=null)throw new ArgumentException("ID 已存在。");
                if(Convert.ToInt32(Scalar(db,"SELECT count(*) FROM items"))>=5000)throw new ArgumentException("当前上限为 5000 个对象（含暂存）。");
                item.Draft=false;item.Source=null;item.Version=1;
            }
            else
            {
                var old=Find(db,id)??throw new ArgumentException("对象不存在。");
                if(item.Version>0){if(item.Version!=old.Version)throw new ConflictException("此对象已被修改，请重新打开后重试。");}else CheckRevision(db,request.Revision);
                if(confirm&&!old.Draft)throw new ArgumentException("暂存对象不存在或已确认。");
                item.Id=id;item.Draft=confirm?false:old.Draft;item.Source=old.Source;item.Version=old.Version+1;
            }
            // New requests keep the original revision guard for legacy clients.
            if(id==null)CheckRevision(db,request.Revision);
            item.UpdatedAt=DateTimeOffset.UtcNow;ValidateItems(db,[item]);WriteItem(db,item);Exec(db,"UPDATE meta SET revision=revision+1");tx.Commit();return item;
        }
    }
    public void Delete(string id,long revision,long? version)
    {
        lock(gate){using var db=Open();using var tx=Begin(db);var old=Find(db,id)??throw new ArgumentException("对象不存在。");
            if(version.HasValue){if(old.Version!=version)throw new ConflictException("此对象已被修改，请刷新后重试。");}else CheckRevision(db,revision);
            DeleteRows(db,[id]);Exec(db,"UPDATE meta SET revision=revision+1");tx.Commit();}
    }
    private static void DeleteRows(SqliteConnection db,List<string> ids)
    {
        var json=JsonSerializer.Serialize(ids);
        if(Convert.ToInt32(Scalar(db,"SELECT count(*) FROM refs WHERE target_id IN(SELECT value FROM json_each($ids)) AND item_id NOT IN(SELECT value FROM json_each($ids))",("$ids",json)))>0)throw new ConflictException("对象仍被引用，请先解除引用。");
        foreach(var table in new[]{"item_tags","properties","refs"})Exec(db,$"DELETE FROM {table} WHERE item_id IN(SELECT value FROM json_each($ids))",("$ids",json));
        Exec(db,"DELETE FROM items WHERE id IN(SELECT value FROM json_each($ids))",("$ids",json));
    }
    public void Batch(BatchRequest request)
    {
        if(request.Versions.Count is <1 or >500)throw new ArgumentException("每次请选择 1–500 个候选。");
        lock(gate){using var db=Open();using var tx=Begin(db);var changes=new List<Item>();
            foreach(var (id,version) in request.Versions){var item=Find(db,id);if(item==null||!item.Draft||item.Version!=version)throw new ConflictException("候选已变化，请刷新后重新选择。");changes.Add(item);}
            if(request.Action=="discard")DeleteRows(db,changes.Select(i=>i.Id).ToList());
            else
            {
                if(request.Action is not ("confirm" or "tags"))throw new ArgumentException("未知批量操作。");
                foreach(var item in changes){if(request.Action=="confirm")item.Draft=false;item.Tags=item.Tags.Except(request.RemoveTags??[]).Union(request.AddTags??[]).ToList();item.Version++;item.UpdatedAt=DateTimeOffset.UtcNow;}
                ValidateItems(db,changes);foreach(var item in changes)WriteItem(db,item);
            }
            Exec(db,"UPDATE meta SET revision=revision+1");tx.Commit();
        }
    }
    // Tag schema edits are rare and must validate every affected object.
    public LibraryState Change(long revision,Action<LibraryState> change)
    {
        lock(gate){using var db=Open();using var tx=Begin(db);CheckRevision(db,revision);var state=Load(db);change(state);Validate(state);
            foreach(var tag in state.Tags)WriteTag(db,tag);
            Exec(db,"UPDATE meta SET revision=revision+1");state.Revision++;tx.Commit();return state;}
    }
    public (int Added,int Skipped) AddCandidates(List<Item> candidates,long? revision=null)
    {
        lock(gate){using var db=Open();using var tx=Begin(db);if(revision.HasValue)CheckRevision(db,revision.Value);int added=0,skipped=0;
            int count=Convert.ToInt32(Scalar(db,"SELECT count(*) FROM items"));
            foreach(var item in candidates)
            {
                var key=PathKey(item);
                if(key!=null&&Convert.ToInt32(Scalar(db,"SELECT count(*) FROM items WHERE path_key=$key",("$key",key)))>0){skipped++;continue;}
                if(count+added>=5000)throw new ArgumentException("收藏库已达到 5000 项上限，请先整理暂存区。");
                item.Version=1;item.Draft=true;ValidateItems(db,[item]);WriteItem(db,item);added++;
            }
            if(added>0)Exec(db,"UPDATE meta SET revision=revision+1");tx.Commit();return(added,skipped);}
    }
}
