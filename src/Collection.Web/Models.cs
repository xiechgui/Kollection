namespace Collection.Web;
public sealed class LibraryState
{
    public long Revision { get; set; }
    public List<Tag> Tags { get; set; } = [];
    public List<Item> Items { get; set; } = [];
}
public sealed class Tag
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
    public string Kind { get; set; } = "text";
    public List<string> Properties { get; set; } = [];
}
public sealed class Item
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, Value> Values { get; set; } = [];
    public bool Draft { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Value
{
    public string? Text { get; set; }
    public decimal? Number { get; set; }
    public List<string> Refs { get; set; } = [];
}
public record ItemRequest(long Revision, Item Item);
public record TagRequest(long Revision, Tag Tag);
public record RevisionRequest(long Revision);
public record ImportRequest(long Revision, string Path, bool Recursive);
