using Collection.Web;
using Microsoft.AspNetCore.StaticFiles;
using System.Diagnostics;
using System.Text.Json;

var publishedRoot=Path.Combine(AppContext.BaseDirectory,"wwwroot");
var webRoot=File.Exists(Path.Combine(publishedRoot,"index.html"))?publishedRoot:Path.Combine(Directory.GetCurrentDirectory(),"wwwroot");
var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=args,WebRootPath=webRoot});
var port=int.TryParse(Environment.GetEnvironmentVariable("COLLECTION_PORT"),out var p)?p:5278;
if(port<1024||port>65535)throw new ArgumentException("COLLECTION_PORT must be between 1024 and 65535.");
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var folder=Environment.GetEnvironmentVariable("COLLECTION_DATA")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Shicang");
var store=new LibraryStore(folder);
var app=builder.Build();
app.Use(async(ctx,next)=>{
    // Reject DNS-rebinding hosts and cross-origin calls into the local file API.
    if(ctx.Request.Host.Host!="127.0.0.1"&&ctx.Request.Host.Host!="localhost"){ctx.Response.StatusCode=403;return;}
    var origin=ctx.Request.Headers.Origin.ToString();
    if(origin.Length>0&&origin!=$"http://{ctx.Request.Host}"){ctx.Response.StatusCode=403;return;}
    if(ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers["Sec-Fetch-Site"]=="cross-site"){ctx.Response.StatusCode=403;return;}
    if(ctx.Request.Method is not ("GET" or "HEAD") && ctx.Request.Headers["X-Collection-Client"]!="local-ui"){ctx.Response.StatusCode=403;return;}
    ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
    try {await next();}
    catch(ConflictException e){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error=e.Message});}
    catch(Exception e) when(e is ArgumentException or IOException or UnauthorizedAccessException or JsonException){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error=e.Message});}
});
app.UseDefaultFiles();app.UseStaticFiles();
app.MapGet("/api/state",()=>store.Read());
app.MapGet("/api/export",()=>Results.File(JsonSerializer.SerializeToUtf8Bytes(store.Read(),LibraryStore.Json),"application/json","collection-export.json"));
app.MapPost("/api/items",(ItemRequest r)=>store.Change(r.Revision,s=>{
    if(s.Items.Any(i=>i.Id==r.Item.Id))throw new ArgumentException("ID 已存在。");r.Item.Draft=false;r.Item.Source=null;r.Item.UpdatedAt=DateTimeOffset.UtcNow;s.Items.Add(r.Item);
}));
app.MapPut("/api/items/{id}",(string id,ItemRequest r)=>store.Change(r.Revision,s=>{
    var old=s.Items.FirstOrDefault(i=>i.Id==id)??throw new ArgumentException("对象不存在。");
    r.Item.Id=id;r.Item.Draft=old.Draft;r.Item.Source=old.Source;r.Item.UpdatedAt=DateTimeOffset.UtcNow;s.Items[s.Items.IndexOf(old)]=r.Item;
}));
app.MapPost("/api/items/{id}/confirm",(string id,ItemRequest r)=>store.Change(r.Revision,s=>{
    var old=s.Items.FirstOrDefault(i=>i.Id==id&&i.Draft)??throw new ArgumentException("暂存对象不存在或已确认。");
    r.Item.Id=id;r.Item.Draft=false;r.Item.Source=old.Source;r.Item.UpdatedAt=DateTimeOffset.UtcNow;s.Items[s.Items.IndexOf(old)]=r.Item;
}));
app.MapDelete("/api/items/{id}",(string id,long revision)=>store.Change(revision,s=>{
    if(s.Items.Any(i=>i.Id!=id&&i.Values.Values.Any(v=>v.Refs.Contains(id))))throw new ConflictException("该对象仍被引用，请先解除引用。");
    if(s.Items.RemoveAll(i=>i.Id==id)==0)throw new ArgumentException("对象不存在。");
}));
app.MapPost("/api/tags",(TagRequest r)=>store.Change(r.Revision,s=>{
    var old=s.Tags.FirstOrDefault(t=>t.Id==r.Tag.Id);
    if(old!=null)s.Tags[s.Tags.IndexOf(old)]=r.Tag;else s.Tags.Add(r.Tag);
}));
app.MapPost("/api/import",(ImportRequest r)=>{
    object? summary=null;var state=store.Change(r.Revision,s=>summary=FileImporter.Import(s,r.Path,r.Recursive));return new{state,summary};
});
app.MapGet("/api/items/{id}/media",(string id)=>{
    var s=store.Read();var i=s.Items.FirstOrDefault(i=>i.Id==id&&!i.Draft);
    if(i==null||!LibraryStore.EffectiveTags(s,i).Contains("file"))return Results.NotFound();
    var path=i.Values.GetValueOrDefault("path")?.Text;
    if(path==null||!File.Exists(path))return Results.NotFound();
    var provider=new FileExtensionContentTypeProvider();
    if(!provider.TryGetContentType(path,out var type)||!(type.StartsWith("image/")&&type!="image/svg+xml"||type.StartsWith("video/")))return Results.BadRequest(new{error="此类型不支持浏览器预览。"});
    return Results.File(Path.GetFullPath(path),type,enableRangeProcessing:true);
});
app.MapPost("/api/items/{id}/locate",(string id)=>{
    var s=store.Read();var i=s.Items.FirstOrDefault(i=>i.Id==id&&!i.Draft);
    if(i==null||!LibraryStore.EffectiveTags(s,i).Contains("file"))return Results.BadRequest(new{error="不是正式文件对象。"});
    var path=i.Values.GetValueOrDefault("path")?.Text;
    if(path==null||!File.Exists(path))return Results.BadRequest(new{error="文件不存在，请修改路径。"});
    if(!OperatingSystem.IsWindows())return Results.BadRequest(new{error="定位文件仅在 Windows 可用。"});
    var start=new ProcessStartInfo("explorer.exe");start.ArgumentList.Add("/select,"+Path.GetFullPath(path));Process.Start(start);return Results.Ok();
});
Console.WriteLine($"拾藏: http://127.0.0.1:{port}   数据: {folder}");
if(args.Contains("--open"))app.Lifetime.ApplicationStarted.Register(()=>{try{Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}"){UseShellExecute=true});}catch{}});
app.Run();
