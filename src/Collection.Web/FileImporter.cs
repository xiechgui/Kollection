namespace Collection.Web;
public static class FileImporter
{
    public static object Import(LibraryState state, string source, bool recursive)
    {
        var path=Path.GetFullPath(source.Trim().Trim('"'));
        if(!File.Exists(path)&&!Directory.Exists(path)) throw new ArgumentException("文件或文件夹不存在。");
        var paths=File.Exists(path)?new[]{path}:Directory.EnumerateFiles(path,"*",new EnumerationOptions{RecurseSubdirectories=recursive,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint|FileAttributes.System});
        var known=state.Items.Select(i=>i.Source).Where(p=>p!=null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added=0,skipped=0,failed=0; bool limited=false;
        foreach(var p in paths)
        {
            if(added>=500 || state.Items.Count>=5000){limited=true;break;}
            var full=Path.GetFullPath(p);
            if(!known.Add(full)){skipped++;continue;}
            try
            {
                var f=new FileInfo(full); var ext=f.Extension.ToLowerInvariant();
                var tags=new List<string>{"file"};
                if(new[]{".jpg",".jpeg",".png",".gif",".webp",".bmp"}.Contains(ext))tags.Add("image");
                if(new[]{".mp4",".mkv",".mov",".webm",".avi",".m4v"}.Contains(ext))tags.Add("video");
                if(new[]{".txt",".md"}.Contains(ext))tags.Add("text");
                state.Items.Add(new Item{Name=f.Name,Tags=tags,Draft=true,Source=full,Values=new(){["path"]=new(){Text=full},["filename"]=new(){Text=f.Name},["size"]=new(){Number=f.Length}}});added++;
            }
            catch(IOException){failed++;} catch(UnauthorizedAccessException){failed++;}
        }
        return new {added,skipped,failed,limited};
    }
}
