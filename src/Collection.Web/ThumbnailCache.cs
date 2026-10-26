using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
namespace Collection.Web;
public sealed class ThumbnailCache
{
    private readonly string folder;
    private readonly SemaphoreSlim gate=new(1,1);
    private const long MaxBytes=256L*1024*1024;
    public ThumbnailCache(string data){folder=Path.Combine(data,"thumbnails");Directory.CreateDirectory(folder);}
    public async Task<string> Get(string source,CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try{return await Task.Run(()=>Generate(source,cancellation),cancellation);}finally{gate.Release();}
    }
    private string Generate(string source,CancellationToken cancellation)
    {
        var info=new FileInfo(source);
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v1|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|320")));
        var target=Path.Combine(folder,key+".jpg");
        if(File.Exists(target)){File.SetLastWriteTimeUtc(target,DateTime.UtcNow);return target;}
        cancellation.ThrowIfCancellationRequested();
        using var stream=File.OpenRead(source);
        using var codec=SKCodec.Create(stream)??throw new ArgumentException("无法解码图片。");
        if(codec.Info.Width<=0||codec.Info.Height<=0||(long)codec.Info.Width*codec.Info.Height>40_000_000)throw new ArgumentException("图片尺寸超过缩略图解码限制。");
        using var bitmap=SKBitmap.Decode(codec)??throw new ArgumentException("无法解码图片。");
        // Orient the first frame, then scale; animation and video decoding are intentionally separate.
        bool swap=(int)codec.EncodedOrigin>=5;
        int width=swap?bitmap.Height:bitmap.Width,height=swap?bitmap.Width:bitmap.Height;
        float scale=Math.Min(1,320f/Math.Max(width,height));int w=Math.Max(1,(int)(width*scale)),h=Math.Max(1,(int)(height*scale));
        using var surface=SKSurface.Create(new SKImageInfo(w,h));var canvas=surface.Canvas;canvas.Clear(SKColors.White);canvas.Scale(scale);
        switch((int)codec.EncodedOrigin)
        {
            case 2:canvas.Translate(width,0);canvas.Scale(-1,1);break;
            case 3:canvas.Translate(width,height);canvas.RotateDegrees(180);break;
            case 4:canvas.Translate(0,height);canvas.Scale(1,-1);break;
            case 5:canvas.RotateDegrees(90);canvas.Scale(1,-1);break;
            case 6:canvas.Translate(width,0);canvas.RotateDegrees(90);break;
            case 7:canvas.Translate(width,height);canvas.RotateDegrees(90);canvas.Scale(-1,1);break;
            case 8:canvas.Translate(0,height);canvas.RotateDegrees(-90);break;
        }
        using var image=SKImage.FromBitmap(bitmap);canvas.DrawImage(image,0,0,new SKSamplingOptions(SKFilterMode.Linear));
        using var thumb=surface.Snapshot();using var encoded=thumb.Encode(SKEncodedImageFormat.Jpeg,80);
        cancellation.ThrowIfCancellationRequested();var temp=target+".tmp";
        try{using(var output=File.Create(temp))encoded.SaveTo(output);File.Move(temp,target,true);}finally{if(File.Exists(temp))File.Delete(temp);}
        var files=new DirectoryInfo(folder).GetFiles("*.jpg").OrderBy(f=>f.LastWriteTimeUtc).ToList();long total=files.Sum(f=>f.Length);
        foreach(var old in files){if(total<=MaxBytes)break;if(old.FullName==target)continue;try{File.Delete(old.FullName);total-=old.Length;}catch(IOException){}}
        return target;
    }
    public async Task Clear(CancellationToken token){await gate.WaitAsync(token);try{foreach(var file in Directory.EnumerateFiles(folder))File.Delete(file);}finally{gate.Release();}}
}
