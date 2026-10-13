// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi
{
    /// <summary>一个解析好的 HTTP 请求（路径/查询/头/正文 + JSON 取值助手）。</summary>
    public sealed class ApiRequest
    {
        public string Method;
        public string Path;       // 不含 query 的路径
        public string RawTarget;  // 原始 request-target（排查用）
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body = "";
        public bool Http10;       // HTTP/1.0 → 默认不 keep-alive

        private object _json;
        private bool _jsonParsed;

        /// <summary>请求体按 JSON 解析（懒解析，失败为 null）。</summary>
        public object Json
        {
            get
            {
                if (!_jsonParsed)
                {
                    _jsonParsed = true;
                    if (!string.IsNullOrWhiteSpace(Body))
                    {
                        try { _json = JsonCompat.Parse(Body); }
                        catch { _json = null; }
                    }
                }
                return _json;
            }
        }

        public Dictionary<string, object> Obj => Json as Dictionary<string, object>;

        /// <summary>取字符串参数：先 JSON body，再 query。</summary>
        public string Str(string key)
        {
            var o = Obj;
            object v;
            if (o != null && o.TryGetValue(key, out v) && v != null)
                return v.ToString();
            if (Query.TryGetValue(key, out string q))
                return q;
            return null;
        }

        public bool Bool(string key, bool dflt = false)
        {
            var o = Obj;
            object v;
            if (o != null && o.TryGetValue(key, out v) && v != null)
            {
                if (v is bool b) return b;
                var s = v.ToString();
                if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                return dflt;
            }
            if (Query.TryGetValue(key, out string q))
                return q == "1" || q.Equals("true", StringComparison.OrdinalIgnoreCase);
            return dflt;
        }

        public long Long(string key, long dflt = 0)
        {
            var o = Obj;
            object v;
            if (o != null && o.TryGetValue(key, out v) && v != null)
            {
                if (v is long l) return l;
                if (long.TryParse(v.ToString(), out l)) return l;
                return dflt;
            }
            if (Query.TryGetValue(key, out string q) && long.TryParse(q, out long ql))
                return ql;
            return dflt;
        }

        /// <summary>取字符串数组参数（JSON 数组；query 里用英文逗号分隔）。</summary>
        public List<string> StrList(string key)
        {
            var o = Obj;
            object v;
            if (o != null && o.TryGetValue(key, out v) && v is object[] arr)
            {
                var list = new List<string>();
                foreach (var el in arr)
                    if (el != null) list.Add(el.ToString());
                return list;
            }
            if (Query.TryGetValue(key, out string q) && !string.IsNullOrEmpty(q))
                return new List<string>(q.Split(','));
            return null;
        }
    }

    /// <summary>处理结果：HTTP 状态码 + 数据载荷（序列化时统一包成 {ok, data|error}）。</summary>
    public sealed class ApiResponse
    {
        public int Status;
        public object Data;

        public static ApiResponse Ok(object data) => new ApiResponse { Status = 200, Data = data };
        public static ApiResponse Error(int status, string message)
            => new ApiResponse { Status = status, Data = message };
    }

    /// <summary>
    /// 本地 HTTP API 微服务：让脚本 / AI 代理能驱动下载器（解析分享、加任务、看进度、暂停恢复）。
    ///
    /// <para>【为什么不用 HttpListener】它底层是 HTTP.sys，监听任意前缀都要
    /// `netsh http add urlacl` 或管理员权限 —— 一个绿色软件不该要求用户配这个。</para>
    ///
    /// <para>【为什么不用 Kestrel】会引入 ASP.NET Core 共享框架依赖，
    /// 分发包体积和部署复杂度都不划算。</para>
    ///
    /// <para>所以用 TcpListener 自己实现一个**最小 HTTP/1.1 子集**：
    /// 只解析请求行 + 头部 + Content-Length 正文，支持 keep-alive。
    /// 只绑 127.0.0.1，配 Bearer token + Host 白名单两道闸。</para>
    ///
    /// <para>安全模型（与 aria2/qBittorrent 的本地 RPC 一致）：</para>
    /// <list type="bullet">
    /// <item>只监听回环地址，局域网/公网摸不到；</item>
    /// <item>除 /api/ping 外全部要求 token（Authorization: Bearer / X-Api-Token / ?token=）；</item>
    /// <item>Host 头必须是 127.0.0.1 / localhost / [::1] —— 挡住浏览器的
    ///    DNS 重绑定攻击（恶意网页把域名解析到 127.0.0.1 来 POST）；</item>
    /// <item>token 首次启动随机生成并随配置落盘，本机脚本从 GeZi.config.xml 读。</item>
    /// </list>
    /// </summary>
    public sealed class LocalApiServer : IDisposable
    {
        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxBodyBytes = 8 * 1024 * 1024;
        private const int RecvTimeoutMs = 60_000;

        private readonly int _port;
        private readonly string _token;
        private readonly Func<ApiRequest, Task<ApiResponse>> _handler;
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        /// <summary>实际监听的端口（配置端口被占用时可能换了别的）。</summary>
        public int Port { get; private set; }

        public LocalApiServer(int port, string token, Func<ApiRequest, Task<ApiResponse>> handler)
        {
            _port = port;
            _token = token ?? "";
            _handler = handler;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            Port = _port;
            _ = AcceptLoopAsync();
        }

        /// <summary>配置端口被占时，按 +1…+9 顺延重试（便携场景常见多实例/端口冲突）。</summary>
        public void StartWithFallback(int tries = 10)
        {
            for (int i = 0; i < tries; i++)
            {
                try
                {
                    _cts = new CancellationTokenSource();
                    _listener = new TcpListener(IPAddress.Loopback, _port + i);
                    _listener.Start();
                    Port = _port + i;
                    _ = AcceptLoopAsync();
                    return;
                }
                catch (SocketException)
                {
                    try { _listener?.Stop(); } catch { }
                    _listener = null;
                    if (i == tries - 1) throw;
                }
            }
        }

        private async Task AcceptLoopAsync()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch { break; }
                _ = HandleConnAsync(client);
            }
        }

        private async Task HandleConnAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    // 60 秒空闲即断开：脚本调用都是短请求，没必要无限挂连接。
                    // ReceiveTimeout 对 NetworkStream.ReadAsync 同样生效（超时抛 IOException）。
                    client.ReceiveTimeout = RecvTimeoutMs;
                    client.SendTimeout = RecvTimeoutMs;
                }
                catch { }

                var reader = new ConnReader(client.GetStream());
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        ApiRequest req;
                        try { req = await ReadRequestAsync(reader).ConfigureAwait(false); }
                        catch (HttpSyntaxException ex)
                        {
                            await WriteResponseAsync(client.GetStream(),
                                ApiResponse.Error(400, ex.Message), keepAlive: false).ConfigureAwait(false);
                            return;
                        }
                        if (req == null)
                            return;   // 对端关闭或超时

                        ApiResponse resp;
                        string host;
                        req.Headers.TryGetValue("host", out host);
                        if (!HostAllowed(host))
                        {
                            resp = ApiResponse.Error(403, "Host not allowed (localhost only)");
                        }
                        else if (req.Path != "/api/ping" && !Authed(req))
                        {
                            resp = ApiResponse.Error(401, "missing or invalid api token");
                        }
                        else
                        {
                            try { resp = await _handler(req).ConfigureAwait(false); }
                            catch (Exception ex)
                            {
                                resp = ApiResponse.Error(500, ex.GetType().Name + ": " + ex.Message);
                            }
                        }

                        bool keepAlive = !req.Http10;
                        string connHdr;
                        if (req.Headers.TryGetValue("connection", out connHdr))
                        {
                            if (connHdr.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0)
                                keepAlive = false;
                            else if (connHdr.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) >= 0)
                                keepAlive = true;
                        }
                        await WriteResponseAsync(client.GetStream(), resp, keepAlive).ConfigureAwait(false);
                        if (!keepAlive)
                            return;
                    }
                }
                catch { /* 超时/对端断开/写失败 —— 单连接问题不影响服务 */ }
            }
        }

        private bool HostAllowed(string host)
        {
            if (string.IsNullOrEmpty(host))
                return true;    // 极简客户端可能不发 Host；要求 token 已足够
            host = host.Trim();
            if (host.StartsWith("["))
            {
                int end = host.IndexOf(']');
                host = end > 0 ? host.Substring(0, end + 1) : host;
                return host == "[::1]";
            }
            int colon = host.LastIndexOf(':');
            if (colon > 0)
                host = host.Substring(0, colon);
            return host == "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }

        private bool Authed(ApiRequest req)
        {
            if (string.IsNullOrEmpty(_token))
                return true;    // 未配置 token 时不设防（理论不会发生：启动时会自动生成）
            string auth;
            if (req.Headers.TryGetValue("authorization", out auth)
                && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && string.Equals(auth.Substring(7).Trim(), _token, StringComparison.Ordinal))
                return true;
            string h;
            if (req.Headers.TryGetValue("x-api-token", out h)
                && string.Equals(h.Trim(), _token, StringComparison.Ordinal))
                return true;
            string q;
            if (req.Query.TryGetValue("token", out q) && string.Equals(q, _token, StringComparison.Ordinal))
                return true;
            return false;
        }

        private static async Task<ApiRequest> ReadRequestAsync(ConnReader reader)
        {
            string head = await reader.ReadHeaderBlockAsync(MaxHeaderBytes).ConfigureAwait(false);
            if (head == null)
                return null;

            var req = new ApiRequest();
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
                throw new HttpSyntaxException("empty request line");

            var parts = lines[0].Split(' ');
            if (parts.Length < 2)
                throw new HttpSyntaxException("bad request line");
            req.Method = parts[0].ToUpperInvariant();
            req.RawTarget = parts[1];
            req.Http10 = parts.Length >= 3 && parts[2].Equals("HTTP/1.0", StringComparison.OrdinalIgnoreCase);

            string target = req.RawTarget;
            int q = target.IndexOf('?');
            string rawPath = q >= 0 ? target.Substring(0, q) : target;
            string rawQuery = q >= 0 ? target.Substring(q + 1) : "";
            req.Path = UriDecode(rawPath);
            foreach (var kv in rawQuery.Split('&'))
            {
                if (kv.Length == 0) continue;
                int eq = kv.IndexOf('=');
                string k = eq >= 0 ? kv.Substring(0, eq) : kv;
                string v = eq >= 0 ? kv.Substring(eq + 1) : "";
                req.Query[UriDecode(k)] = UriDecode(v);
            }

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                req.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            string te;
            if (req.Headers.TryGetValue("transfer-encoding", out te)
                && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new HttpSyntaxException("chunked body not supported; use Content-Length");
            }

            int contentLength = 0;
            string cl;
            if (req.Headers.TryGetValue("content-length", out cl))
            {
                if (!int.TryParse(cl, out contentLength) || contentLength < 0)
                    throw new HttpSyntaxException("bad Content-Length");
                if (contentLength > MaxBodyBytes)
                    throw new HttpSyntaxException("body too large");
            }
            if (contentLength > 0)
            {
                var body = await reader.ReadExactAsync(contentLength).ConfigureAwait(false);
                if (body == null)
                    return null;
                req.Body = Encoding.UTF8.GetString(body);
            }
            return req;
        }

        private async Task WriteResponseAsync(NetworkStream ns, ApiResponse resp, bool keepAlive)
        {
            object payload;
            if (resp.Status >= 200 && resp.Status < 300)
                payload = new Dictionary<string, object> { { "ok", true }, { "data", resp.Data } };
            else
                payload = new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", resp.Data == null ? "error" : resp.Data.ToString() },
                };
            byte[] body = Encoding.UTF8.GetBytes(JsonCompat.Serialize(payload));

            string reason = ReasonOf(resp.Status);
            string head = "HTTP/1.1 " + resp.Status + " " + reason + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Connection: " + (keepAlive ? "keep-alive" : "close") + "\r\n"
                + "\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            await ns.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
            await ns.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        }

        private static string ReasonOf(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 411: return "Length Required";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                default: return "Status";
            }
        }

        private static string UriDecode(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // '+' → 空格只在 query 里算约定；路径段里保持原样也无妨（夸克 fid 无空格）。
            s = s.Replace('+', ' ');
            return Uri.UnescapeDataString(s);
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
        }

        private sealed class HttpSyntaxException : Exception
        {
            public HttpSyntaxException(string msg) : base(msg) { }
        }

        /// <summary>
        /// 连接级读缓冲：peek 到 "\r\n\r\n" 前不做任何消费性假设，
        /// 已多读的字节留给正文/下一个 keep-alive 请求（保序、不丢数据）。
        /// </summary>
        private sealed class ConnReader
        {
            private readonly NetworkStream _ns;
            private byte[] _buf = new byte[8192];
            private int _start, _len;

            public ConnReader(NetworkStream ns) { _ns = ns; }

            private async Task FillAsync()
            {
                if (_start > 0)
                {
                    Array.Copy(_buf, _start, _buf, 0, _len);
                    _start = 0;
                }
                if (_len == _buf.Length)
                    Array.Resize(ref _buf, _buf.Length * 2);
                int n = await _ns.ReadAsync(_buf, _len, _buf.Length - _len).ConfigureAwait(false);
                if (n <= 0)
                    throw new EndOfStreamException();
                _len += n;
            }

            /// <summary>读到 "\r\n\r\n" 为止（不含分隔符）；对端关连接返回 null，超限抛异常。</summary>
            public async Task<string> ReadHeaderBlockAsync(int maxBytes)
            {
                while (true)
                {
                    int idx = IndexOfHeaderEnd();
                    if (idx >= 0)
                    {
                        string s = Encoding.ASCII.GetString(_buf, _start, idx);
                        Consume(idx + 4);
                        return s;
                    }
                    if (_len > maxBytes)
                        throw new HttpSyntaxException("headers too large");
                    try { await FillAsync().ConfigureAwait(false); }
                    catch (EndOfStreamException)
                    {
                        if (_len == 0) return null;      // 干净关闭
                        throw new HttpSyntaxException("connection closed mid-headers");
                    }
                }
            }

            /// <summary>精确读 n 字节；对端提前关连接返回 null。</summary>
            public async Task<byte[]> ReadExactAsync(int n)
            {
                while (_len < n)
                {
                    try { await FillAsync().ConfigureAwait(false); }
                    catch (EndOfStreamException) { return null; }
                }
                var res = new byte[n];
                Array.Copy(_buf, _start, res, 0, n);
                Consume(n);
                return res;
            }

            private int IndexOfHeaderEnd()
            {
                // 找 "\r\n\r\n" 的首位置。逐字节扫即可（头部 ≤64KB，成本忽略）。
                for (int i = _start; i + 3 < _start + _len; i++)
                {
                    if (_buf[i] == '\r' && _buf[i + 1] == '\n' && _buf[i + 2] == '\r' && _buf[i + 3] == '\n')
                        return i - _start;
                }
                return -1;
            }

            private void Consume(int n)
            {
                _start += n;
                _len -= n;
                if (_len == 0) { _start = 0; }
            }
        }
    }
}
