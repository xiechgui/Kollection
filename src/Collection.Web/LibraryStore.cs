using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Collection.Web;

// v0.1 stores one validated document in SQLite. A transaction and revision guard
// prevent partial writes and stale browser tabs from overwriting newer changes.
public sealed class LibraryStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string connectionString;
    public LibraryStore(string folder)
    {
        Directory.CreateDirectory(folder);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "collection.db") }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS library(id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT OR IGNORE INTO library(id,json) VALUES(1,$json)";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(Seed(), Json));
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public LibraryState Read()
    {
        lock (gate) { using var db = Open(); return Load(db); }
    }
    private static LibraryState Load(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT json FROM library WHERE id=1";
        return JsonSerializer.Deserialize<LibraryState>((string)cmd.ExecuteScalar()!, Json)!;
    }
    public LibraryState Change(long revision, Action<LibraryState> change)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            var state = Load(db, tx);
            if (state.Revision != revision) throw new ConflictException("数据已被其他操作更新，请刷新后重试。");
            change(state); Validate(state); state.Revision++;
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "UPDATE library SET json=$json WHERE id=1";
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, Json));
            cmd.ExecuteNonQuery(); tx.Commit(); return state;
        }
    }
    public static HashSet<string> EffectiveTags(LibraryState state, Item item)
    {
        var result = new HashSet<string>();
        foreach (var id in item.Tags)
        {
            string? current = id;
            while (current != null && result.Add(current)) current = state.Tags.FirstOrDefault(t => t.Id == current)?.ParentId;
        }
        return result;
    }
    public static void Validate(LibraryState s)
    {
        if (s.Items.Count > 5000) throw new ArgumentException("初版上限为 5000 个对象（含暂存），请缩小导入范围。");
        if(s.Tags.Count > 500) throw new ArgumentException("初版最多 500 个标签。");
        if(s.Tags.Select(t=>t.Id).Distinct().Count()!=s.Tags.Count || s.Items.Select(i=>i.Id).Distinct().Count()!=s.Items.Count) throw new ArgumentException("ID 重复。");
        if(s.Tags.Select(t=>t.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=s.Tags.Count) throw new ArgumentException("标签名称重复。");
        var tags=s.Tags.ToDictionary(t=>t.Id); var items=s.Items.ToDictionary(i=>i.Id);
        foreach(var t in s.Tags)
        {
            if(string.IsNullOrWhiteSpace(t.Id)||string.IsNullOrWhiteSpace(t.Name)||t.Name.Length>80) throw new ArgumentException("标签名称不能为空且不超过 80 字。");
            if(!new[]{"text","number","ref","refs"}.Contains(t.Kind)) throw new ArgumentException("未知属性类型。");
            if(t.Properties.Any(p=>!tags.ContainsKey(p))) throw new ArgumentException("属性标签不存在。");
            var seen=new HashSet<string>{t.Id}; var parent=t.ParentId;
            while(parent!=null)
            {
                if(!tags.TryGetValue(parent,out var p)) throw new ArgumentException("父标签不存在。");
                if(!seen.Add(parent)) throw new ArgumentException("标签不能循环继承。"); parent=p.ParentId;
            }
        }
        foreach(var i in s.Items)
        {
            if(string.IsNullOrWhiteSpace(i.Id)||string.IsNullOrWhiteSpace(i.Name)||i.Name.Length>300) throw new ArgumentException("名称不能为空且不超过 300 字。");
            if(i.Tags.Any(t=>!tags.ContainsKey(t))) throw new ArgumentException("分类标签不存在。");
            foreach(var (key,v) in i.Values)
            {
                if(!tags.TryGetValue(key,out var t)) throw new ArgumentException("属性名标签不存在。");
                if(t.Kind=="text" && (v.Number!=null||v.Refs.Count>0)) throw new ArgumentException("文本属性类型不匹配。");
                if(t.Kind=="number" && (v.Text!=null||v.Refs.Count>0)) throw new ArgumentException("数字属性类型不匹配。");
                if(t.Kind is "ref" or "refs")
                {
                    if(v.Text!=null||v.Number!=null) throw new ArgumentException("引用属性类型不匹配。");
                    if(t.Kind=="ref"&&v.Refs.Count>1) throw new ArgumentException("该属性只能引用一个对象。");
                    if(v.Refs.Distinct().Count()!=v.Refs.Count) throw new ArgumentException("引用数组不能重复。");
                    foreach(var id in v.Refs)
                        if(!items.TryGetValue(id,out var target)||(!i.Draft&&target.Draft)) throw new ArgumentException("引用不存在或指向尚未确认的对象。");
                }
            }
        }
    }
    private static LibraryState Seed()
    {
        var s=new LibraryState();
        void Tag(string id,string name,string kind="text",string? parent=null,params string[] props) => s.Tags.Add(new Tag{Id=id,Name=name,Kind=kind,ParentId=parent,Properties=[..props]});
        Tag("path","路径"); Tag("size","文件大小（字节）","number"); Tag("filename","文件名");
        Tag("title","作品名"); Tag("rating","评分","number"); Tag("notes","感想"); Tag("duration","时长（秒）","number");
        Tag("url","网址"); Tag("body","正文"); Tag("versions","视频版本","refs"); Tag("cover","主封面","ref");
        Tag("actor","演员","refs"); Tag("director","导演","ref");
        Tag("file","文件","text",null,"path","size","filename");
        Tag("video","视频","text",null,"duration");
        Tag("movie","电影","text","video","title","rating","notes","actor","director","versions","cover");
        Tag("series","电视剧","text","video","title","versions"); Tag("anime","动漫"); Tag("av","AV","text","video","title","actor","versions");
        Tag("image","图片"); Tag("web","网页","text",null,"url","notes"); Tag("text","文字","text",null,"body"); Tag("excerpt","摘抄","text","text","url"); Tag("later","待看");
        return s;
    }
}
public sealed class ConflictException(string message): Exception(message);
