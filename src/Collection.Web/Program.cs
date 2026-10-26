using Collection.Web;
using Microsoft.AspNetCore.StaticFiles;
using System.Diagnostics;
using System.Text.Json;

var publishedRoot=Path.Combine(AppContext.BaseDirectory,"wwwroot");
var webRoot=File.Exists(Path.Combine(publishedRoot,"index.html"))?publishedRoot:Path.Combine(Directory.GetCurrentDirectory(),"wwwroot");
var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=args,WebRootPath=webRoot});
var port=int.TryParse(Environment.GetEnvironmentVariable("COLLECTION_PORT") ?? Environment.GetEnvironmentVariable("PORT"),out var p)?p:5278;
if(port<1024||port>65535)throw new ArgumentException("COLLECTION_PORT must be between 1024 and 65535.");
var mobile = new MobileAccess(args.Contains("--lan"));
builder.WebHost.UseUrls(mobile.Cloud ? [$"http://0.0.0.0:{port}"] : mobile.Address == null ? [$"http://127.0.0.1:{port}"] : [$"http://127.0.0.1:{port}", $"http://{mobile.Address}:{port}"]);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 101L * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 101L * 1024 * 1024);
var folder=Environment.GetEnvironmentVariable("COLLECTION_DATA")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Shicang");
if(mobile.Cloud && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COLLECTION_DATA"))) throw new ArgumentException("Cloud mode requires COLLECTION_DATA on a persistent volume.");
var store=new LibraryStore(folder);
var jobs=new ImportJobs(store,folder);
builder.Services.AddHostedService(_=>jobs);
var thumbnails=new ThumbnailCache(folder);
var app=builder.Build();
app.Use(async(ctx,next)=>{
    // Platform health probes do not expose collection data or require login.
    if(mobile.Cloud && ctx.Request.Path == "/health" && ctx.Request.Method == "GET") { await ctx.Response.WriteAsJsonAsync(new { status = "ok" }); return; }
    // Reject DNS-rebinding hosts and cross-origin calls into the local file API.
    if(!mobile.AllowedHost(ctx.Request.Host.Host)){ctx.Response.StatusCode=403;return;}
    var origin=ctx.Request.Headers.Origin.ToString();
    if(origin.Length>0&&origin!=mobile.ExpectedOrigin(ctx)){ctx.Response.StatusCode=403;return;}
    if(ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers["Sec-Fetch-Site"]=="cross-site"){ctx.Response.StatusCode=403;return;}
    if(ctx.Request.Method is not ("GET" or "HEAD") && ctx.Request.Headers["X-Collection-Client"]!="local-ui"){ctx.Response.StatusCode=403;return;}
    ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
    try {await next();}
    catch(ConflictException e){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error=e.Message});}
    catch(Exception e) when(e is ArgumentException or IOException or UnauthorizedAccessException or JsonException){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error=e.Message});}
});
app.Use(mobile.Handle);
app.UseDefaultFiles();app.UseStaticFiles();
app.MapGet("/api/client", (HttpContext ctx) => new { remote = !mobile.CanAccessLocalFiles(ctx), uploadLimitMb = 100 });
app.MapPost("/api/logout", (HttpContext ctx) => { mobile.Logout(ctx); return Results.Ok(); });
app.MapGet("/api/state",()=>store.Read());
app.MapGet("/api/export",()=>Results.File(JsonSerializer.SerializeToUtf8Bytes(store.Read(),LibraryStore.Json),"application/json","collection-export.json"));
bool Compact(HttpContext ctx)=>ctx.Request.Query["compact"]=="1";
object Mutation(HttpContext ctx,Item? item=null)=>Compact(ctx)?new {item,catalog=store.Catalog()}:store.Read();
app.MapGet("/api/catalog",()=>store.Catalog());
app.MapGet("/api/items",(bool? draft,string? q,string? tag,int? offset,int? limit)=>store.Query(draft??false,q,tag,offset??0,limit??60));
app.MapGet("/api/items/{id}",(string id)=>store.Get(id) is {} item?Results.Ok(new{item,related=store.Related(id)}):Results.NotFound());
app.MapGet("/api/items/{id}/inbound",(string id,bool? draft,int? offset)=>store.Query(draft??false,null,null,offset??0,60,id));
app.MapPost("/api/items",(HttpContext ctx,ItemRequest r)=>Mutation(ctx,store.Save(r)));
app.MapPut("/api/items/{id}",(HttpContext ctx,string id,ItemRequest r)=>Mutation(ctx,store.Save(r,id)));
app.MapPost("/api/items/{id}/confirm",(HttpContext ctx,string id,ItemRequest r)=>Mutation(ctx,store.Save(r,id,true)));
app.MapDelete("/api/items/{id}",(HttpContext ctx,string id,long revision,long? version)=>{store.Delete(id,revision,version);return Mutation(ctx);});
app.MapPost("/api/drafts/batch",(BatchRequest r)=>{store.Batch(r);return store.Catalog();});
app.MapGet("/api/imports",(HttpContext ctx)=>mobile.CanAccessLocalFiles(ctx)?Results.Ok(jobs.List()):Results.StatusCode(403));
app.MapPost("/api/imports",(HttpContext ctx,ImportRequest r)=>mobile.CanAccessLocalFiles(ctx)?Results.Ok(jobs.Add(r.Path,r.Recursive)):Results.StatusCode(403));
app.MapPost("/api/imports/{id}/cancel",(HttpContext ctx,string id)=>{if(!mobile.CanAccessLocalFiles(ctx))return Results.StatusCode(403);jobs.Cancel(id);return Results.Ok();});
app.MapPost("/api/imports/{id}/retry",(HttpContext ctx,string id)=>mobile.CanAccessLocalFiles(ctx)?Results.Ok(jobs.Retry(id)):Results.StatusCode(403));
app.MapPost("/api/tags",(HttpContext ctx,TagRequest r)=>{store.Change(r.Revision,s=>{
    var old=s.Tags.FirstOrDefault(t=>t.Id==r.Tag.Id);
    if(old!=null)s.Tags[s.Tags.IndexOf(old)]=r.Tag;else s.Tags.Add(r.Tag);
});return Mutation(ctx);});
app.MapPost("/api/import",(HttpContext ctx, ImportRequest r)=>{
    if(!mobile.CanAccessLocalFiles(ctx)) return Results.Json(new {error="手机端请使用上传文件；电脑路径扫描只能在电脑端操作。"}, statusCode:403);
    var summary=FileImporter.Import(store,r.Path,r.Recursive,r.Revision);return Results.Ok(new{state=store.Read(),summary});
});
app.MapPost("/api/upload", async (HttpContext ctx) => {
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "请选择一个文件。" });
    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    if (form.Files.Count != 1 || !long.TryParse(form["revision"], out var revision))
        return Results.BadRequest(new { error = "每次请选择一个文件。" });
    var file = form.Files[0];
    if (file.Length > 100L * 1024 * 1024) return Results.BadRequest(new { error = "单个文件不能超过 100 MB；较大视频请在电脑端按路径导入。" });
    var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
    if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        return Results.BadRequest(new { error = "文件名无效或过长。" });
    var directory = Path.Combine(folder, "uploads", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
        var target = Path.Combine(directory, name);
        await using (var output = File.Create(target)) await file.CopyToAsync(output, ctx.RequestAborted);
        var result=store.AddCandidates([FileImporter.Candidate(target)],revision);
        var summary=new{added=result.Added,skipped=result.Skipped,failed=0,limited=false};
        return Compact(ctx)?Results.Ok(new{catalog=store.Catalog(),summary}):Results.Ok(new {state=store.Read(), summary});
    } catch { Directory.Delete(directory, true); throw; }
});
app.MapGet("/api/items/{id}/media",(string id)=>{
    var i=store.Get(id);
    if(i?.Draft==true)i=null;
    if(i==null||!store.IsFile(i))return Results.NotFound();
    var path=i.Values.GetValueOrDefault("path")?.Text;
    if(path==null||!File.Exists(path))return Results.NotFound();
    if(mobile.Cloud) {
        var uploads = Path.GetFullPath(Path.Combine(folder,"uploads")) + Path.DirectorySeparatorChar;
        if(!Path.GetFullPath(path).StartsWith(uploads, OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)) return Results.NotFound();
    }
    var provider=new FileExtensionContentTypeProvider();
    if(!provider.TryGetContentType(path,out var type)||!(type.StartsWith("image/")&&type!="image/svg+xml"||type.StartsWith("video/")))return Results.BadRequest(new{error="此类型不支持浏览器预览。"});
    return Results.File(Path.GetFullPath(path),type,enableRangeProcessing:true);
});
app.MapGet("/api/items/{id}/thumbnail",async (HttpContext ctx,string id)=>{
    var item=store.Get(id);if(item==null||item.Draft||!store.IsFile(item))return Results.NotFound();
    var path=item.Values.GetValueOrDefault("path")?.Text;
    if(path==null||!File.Exists(path))return Results.NotFound();
    if(mobile.Cloud){var uploads=Path.GetFullPath(Path.Combine(folder,"uploads"))+Path.DirectorySeparatorChar;if(!Path.GetFullPath(path).StartsWith(uploads,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))return Results.NotFound();}
    try{var cached=await thumbnails.Get(path,ctx.RequestAborted);return Results.File(await File.ReadAllBytesAsync(cached,ctx.RequestAborted),"image/jpeg");}
    catch(Exception e) when(e is ArgumentException or IOException or UnauthorizedAccessException){return Results.NotFound();}
});
app.MapPost("/api/thumbnails/rebuild",async (HttpContext ctx)=>{await thumbnails.Clear(ctx.RequestAborted);return Results.Ok();});
app.MapPost("/api/items/{id}/locate",(HttpContext ctx, string id)=>{
    if(!mobile.CanAccessLocalFiles(ctx))return Results.Json(new{error="资源管理器定位只能在电脑端操作。"},statusCode:403);
    var i=store.Get(id);
    if(i?.Draft==true)i=null;
    if(i==null||!store.IsFile(i))return Results.BadRequest(new{error="不是正式文件对象。"});
    var path=i.Values.GetValueOrDefault("path")?.Text;
    if(path==null||!File.Exists(path))return Results.BadRequest(new{error="文件不存在，请修改路径。"});
    if(!OperatingSystem.IsWindows())return Results.BadRequest(new{error="定位文件仅在 Windows 可用。"});
    var start=new ProcessStartInfo("explorer.exe");start.ArgumentList.Add("/select,"+Path.GetFullPath(path));Process.Start(start);return Results.Ok();
});
Console.WriteLine($"拾藏: http://127.0.0.1:{port}   数据: {folder}");
if(mobile.Address != null) {
    Console.WriteLine($"手机地址: http://{mobile.Address}:{port}   访问口令: {mobile.AccessKey}");
    Console.WriteLine("手机和电脑连接同一可信 Wi-Fi。HTTP 不加密；不要端口转发到公网。关闭此窗口即停止手机访问。");
}
if(args.Contains("--open"))app.Lifetime.ApplicationStarted.Register(()=>{try{Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}"){UseShellExecute=true});}catch{}});
app.Run();
