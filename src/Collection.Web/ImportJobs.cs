using System.Text.Json;
using System.Threading.Channels;
namespace Collection.Web;
public sealed class ImportJob
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Path {get;set;}="";
    public bool Recursive {get;set;}
    public string Status {get;set;}="queued";
    public int Scanned {get;set;}
    public int Added {get;set;}
    public int Skipped {get;set;}
    public int Failed {get;set;}
    public List<string> Errors {get;set;}=[];
    public DateTimeOffset CreatedAt {get;set;}=DateTimeOffset.UtcNow;
}
public sealed class ImportJobs:BackgroundService
{
    private readonly LibraryStore store;
    private readonly string file;
    private readonly object gate=new();
    private readonly List<ImportJob> jobs;
    private readonly Dictionary<string,CancellationTokenSource> cancellations=new();
    private readonly Channel<string> queue=Channel.CreateBounded<string>(32);
    public ImportJobs(LibraryStore store,string folder)
    {
        this.store=store;file=System.IO.Path.Combine(folder,"import-jobs.json");
        jobs=File.Exists(file)?JsonSerializer.Deserialize<List<ImportJob>>(File.ReadAllText(file),LibraryStore.Json)??[]:[];
        foreach(var job in jobs.Where(j=>j.Status is "queued" or "running" or "cancelling"))job.Status="interrupted";
        Persist();
    }
    private void Persist(){var tmp=file+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(jobs,LibraryStore.Json));File.Move(tmp,file,true);}
    public List<ImportJob> List(){lock(gate)return JsonSerializer.Deserialize<List<ImportJob>>(JsonSerializer.Serialize(jobs,LibraryStore.Json),LibraryStore.Json)!;}
    public ImportJob Add(string path,bool recursive)
    {
        path=System.IO.Path.GetFullPath(path.Trim().Trim('"'));
        if(!File.Exists(path)&&!Directory.Exists(path))throw new ArgumentException("文件或文件夹不存在。");
        lock(gate)
        {
            if(jobs.Count(j=>j.Status is "queued" or "running" or "cancelling")>=32)throw new ArgumentException("任务队列已满。");
            var job=new ImportJob{Path=path,Recursive=recursive};jobs.Add(job);cancellations[job.Id]=new();
            while(jobs.Count>100){var old=jobs.FirstOrDefault(j=>j.Status is not ("queued" or "running" or "cancelling"));if(old==null)break;jobs.Remove(old);}
            Persist();queue.Writer.TryWrite(job.Id);return new ImportJob{Id=job.Id,Path=path,Recursive=recursive};
        }
    }
    public void Cancel(string id){lock(gate){var job=jobs.FirstOrDefault(j=>j.Id==id)??throw new ArgumentException("任务不存在。");if(cancellations.TryGetValue(id,out var c)){c.Cancel();job.Status="cancelling";Persist();}}}
    public ImportJob Retry(string id){lock(gate){var old=jobs.FirstOrDefault(j=>j.Id==id)??throw new ArgumentException("任务不存在。");if(old.Status is "running" or "queued" or "cancelling")throw new ArgumentException("请等待任务结束。");return Add(old.Path,old.Recursive);}}
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try{await foreach(var id in queue.Reader.ReadAllAsync(stoppingToken))await Task.Run(()=>Run(id,stoppingToken),stoppingToken);}catch(OperationCanceledException){}
    }
    private void Run(string id,CancellationToken stoppingToken)
    {
        ImportJob job;CancellationTokenSource linked;
        lock(gate){job=jobs.Single(j=>j.Id==id);linked=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken,cancellations[id].Token);job.Status="running";Persist();}
        using(linked)
        {
            try
            {
                var batch=new List<Item>();
                void Flush(){if(batch.Count==0)return;linked.Token.ThrowIfCancellationRequested();var result=store.AddCandidates(batch);lock(gate){job.Added+=result.Added;job.Skipped+=result.Skipped;Persist();}batch.Clear();}
                foreach(var path in FileImporter.Enumerate(job.Path,job.Recursive))
                {
                    linked.Token.ThrowIfCancellationRequested();lock(gate)job.Scanned++;
                    try{batch.Add(FileImporter.Candidate(path));}
                    catch(Exception e) when(e is IOException or UnauthorizedAccessException){lock(gate){job.Failed++;if(job.Errors.Count<100)job.Errors.Add(path+": "+e.Message);}}
                    if(batch.Count>=25)Flush();
                }
                Flush();lock(gate)job.Status="completed";
            }
            catch(OperationCanceledException){lock(gate)job.Status=stoppingToken.IsCancellationRequested?"interrupted":"cancelled";}
            catch(Exception e){lock(gate){job.Status="failed";if(job.Errors.Count<100)job.Errors.Add(e.Message);}}
            finally{lock(gate){cancellations[id].Dispose();cancellations.Remove(id);Persist();}}
        }
    }
}
