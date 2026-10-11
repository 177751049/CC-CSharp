using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CCRadar.Parser.Engine;

/// <summary>
/// 资源存储（移植 Python: gateway_parser.AssetStore）——目录或 ZIP 兼容的 .pak。
/// 发布形态：NvaAssets.pak 由 zipfile 打包（items/ avatars/ maps2d/ map_catalog.json …）。
/// 路径键统一为相对 POSIX 路径；拒绝空段 / "." / ".." / NUL（目录穿越防护）。
/// </summary>
public sealed class AssetStore : IDisposable
{
    private readonly string? _directory;
    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    // ZipArchive 不是线程安全的：多个请求并发 Open() 会争用底层流位置，轻则抛
    // "A local file header is corrupt"，重则返回错位字节（地图瓦片花屏的直接原因）。
    // 用锁串行化归档读取；条目内容不可变，故命中缓存可并发读。
    private readonly object _readGate = new();
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private const long CacheLimitBytes = 96L * 1024 * 1024;
    private long _cacheBytes;

    public AssetStore(string? directory = null, string? archive = null)
    {
        if (!string.IsNullOrEmpty(directory)) _directory = Path.GetFullPath(directory);
        if (!string.IsNullOrEmpty(archive))
        {
            _zip = ZipFile.OpenRead(Path.GetFullPath(archive));
            foreach (var e in _zip.Entries)
            {
                if (e.Name.Length == 0) continue;   // 目录项（以 / 结尾）跳过
                _entries[e.FullName] = e;
            }
        }
    }

    /// <summary>true = ZIP 归档形态；false = 目录形态。两者都未配置时为"空存储"。</summary>
    public bool IsArchive => _zip is not null;
    public bool IsConfigured => _zip is not null || _directory is not null;
    public int EntryCount => _entries.Count;

    /// <summary>Python: AssetStore._key —— 解码、统一分隔符、去首尾斜杠，非法返回 false。</summary>
    internal static bool TryKey(string value, out string key)
    {
        key = "";
        var decoded = Uri.UnescapeDataString(value).Replace('\\', '/').Trim('/');
        if (decoded.Length == 0 || decoded.Contains('\0')) return false;
        var parts = decoded.Split('/');
        foreach (var p in parts)
        {
            if (p is "" or "." or "..") return false;
        }
        key = string.Join('/', parts);
        return true;
    }

    public bool Exists(string key)
    {
        if (!TryKey(key, out var k)) return false;
        if (_zip is not null) return _entries.ContainsKey(k);
        return _directory is not null &&
               File.Exists(Path.Combine(_directory, k.Replace('/', Path.DirectorySeparatorChar)));
    }

    public byte[] ReadBytes(string key)
    {
        if (!TryKey(key, out var k)) throw new ArgumentException("invalid asset path", nameof(key));
        if (_zip is not null)
        {
            if (_cache.TryGetValue(k, out var cached)) return cached;
            var bytes = ReadZipEntry(k);
            Cache(k, bytes);
            return bytes;
        }
        return File.ReadAllBytes(Path.Combine(_directory!, k.Replace('/', Path.DirectorySeparatorChar)));
    }

    // 归档读取必须串行：并发 Open()/读会破坏共享流位置（见字段注释）。
    private byte[] ReadZipEntry(string k)
    {
        lock (_readGate)
        {
            if (!_entries.TryGetValue(k, out var entry)) throw new FileNotFoundException($"资源不存在：{k}");
            using var s = entry.Open();
            using var ms = new MemoryStream(entry.Length is > 0 and <= int.MaxValue ? (int)entry.Length : 0);
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    // 内容不可变的字节缓存（FIFO 上限淘汰），避免地图页反复请求同一批瓦片时重复读盘。
    private void Cache(string k, byte[] bytes)
    {
        if (bytes.Length > CacheLimitBytes / 4) return;
        lock (_cacheOrder)
        {
            if (_cache.TryAdd(k, bytes)) { _cacheBytes += bytes.Length; _cacheOrder.Enqueue(k); }
            while (_cacheBytes > CacheLimitBytes && _cacheOrder.Count > 0)
            {
                var oldest = _cacheOrder.Dequeue();
                if (_cache.TryRemove(oldest, out var removed)) _cacheBytes -= removed.Length;
            }
        }
    }

    /// <summary>Python: read_json —— UTF-8（容忍 BOM）。</summary>
    public JsonNode? ReadJson(string key)
    {
        var text = Encoding.UTF8.GetString(ReadBytes(key)).TrimStart('\uFEFF');
        return JsonNode.Parse(text);
    }

    public void Dispose() { _cache.Clear(); _zip?.Dispose(); }
}

/// <summary>
/// 静态资源服务（移植 Python: er_radar.m3d_serve + AssetStore.serve 的 HTTP 侧）。
/// 语义：MIME 表、ETag/If-None-Match 304、Cache-Control（?v= → immutable）、
/// 目录穿越防护、Access-Control-Allow-Origin: *、大文件分块写出。
/// 不做 gzip 内容编码（当前发布无 .gz 边车；避免 HttpListener 受限头问题）。
/// </summary>
internal static class StaticServe
{
    private static readonly Dictionary<string, string> MimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".glb"] = "model/gltf-binary",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".bin"] = "application/octet-stream",
        [".wasm"] = "application/wasm",
        [".txt"] = "text/plain; charset=utf-8",
    };

    public static string Mime(string path)
    {
        var suffix = Path.GetExtension(path);
        return suffix.Length > 0 && MimeMap.TryGetValue(suffix, out var m) ? m : "application/octet-stream";
    }

    private static void ApplyHeaders(HttpListenerContext ctx, int code, string contentType,
        long contentLength, string? etag, string? cacheControl)
    {
        var res = ctx.Response;
        res.StatusCode = code;
        if (contentType.Length > 0) res.ContentType = contentType;
        res.ContentLength64 = contentLength;
        if (etag is not null) res.Headers["ETag"] = etag;
        if (cacheControl is not null) res.Headers["Cache-Control"] = cacheControl;
        res.Headers["Access-Control-Allow-Origin"] = "*";
    }

    public static void WriteBytes(HttpListenerContext ctx, int code, string contentType, byte[] body,
        string? etag = null, string? cacheControl = null)
    {
        ApplyHeaders(ctx, code, contentType, body.Length, etag, cacheControl);
        try
        {
            if (ctx.Request.HttpMethod != "HEAD" && body.Length > 0) ctx.Response.OutputStream.Write(body);
        }
        finally { ctx.Response.OutputStream.Close(); }
    }

    public static void WriteJson(HttpListenerContext ctx, JsonNode? node)
        => WriteBytes(ctx, 200, "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null"), cacheControl: "no-store");

    private static string CacheControlFor(HttpListenerContext ctx, int cacheS, bool noCache)
    {
        var pq = ctx.Request.Url!.PathAndQuery;
        if (pq.Contains("?v=", StringComparison.Ordinal)) return "public, max-age=31536000, immutable";
        return noCache ? "no-cache" : $"public, max-age={cacheS}";
    }

    /// <summary>把磁盘文件流式写出（含 ETag/304/缓存头）。</summary>
    public static void ServeFile(HttpListenerContext ctx, string file, int cacheS, bool noCache)
    {
        var fi = new FileInfo(file);
        var etag = $"\"{fi.LastWriteTimeUtc.Ticks:x}-{fi.Length:x}\"";
        if (ctx.Request.Headers["If-None-Match"] == etag)
        {
            WriteBytes(ctx, 304, "", [], etag);
            return;
        }
        var cacheControl = CacheControlFor(ctx, cacheS, noCache);
        ApplyHeaders(ctx, 200, Mime(file), fi.Length, etag, cacheControl);
        try
        {
            if (ctx.Request.HttpMethod == "HEAD") return;
            using var fs = File.OpenRead(file);
            var buffer = new byte[262144];
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0) ctx.Response.OutputStream.Write(buffer, 0, n);
        }
        catch (Exception) { /* 客户端断开：忽略（Python: BrokenPipe 处理） */ }
        finally { ctx.Response.OutputStream.Close(); }
    }

    /// <summary>
    /// 目录前缀服务（Python: m3d_serve）。命中（含 404/403）返回 true，非本前缀返回 false。
    /// </summary>
    public static bool ServeDirectory(HttpListenerContext ctx, string requestPath, string prefix, string root,
        int cacheS = 604800, bool noCache = false)
    {
        if (!requestPath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            WriteBytes(ctx, 404, "text/plain; charset=utf-8", []);   // 资源目录缺失 → 404
            return true;
        }
        var rel = Uri.UnescapeDataString(requestPath[prefix.Length..]);
        if (rel.Length == 0 || rel.EndsWith('/')) rel = "index.html";
        string full;
        try
        {
            var rootFull = Path.GetFullPath(root);
            full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                WriteBytes(ctx, 403, "text/plain; charset=utf-8", []);
                return true;
            }
        }
        catch
        {
            WriteBytes(ctx, 403, "text/plain; charset=utf-8", []);
            return true;
        }
        if (!File.Exists(full))
        {
            WriteBytes(ctx, 404, "text/plain; charset=utf-8", []);
            return true;
        }
        ServeFile(ctx, full, cacheS, noCache);
        return true;
    }

    /// <summary>
    /// 归档/目录存储前缀服务（Python: AssetStore.serve）。命中返回 true，非本前缀返回 false。
    /// </summary>
    public static bool ServeStore(AssetStore? store, HttpListenerContext ctx, string requestPath, string prefix,
        int cacheS = 604800, bool noCache = false)
    {
        if (!requestPath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (store is null || !store.IsConfigured)
        {
            WriteBytes(ctx, 404, "text/plain; charset=utf-8", []);
            return true;
        }
        var raw = requestPath[prefix.Length..];
        if (raw.Length == 0) raw = "index.html";
        if (!AssetStore.TryKey(raw, out var key))
        {
            WriteBytes(ctx, 403, "text/plain; charset=utf-8", []);
            return true;
        }
        if (!store.Exists(key))
        {
            WriteBytes(ctx, 404, "text/plain; charset=utf-8", []);
            return true;
        }
        var body = store.ReadBytes(key);
        var etag = $"\"{body.Length:x}-{key.GetHashCode():x8}\"";
        if (ctx.Request.Headers["If-None-Match"] == etag)
        {
            WriteBytes(ctx, 304, "", [], etag);
            return true;
        }
        WriteBytes(ctx, 200, Mime(key), body, etag, CacheControlFor(ctx, cacheS, noCache));
        return true;
    }
}
