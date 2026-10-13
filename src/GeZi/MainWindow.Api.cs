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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Core.Download;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi
{
    /// <summary>
    /// 本地 HTTP API 的窗口侧实现：路由分发 + 各端点。
    ///
    /// <para>设计要点：</para>
    /// <list type="bullet">
    /// <item>所有处理都经 <see cref="ApiHandleAsync"/> 切回 UI 线程执行 ——
    ///    _tasks（ObservableCollection 绑着 ListBox）、_client、Pending 记录
    ///    都不是线程安全的，必须在 UI 线程碰；顺带也天然串行化了并发请求。</item>
    /// <item>分享上下文不借用界面字段（_pwdId/_stoken/ShareModeRadio），
    ///    而是从请求参数构造 <see cref="ShareCtx"/> —— 这样 API 操作
    ///    不会把用户界面上正在浏览的分享"偷梁换柱"。</item>
    /// <item>取链/转存/建任务完全复用界面下载的同一条管线
    ///    （CollectFilesAsync → GetDownloadItemsAsync → BuildJobs → RunJobs），
    ///    界面有的保护（文件数上限、直链缓存、慢链重试、容量降级）API 全部继承。</item>
    /// </list>
    /// </summary>
    public partial class MainWindow
    {
        private LocalApiServer _api;

        /// <summary>
        /// 按设置启动本地 API。token 为空时自动生成随机值并随配置落盘 ——
        /// 调用方（本机脚本/AI）从 GeZi.config.xml 读出来就能用，无需人工配置。
        /// </summary>
        private void StartApiServer()
        {
            if (_settings == null || !_settings.ApiEnabled)
                return;

            if (string.IsNullOrEmpty(_settings.ApiToken))
            {
                _settings.ApiToken = Guid.NewGuid().ToString("N");
                try { SettingsStore.Save(_settings); } catch { }
            }

            try
            {
                _api = new LocalApiServer(_settings.ApiPort, _settings.ApiToken, ApiHandleAsync);
                _api.StartWithFallback();
                Log("本地 API 已启动: http://127.0.0.1:" + _api.Port
                    + "/api （仅供本机脚本调用，token 见配置文件）");
            }
            catch (Exception ex)
            {
                Log("本地 API 启动失败（端口段被占用？）: " + ex.Message);
            }
        }

        private void StopApiServer()
        {
            try { _api?.Dispose(); } catch { }
            _api = null;
        }

        /// <summary>设置页改了 API 开关/端口/token 后重启服务。</summary>
        private void RestartApiServer()
        {
            StopApiServer();
            StartApiServer();
        }

        /// <summary>
        /// 服务线程入口：把请求切回 UI 线程再分发。
        /// InvokeAsync(Func&lt;Task&lt;T&gt;&gt;) 得到 Task&lt;Task&lt;T&gt;&gt;，Unwrap 摊平。
        /// </summary>
        private Task<ApiResponse> ApiHandleAsync(ApiRequest req)
        {
            try
            {
                return Dispatcher.InvokeAsync(() => ApiDispatchAsync(req)).Task.Unwrap();
            }
            catch (Exception ex)
            {
                return Task.FromResult(ApiResponse.Error(500, ex.Message));
            }
        }

        private async Task<ApiResponse> ApiDispatchAsync(ApiRequest req)
        {
            try
            {
                switch (req.Path)
                {
                    case "/api/ping":
                        return ApiResponse.Ok(new Dictionary<string, object>
                        {
                            { "name", "GeZi" },
                            { "version", AppVersion() },
                            { "api", 1 },
                        });
                    case "/api/status":
                        return ApiStatus();
                    case "/api/resolve":
                        return await ApiResolveAsync(req);
                    case "/api/list":
                        return await ApiListAsync(req);
                    case "/api/download":
                        return await ApiDownloadAsync(req);
                    case "/api/links":
                        return await ApiLinksAsync(req);
                    case "/api/tasks":
                        return ApiTasks();
                    case "/api/tasks/control":
                        return ApiTaskControl(req);
                    case "/api/pending":
                        return ApiPending();
                    case "/api/pending/resume":
                        return await ApiResumePendingAsync(req);
                    case "/api/history":
                        return ApiHistory(req);
                    case "/api/log":
                        return ApiLog(req);
                    default:
                        return ApiResponse.Error(404, "unknown endpoint: " + req.Path);
                }
            }
            catch (QuarkApiException ex)
            {
                return ApiResponse.Error(502, "夸克接口错误: " + ex.Message);
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(500, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------- 端点实现 ----------------

        private static string AppVersion()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "" : v.ToString();
            }
            catch { return ""; }
        }

        /// <summary>API 需要一个可发请求的客户端；没有就用当前 Cookie 现建一个（游客态也能用分享接口）。</summary>
        private QuarkClient EnsureApiClient()
        {
            if (_client == null)
                _client = new QuarkClient(_settings.Cookie);
            return _client;
        }

        private ApiResponse ApiStatus()
        {
            var counts = new Dictionary<string, int>();
            foreach (JobState s in Enum.GetValues(typeof(JobState)))
                counts[s.ToString()] = 0;
            foreach (var t in _tasks)
            {
                var k = t.State.ToString();
                counts[k] = counts.TryGetValue(k, out var c) ? c + 1 : 1;
            }
            return ApiResponse.Ok(new Dictionary<string, object>
            {
                { "version", AppVersion() },
                { "apiPort", _api == null ? 0 : _api.Port },
                { "loggedIn", !string.IsNullOrEmpty(_settings.Cookie) },
                { "who", _settings.Who ?? "" },
                { "outDir", _settings.OutDir ?? "" },
                { "threads", _settings.Threads },
                { "concurrentTasks", _settings.ConcurrentTasks },
                { "tasks", counts },
                { "pending", HistoryStore.GetPending().Count },
            });
        }

        /// <summary>POST /api/resolve {url, passcode?, fid?} —— 解析分享链接，返回 pwdId/stoken 和目录内容。</summary>
        private async Task<ApiResponse> ApiResolveAsync(ApiRequest req)
        {
            string url = req.Str("url");
            if (string.IsNullOrWhiteSpace(url))
                return ApiResponse.Error(400, "missing url");

            var info = ShareUrlParser.Parse(url);
            if (info.IsKouling)
                return ApiResponse.Error(400, "这是夸克口令，不是分享链接（夸克没有公开口令转链接的接口）");
            if (string.IsNullOrEmpty(info.PwdId))
                return ApiResponse.Error(400, "无法从链接解析出分享 ID（pwd_id）");

            string passcode = req.Str("passcode");
            if (string.IsNullOrEmpty(passcode)) passcode = info.Passcode ?? "";

            var client = EnsureApiClient();
            string stoken = await client.GetStokenAsync(info.PwdId, passcode);
            if (string.IsNullOrEmpty(stoken))
                return ApiResponse.Error(502, "获取分享凭证失败（可能需要登录或提取码错误）");

            string fid = req.Str("fid");
            if (string.IsNullOrEmpty(fid)) fid = info.StartFid ?? "0";

            var entries = await client.ShareDetailAsync(info.PwdId, stoken, fid);
            var files = entries.Select(EntryToDict).ToList();

            Log("[API] 解析分享 " + info.PwdId + " → " + files.Count + " 项");
            return ApiResponse.Ok(new Dictionary<string, object>
            {
                { "pwdId", info.PwdId },
                { "stoken", stoken },
                { "passcode", passcode },
                { "fid", fid },
                { "files", files },
            });
        }

        /// <summary>POST /api/list {pwdId, stoken, fid} —— 列出分享内某个目录的子项。</summary>
        private async Task<ApiResponse> ApiListAsync(ApiRequest req)
        {
            string pwdId = req.Str("pwdId"), stoken = req.Str("stoken"), fid = req.Str("fid") ?? "0";
            if (string.IsNullOrEmpty(pwdId) || string.IsNullOrEmpty(stoken))
                return ApiResponse.Error(400, "missing pwdId or stoken（先调 /api/resolve 拿）");

            var entries = await EnsureApiClient().ShareDetailAsync(pwdId, stoken, fid);
            return ApiResponse.Ok(new Dictionary<string, object>
            {
                { "pwdId", pwdId },
                { "fid", fid },
                { "files", entries.Select(EntryToDict).ToList() },
            });
        }

        /// <summary>
        /// POST /api/download —— 把分享内容加入下载队列（走与界面「下载选中」完全相同的管线）。
        ///
        /// 参数：
        ///   url       分享链接（或直接整段分享文案）。与 pwdId 二选一。
        ///   pwdId     分享 ID（跳过 url 解析）；需配合 stoken，或由 passcode 换。
        ///   stoken    分享凭证（省略时用 pwdId+passcode 现取）。
        ///   passcode  提取码。
        ///   fids      要下载的文件/目录 fid 数组；缺省 = 从 rootFid 起整个展开。
        ///   fid       起始目录 fid（缺省 "0" = 分享根）。
        ///   dest      目标根目录；缺省用设置里的下载目录。
        ///   noSave    true = 免转存直接取链（不写自己网盘）；缺省走界面同款自动策略。
        ///   streamPlay true = 独立分片+边合并（边下边播）；缺省用全局设置。
        /// </summary>
        private async Task<ApiResponse> ApiDownloadAsync(ApiRequest req)
        {
            string url = req.Str("url");
            var info = !string.IsNullOrEmpty(url) ? ShareUrlParser.Parse(url) : null;

            string pwdId = req.Str("pwdId");
            if (string.IsNullOrEmpty(pwdId) && info != null)
                pwdId = info.PwdId;
            if (info != null && info.IsKouling)
                return ApiResponse.Error(400, "这是夸克口令，不是分享链接");
            if (string.IsNullOrEmpty(pwdId))
                return ApiResponse.Error(400, "missing url 或 pwdId");

            string passcode = req.Str("passcode");
            if (string.IsNullOrEmpty(passcode) && info != null)
                passcode = info.Passcode ?? "";
            passcode = passcode ?? "";

            var client = EnsureApiClient();
            string stoken = req.Str("stoken");
            if (string.IsNullOrEmpty(stoken))
            {
                stoken = await client.GetStokenAsync(pwdId, passcode);
                if (string.IsNullOrEmpty(stoken))
                    return ApiResponse.Error(502, "获取分享凭证失败（可能需要登录或提取码错误）");
            }

            var ctx = new ShareCtx
            {
                PwdId = pwdId, Stoken = stoken, Passcode = passcode, ShareMode = true,
            };

            string rootFid = req.Str("fid");
            if (string.IsNullOrEmpty(rootFid))
                rootFid = info != null && !string.IsNullOrEmpty(info.StartFid) ? info.StartFid : "0";

            // ---- 选定文件 ----
            var fids = req.StrList("fids");
            var skipped = new List<object>();
            List<ShareFileItem> selected;
            if (fids == null || fids.Count == 0)
            {
                var roots = await client.ShareDetailAsync(pwdId, stoken, rootFid);
                selected = roots.Select(r => new ShareFileItem
                {
                    Fid = r.Fid, Name = r.Name, Size = r.Size, IsDir = r.IsDir, Token = r.Token,
                }).ToList();
            }
            else
            {
                var map = await ApiWalkMapAsync(client, pwdId, stoken, rootFid);
                selected = new List<ShareFileItem>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var fid in fids)
                {
                    if (string.IsNullOrEmpty(fid) || !seen.Add(fid))
                        continue;
                    ShareFileItem it;
                    if (map.TryGetValue(fid, out it))
                        selected.Add(it);
                    else
                        skipped.Add(new Dictionary<string, object> { { "fid", fid }, { "reason", "分享中未找到该 fid" } });
                }
            }

            if (selected.Count == 0)
                return ApiResponse.Error(400, "没有可下载的文件（分享为空或 fid 全部未命中）");

            var files = await CollectFilesAsync(selected, "", 0, ctx);
            if (files.Count == 0)
                return ApiResponse.Error(400, "展开后没有文件");
            if (files.Count > MaxFiles)
                return ApiResponse.Error(400, string.Format(
                    "展开后共 {0} 个文件，超过单次上限 {1}；请用 fids 分批指定。", files.Count, MaxFiles));

            var items = await GetDownloadItemsAsync(files, ctx, req.Bool("noSave"));
            items = items.Where(it => !string.IsNullOrEmpty(it.Url)).ToList();
            if (items.Count == 0)
                return ApiResponse.Error(502, "没有取得任何直链（分享可能关闭直链/需登录/额度受限）");

            string destRoot = req.Str("dest");
            if (string.IsNullOrWhiteSpace(destRoot))
                destRoot = _settings.OutDir;
            if (string.IsNullOrWhiteSpace(destRoot))
                return ApiResponse.Error(400, "未指定 dest 且没有已保存的下载目录");

            WriteMode? wm = null;
            if (req.Bool("streamPlay")) wm = WriteMode.SegmentedFiles;

            var alloc = AllocateThreads(items.Select(x => x.Size).ToList());
            var jobs = BuildJobs(items, alloc, ctx, destRoot, wm);
            if (jobs.Count == 0)
                return ApiResponse.Error(500, "建任务失败（目标目录不可写？）");

            var queued = jobs.Select(j => (object)new Dictionary<string, object>
            {
                { "id", j.Id },
                { "name", j.Name },
                { "dest", j.Dest },
            }).ToList();
            foreach (var job in jobs)
                _tasks.Add(new TaskItem(job, job.Name));

            int skippedNoLink = files.Count - items.Count;
            for (int i = 0; i < skippedNoLink; i++)
                skipped.Add(new Dictionary<string, object> { { "reason", "未取得直链" } });

            Log(string.Format("[API] 已加入下载队列: {0} 个文件（{1}）", jobs.Count, pwdId));
            RunJobs(jobs);

            return ApiResponse.Ok(new Dictionary<string, object>
            {
                { "queued", queued },
                { "count", jobs.Count },
                { "skipped", skipped },
            });
        }

        /// <summary>
        /// POST /api/links —— 只取直链不下载（导出给 aria2/别的下载器用）。
        /// 参数同 /api/download（fids 缺省 = 整个分享展开）。
        /// </summary>
        private async Task<ApiResponse> ApiLinksAsync(ApiRequest req)
        {
            string url = req.Str("url");
            var info = !string.IsNullOrEmpty(url) ? ShareUrlParser.Parse(url) : null;

            string pwdId = req.Str("pwdId");
            if (string.IsNullOrEmpty(pwdId) && info != null)
                pwdId = info.PwdId;
            if (string.IsNullOrEmpty(pwdId))
                return ApiResponse.Error(400, "missing url 或 pwdId");

            string passcode = req.Str("passcode");
            if (string.IsNullOrEmpty(passcode) && info != null)
                passcode = info.Passcode ?? "";
            passcode = passcode ?? "";

            var client = EnsureApiClient();
            string stoken = req.Str("stoken");
            if (string.IsNullOrEmpty(stoken))
            {
                stoken = await client.GetStokenAsync(pwdId, passcode);
                if (string.IsNullOrEmpty(stoken))
                    return ApiResponse.Error(502, "获取分享凭证失败");
            }
            var ctx = new ShareCtx
            {
                PwdId = pwdId, Stoken = stoken, Passcode = passcode, ShareMode = true,
            };

            string rootFid = req.Str("fid");
            if (string.IsNullOrEmpty(rootFid))
                rootFid = info != null && !string.IsNullOrEmpty(info.StartFid) ? info.StartFid : "0";

            var fids = req.StrList("fids");
            List<ShareFileItem> selected;
            if (fids == null || fids.Count == 0)
            {
                var roots = await client.ShareDetailAsync(pwdId, stoken, rootFid);
                selected = roots.Select(r => new ShareFileItem
                {
                    Fid = r.Fid, Name = r.Name, Size = r.Size, IsDir = r.IsDir, Token = r.Token,
                }).ToList();
            }
            else
            {
                var map = await ApiWalkMapAsync(client, pwdId, stoken, rootFid);
                selected = new List<ShareFileItem>();
                foreach (var fid in fids)
                {
                    ShareFileItem it;
                    if (map.TryGetValue(fid, out it)) selected.Add(it);
                }
            }

            var files = await CollectFilesAsync(selected, "", 0, ctx);
            if (files.Count == 0)
                return ApiResponse.Error(400, "没有文件");
            if (files.Count > MaxFiles)
                return ApiResponse.Error(400, "超过单次上限 " + MaxFiles);

            var targets = files.Select(f => new LinkTarget { Fid = f.Fid, Token = f.Token, Name = f.Name }).ToList();
            var urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: true, ctx);

            var links = new List<object>();
            var missed = new List<object>();
            foreach (var f in files)
            {
                string u;
                if (urlOf.TryGetValue(f.Fid, out u) && !string.IsNullOrEmpty(u))
                {
                    links.Add(new Dictionary<string, object>
                    {
                        { "fid", f.Fid },
                        { "name", (f.RelDir ?? "").Replace('\\', '/') + f.Name },
                        { "size", f.Size },
                        { "url", u },
                    });
                }
                else
                {
                    missed.Add(new Dictionary<string, object> { { "fid", f.Fid }, { "name", f.Name } });
                }
            }
            Log(string.Format("[API] 导出直链 {0} 条（{1}）", links.Count, pwdId));
            return ApiResponse.Ok(new Dictionary<string, object>
            {
                { "links", links },
                { "count", links.Count },
                { "missed", missed },
            });
        }

        /// <summary>
        /// 分享内按 fid 索引：从 rootFid 广度优先走树，建 fid → 条目 的映射。
        /// 有界遍历（最多 500 个目录），避免恶意/巨型分享把接口打爆。
        /// </summary>
        private async Task<Dictionary<string, ShareFileItem>> ApiWalkMapAsync(
            QuarkClient client, string pwdId, string stoken, string rootFid)
        {
            const int MaxDirs = 500;
            var map = new Dictionary<string, ShareFileItem>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(string.IsNullOrEmpty(rootFid) ? "0" : rootFid);
            int dirs = 0;
            while (queue.Count > 0 && dirs < MaxDirs)
            {
                dirs++;
                string fid = queue.Dequeue();
                List<QuarkFileEntry> kids;
                try { kids = await client.ShareDetailAsync(pwdId, stoken, fid); }
                catch { continue; }
                foreach (var k in kids)
                {
                    if (string.IsNullOrEmpty(k.Fid) || map.ContainsKey(k.Fid))
                        continue;
                    map[k.Fid] = new ShareFileItem
                    {
                        Fid = k.Fid, Name = k.Name, Size = k.Size, IsDir = k.IsDir, Token = k.Token,
                    };
                    if (k.IsDir)
                        queue.Enqueue(k.Fid);
                }
            }
            return map;
        }

        private ApiResponse ApiTasks()
        {
            var list = _tasks.Select(t => (object)new Dictionary<string, object>
            {
                { "id", t.Job.Id },
                { "name", t.Name },
                { "dest", t.Job.Dest ?? "" },
                { "size", t.Total },
                { "done", t.Done },
                { "percent", Math.Round(t.Percent, 2) },
                { "speed", Math.Round(t.Speed, 0) },
                { "state", t.State.ToString() },
                { "statusText", t.StatusText },
                { "error", t.Error ?? "" },
            }).ToList();
            return ApiResponse.Ok(new Dictionary<string, object> { { "tasks", list }, { "count", list.Count } });
        }

        /// <summary>POST /api/tasks/control {id, action} —— action: pause / resume / cancel。</summary>
        private ApiResponse ApiTaskControl(ApiRequest req)
        {
            string id = req.Str("id"), action = (req.Str("action") ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(action))
                return ApiResponse.Error(400, "missing id 或 action（pause|resume|cancel）");

            var t = _tasks.FirstOrDefault(x => x.Job != null && x.Job.Id == id);
            if (t == null)
                return ApiResponse.Error(404, "任务不存在: " + id);

            switch (action)
            {
                case "pause": PauseTask(t); break;
                case "resume": ResumeTask(t); break;
                case "cancel": CancelTask(t); break;
                default: return ApiResponse.Error(400, "未知 action: " + action);
            }
            return ApiResponse.Ok(new Dictionary<string, object>
            {
                { "id", id },
                { "action", action },
                { "state", t.State.ToString() },
            });
        }

        private ApiResponse ApiPending()
        {
            var list = HistoryStore.GetPending().Select(p => (object)new Dictionary<string, object>
            {
                { "name", p.Name ?? "" },
                { "dest", p.Dest ?? "" },
                { "size", p.Size },
                { "fromShare", p.FromShare },
                { "canResume", p.CanResume },
                { "status", p.Status ?? "" },
                { "relDir", p.RelDir ?? "" },
            }).ToList();
            return ApiResponse.Ok(new Dictionary<string, object> { { "pending", list }, { "count", list.Count } });
        }

        /// <summary>POST /api/pending/resume {dests?:[]} —— 续传未完成任务；缺省全部可续传的。</summary>
        private async Task<ApiResponse> ApiResumePendingAsync(ApiRequest req)
        {
            var pending = HistoryStore.GetPending();
            var dests = req.StrList("dests");
            List<PendingTask> todo;
            if (dests == null || dests.Count == 0)
            {
                todo = pending.Where(p => p.CanResume).ToList();
            }
            else
            {
                var want = new HashSet<string>(dests, StringComparer.OrdinalIgnoreCase);
                todo = pending.Where(p => p.CanResume && p.Dest != null && want.Contains(p.Dest)).ToList();
            }
            if (todo.Count == 0)
                return ApiResponse.Error(400, "没有可续传的任务");

            // 没有登录态时续传必然失败；先探测再进，免得走 UI 弹窗路径打断 API 调用方。
            var client = await EnsureClientForResumeAsync();
            if (client == null)
                return ApiResponse.Error(400, "续传需要登录态：请先扫码登录");

            _ = ResumePendingBatchAsync(todo);
            return ApiResponse.Ok(new Dictionary<string, object> { { "resuming", todo.Count } });
        }

        private ApiResponse ApiHistory(ApiRequest req)
        {
            int limit = (int)req.Long("limit", 50);
            if (limit <= 0) limit = 50;
            var all = HistoryStore.GetHistory();
            var list = all.Skip(Math.Max(0, all.Count - limit))
                .Select(r => (object)new Dictionary<string, object>
                {
                    { "name", r.Name ?? "" },
                    { "size", r.Size ?? "" },
                    { "path", r.Path ?? "" },
                    { "status", r.Status ?? "" },
                    { "time", r.Time ?? "" },
                }).ToList();
            return ApiResponse.Ok(new Dictionary<string, object> { { "records", list }, { "count", list.Count } });
        }

        private ApiResponse ApiLog(ApiRequest req)
        {
            int tail = (int)req.Long("tail", 100);
            if (tail <= 0) tail = 100;
            var lines = _logTail.Skip(Math.Max(0, _logTail.Count - tail)).Cast<object>().ToList();
            return ApiResponse.Ok(new Dictionary<string, object> { { "lines", lines } });
        }

        private static Dictionary<string, object> EntryToDict(QuarkFileEntry e)
            => new Dictionary<string, object>
            {
                { "fid", e.Fid ?? "" },
                { "name", e.Name ?? "" },
                { "size", e.Size },
                { "isDir", e.IsDir },
                { "token", e.Token ?? "" },
            };
    }
}
