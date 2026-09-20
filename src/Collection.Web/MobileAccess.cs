using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Collection.Web;

// LAN access is opt-in. The desktop-only launcher keeps its loopback boundary.
public sealed class MobileAccess
{
    public string? Address { get; }
    public string AccessKey { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> sessions = new();
    private const string CookieName = "kollection-session";

    public MobileAccess(bool enabled)
    {
        if (!enabled) return;
        Address = Environment.GetEnvironmentVariable("COLLECTION_LAN_IP");
        Address ??= NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address.ToString()).FirstOrDefault(IsPrivateIPv4);
        if (Address == null || !IsPrivateIPv4(Address))
            throw new ArgumentException("找不到局域网 IPv4 地址。请连接 Wi-Fi，或设置 COLLECTION_LAN_IP 为电脑的私有 IPv4 地址。");
    }
    private static bool IsPrivateIPv4(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
    }
    public bool AllowedHost(string host) => host is "127.0.0.1" or "localhost" || Address != null && host == Address;
    public static bool IsLocal(HttpContext ctx) => ctx.Request.Host.Host is "127.0.0.1" or "localhost"
        && ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
    private static bool SameSecret(string? supplied, string expected) => supplied != null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));

    public async Task Handle(HttpContext ctx, RequestDelegate next)
    {
        if (IsLocal(ctx)) { await next(ctx); return; }
        ctx.Response.Headers.CacheControl = "no-store";
        if (Address == null) { ctx.Response.StatusCode = 403; return; }
        if (ctx.Request.Path == "/api/login" && ctx.Request.Method == "POST")
        {
            var request = await ctx.Request.ReadFromJsonAsync<LoginRequest>();
            if (!SameSecret(request?.Key?.Trim(), AccessKey))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "访问口令不正确，请查看电脑的启动窗口。" });
                return;
            }
            var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            foreach(var old in sessions.Where(s => s.Value <= DateTimeOffset.UtcNow)) sessions.TryRemove(old.Key, out _);
            sessions[session] = DateTimeOffset.UtcNow.AddHours(12);
            ctx.Response.Cookies.Append(CookieName, session, new CookieOptions
            { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(12), Secure = ctx.Request.IsHttps });
            await ctx.Response.WriteAsJsonAsync(new { ok = true }); return;
        }
        if (ctx.Request.Cookies[CookieName] is { } token && sessions.TryGetValue(token, out var expiry) && expiry > DateTimeOffset.UtcNow) { await next(ctx); return; }
        if (ctx.Request.Path == "/app.css" || ctx.Request.Path == "/login.js") { await next(ctx); return; }
        if (ctx.Request.Path == "/" || ctx.Request.Path == "/index.html")
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(Path.Combine(ctx.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootPath, "login.html"));
            return;
        }
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { error = "请返回首页，输入电脑启动窗口中的访问口令。" });
    }
    public void Logout(HttpContext ctx)
    {
        if(ctx.Request.Cookies[CookieName] is { } token) sessions.TryRemove(token, out _);
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
    }
    private sealed record LoginRequest(string? Key);
}
