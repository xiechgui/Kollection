namespace Collection.Web;
public static class FileImporter
{
    public static IEnumerable<string> Enumerate(string source,bool recursive)
    {
        var path=Path.GetFullPath(source.Trim().Trim('"'));
        if(File.Exists(path))return new[]{path};
        if(!Directory.Exists(path))throw new ArgumentException("文件或文件夹不存在。");
        return Directory.EnumerateFiles(path,"*",new EnumerationOptions{RecurseSubdirectories=recursive,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint|FileAttributes.System});
    }
    public static Item Candidate(string path)
    {
        var f=new FileInfo(Path.GetFullPath(path));var ext=f.Extension.ToLowerInvariant();var tags=new List<string>{"file"};
        if(new[]{".jpg",".jpeg",".png",".gif",".webp",".bmp"}.Contains(ext))tags.Add("image");
        if(new[]{".mp4",".mkv",".mov",".webm",".avi",".m4v"}.Contains(ext))tags.Add("video");
        if(new[]{".txt",".md"}.Contains(ext))tags.Add("text");
        return new Item{Name=f.Name,Tags=tags,Draft=true,Source=f.FullName,Values=new(){["path"]=new(){Text=f.FullName},["filename"]=new(){Text=f.Name},["size"]=new(){Number=f.Length}}};
    }
    public static object Import(LibraryStore store,string source,bool recursive,long revision)
    {
        // Compatibility endpoint; the UI uses the background job endpoint.
        store.AddCandidates([],revision);int added=0,skipped=0,failed=0;bool limited=false;
        foreach(var path in Enumerate(source,recursive))
        {
            if(added>=500){limited=true;break;}
            try{var result=store.AddCandidates([Candidate(path)]);added+=result.Added;skipped+=result.Skipped;}
            catch(IOException){failed++;}catch(UnauthorizedAccessException){failed++;}
        }
        return new{added,skipped,failed,limited};
    }
}
