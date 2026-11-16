// VrcaCore.cs
// VRChat VRCA 上传核心逻辑 (C# 5 / .NET Framework 4.x)
// 被 CLI (VrcaCli.cs) 和 GUI (VrcaGui.cs) 共享。
//
// 所有日志通过构造函数传入的回调输出, 不直接依赖 Console, 这样 GUI 也能用。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VrcaUploader
{
    public partial class VrcaClient
    {
        // ---- 与官方 SDK 对齐的常量 ----
        static readonly string ApiBaseUrl = BuildApiBaseUrl();
        static readonly string ApiUserAgent = "VRC.Core." + "BestHTTP";
        static readonly string UploadUserAgent = "Vrca" + "Uploader/1.0";
        static readonly string SdkVersion = "3." + "10." + "4";
        const string DefaultUnityVersion = "2022.3.22f1";
        const string Platform = "standalonewindows";
        const int DefaultMultipartPartSize = 32 * 1024 * 1024;
        const int DefaultParallelMultipartParts = 4;
        const int DefaultGlobalMultipartParts = 8;
        const int UploadBufferBytes = 4 * 1024 * 1024;
        const int MaxUploadRetryAttempts = 8;
        const string MultipartPartSizeMbEnv = "HOTUPLOAD_MULTIPART_MB";
        const string ParallelMultipartEnv = "HOTUPLOAD_PARALLEL_PARTS";
        const string GlobalParallelMultipartEnv = "HOTUPLOAD_GLOBAL_PARALLEL_PARTS";
        const string HotswapCompressionEnv = "HOTUPLOAD_HOTSWAP_COMPRESSION";
        const string HotswapTimeoutEnv = "HOTUPLOAD_HOTSWAP_TIMEOUT_MINUTES";
        const string HotswapStallEnv = "HOTUPLOAD_HOTSWAP_STALL_MINUTES";
        const string HotswapMaxProcessesEnv = "HOTUPLOAD_MAX_HOTSWAP";
        const string WorldPreflightEnv = "HOTUPLOAD_WORLD_PREFLIGHT";
        const string DebugLogEnv = "HOTUPLOAD_DEBUG_LOG";
        const string DebugLogMarkerFile = "hotupload.debuglogs";
        const string TempCleanupAgeHoursEnv = "HOTUPLOAD_TEMP_CLEANUP_HOURS";
        const string RuntimeCleanupDaysEnv = "HOTUPLOAD_RUNTIME_CLEANUP_DAYS";
        const string PreparedCacheMaxGbEnv = "HOTUPLOAD_PREPARED_CACHE_GB";
        const string AuthDirOverrideEnv = "HOTUPLOAD_AUTH_DIR";
        const int DefaultFastHotswapCompression = 3; // AssetsTools.NET: LZ4Fast

        readonly Action<string> _log;
        readonly object _logFileLock = new object();
        readonly object _authCacheLock = new object();
        readonly string _runtimeDir;
        readonly string _authDir;
        readonly bool _isolatedAuthSession;
        readonly string _toolsDir;
        string _logFilePath = "";
        string _activeUnityVersion = DefaultUnityVersion;
        bool _traceEnabled;
        readonly bool _debugLogEnabled;
        readonly object _cancelLock = new object();
        readonly List<HttpWebRequest> _activeRequests = new List<HttpWebRequest>();
        readonly List<Process> _activeProcesses = new List<Process>();
        volatile bool _cancelRequested;
        static int _logSequence;
        static long _lastTempCleanupUtcTicks;
        static long _lastRuntimeCleanupUtcTicks;
        static long _lastPreparedCacheQuotaUtcTicks;
        static readonly object _activeTempArtifactsLock = new object();
        static readonly HashSet<string> _activeTempArtifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly object _hotswapGate = new object();
        static int _hotswapRunning;
        static readonly object _multipartGate = new object();
        static int _multipartRunning;
        static readonly object _sourceHashCacheLock = new object();
        static readonly Dictionary<string, SourceHashCacheEntry> _sourceHashCache =
            new Dictionary<string, SourceHashCacheEntry>(StringComparer.OrdinalIgnoreCase);

        sealed class SourceHashCacheEntry
        {
            public long Length;
            public long LastWriteUtcTicks;
            public string Sha256;
        }

        // 登录会话期间的 cookie (LoginStart -> LoginVerify2FA 之间保持)
        Dictionary<string, string> _loginCookies;

        public VrcaClient(string baseDir, Action<string> logger)
        {
            _log = logger ?? delegate { };
            _debugLogEnabled = IsDebugLogEnabled(baseDir);
            _traceEnabled = _debugLogEnabled;
            _runtimeDir = Path.Combine(baseDir, "_runtime");
            _isolatedAuthSession = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AuthDirOverrideEnv));
            _authDir = ResolvePersistentAuthDir(_runtimeDir);
            _toolsDir = ResolveToolsDir(baseDir);
            Directory.CreateDirectory(_runtimeDir);
            Directory.CreateDirectory(_authDir);
            Directory.CreateDirectory(Path.Combine(_runtimeDir, "logs"));
            CleanupOldUploadArtifactsOnce();
            CleanupRuntimeCacheOnce(false);
            EnforcePreparedWorldCacheQuota("");
        }

        public string RuntimeDir { get { return _runtimeDir; } }
        public string LogFilePath { get { return _logFilePath; } }
        public bool IsCancellationRequested { get { return _cancelRequested; } }

        static string BuildApiBaseUrl()
        {
            var host = string.Join(".", new[] { "api", "vrchat", "cloud" });
            return "https://" + host + "/api/1/";
        }

        public void Cancel()
        {
            List<HttpWebRequest> requests;
            List<Process> processes;
            lock (_cancelLock)
            {
                _cancelRequested = true;
                requests = new List<HttpWebRequest>(_activeRequests);
                processes = new List<Process>(_activeProcesses);
            }

            foreach (var request in requests)
            {
                try { request.Abort(); } catch { }
            }

            foreach (var process in processes)
                SafeKill(process);

            Log("[CANCEL] 上传任务已请求停止。");
        }

        public string StartNewLog(string title)
        {
            var seq = Interlocked.Increment(ref _logSequence);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + seq.ToString("0000");
            _logFilePath = Path.Combine(_runtimeDir, "logs", "upload-" + stamp + ".log");
            try { File.WriteAllText(_logFilePath, "", Encoding.UTF8); } catch { }
            Log("[RUN] " + title + " @ " + DateTime.Now.ToString("u"));
            Log("[LOG] " + _logFilePath);
            return _logFilePath;
        }

        static string ResolveToolsDir(string baseDir)
        {
            var candidates = new string[]
            {
                Path.Combine(baseDir, "tools"),
                Path.Combine(baseDir, "..", "tools"),
                Path.Combine(baseDir, "..", "..", "tools")
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(Path.Combine(full, "HotswapTool.exe")))
                    return full;
            }
            return Path.GetFullPath(Path.Combine(baseDir, "tools"));
        }

        static string ResolvePersistentAuthDir(string fallbackDir)
        {
            try
            {
                var overridePath = Environment.GetEnvironmentVariable(AuthDirOverrideEnv);
                if (!string.IsNullOrWhiteSpace(overridePath))
                    return Path.GetFullPath(overridePath.Trim());
            }
            catch { }
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                    return Path.Combine(local, "Mianchuan.VrcHotUpload");
            }
            catch { }
            return fallbackDir;
        }

        // =====================================================================
        //  登录 (两步: 密码 -> 可能需要 2FA)
        // =====================================================================
        public LoginResult LoginStart(string username, string password)
        {
            _loginCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Log("[LOGIN] 正在认证...");
            var userJson = LoginWithPassword(username, password, _loginCookies);

            var requiredType = GetRequiredTwoFactorType(userJson);
            if (!string.IsNullOrEmpty(requiredType))
            {
                return new LoginResult { Needs2FA = true, TwoFactorType = requiredType };
            }

            return FinishLogin(username, userJson);
        }

        public LoginResult LoginVerify2FA(string username, string code, string type)
        {
            if (_loginCookies == null)
                return new LoginResult { Error = "请先调用 LoginStart。" };

            VerifyTwoFactor(code, type, _loginCookies);

            var recheck = SendApi("GET", "auth/user", _loginCookies, null, null);
            EnsureSuccess(recheck, "auth/user");
            var userJson = ParseJsonObject(recheck.Text);
            return FinishLogin(username, userJson);
        }

        LoginResult FinishLogin(string username, Dictionary<string, object> userJson)
        {
            if (!_loginCookies.ContainsKey("auth"))
                return new LoginResult { Error = "登录后未取得 auth cookie。" };

            var display = GetJsonString(userJson, "displayName");
            var userId = GetJsonString(userJson, "id");

            SaveAuthCache(new AuthCache
            {
                Username = username,
                DisplayName = display,
                UserId = userId,
                Auth = _loginCookies.ContainsKey("auth") ? _loginCookies["auth"] : "",
                TwoFactorAuth = _loginCookies.ContainsKey("twoFactorAuth") ? _loginCookies["twoFactorAuth"] : "",
                DeviceId = GetOrCreateDeviceId(),
                SavedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            });

            Log("[OK] 登录成功: " + display + " (" + userId + ")");
            return new LoginResult { Success = true, DisplayName = display, UserId = userId };
        }

        // 返回当前登录显示名; 未登录/过期返回 null
        public string WhoAmI()
        {
            var cache = LoadAuthCache();
            if (cache == null || string.IsNullOrEmpty(cache.Auth)) return null;
            var cookies = BuildCookiesFromCache(cache);
            var resp = SendApi("GET", "auth/user", cookies, null, null);
            if (resp.StatusCode == 401 || resp.StatusCode == 403) return null;
            EnsureSuccess(resp, "auth/user");
            var user = ParseJsonObject(resp.Text);
            return GetJsonString(user, "displayName") + " (" + GetJsonString(user, "id") + ")";
        }

        public bool HasCachedLogin()
        {
            var cache = LoadAuthCache();
            return cache != null && !string.IsNullOrEmpty(cache.Auth);
        }

        public AvatarInspectResult InspectAvatar(string avatarId)
        {
            if (string.IsNullOrEmpty(avatarId))
                throw new InvalidOperationException("需要 avatar id。");

            var cache = LoadAuthCache();
            if (cache == null || string.IsNullOrEmpty(cache.Auth))
                throw new NotLoggedInException();

            var cookies = BuildCookiesFromCache(cache);
            var response = SendApi("GET", "avatars/" + Uri.EscapeDataString(avatarId), cookies, null, null);
            if (response.StatusCode == 401 || response.StatusCode == 403)
                throw new NotLoggedInException();
            EnsureSuccess(response, "avatars/" + avatarId);

            var avatar = ParseJsonObject(response.Text);
            var result = new AvatarInspectResult();
            result.Id = GetJsonString(avatar, "id");
            result.Name = GetJsonString(avatar, "name");
            result.AuthorId = GetJsonString(avatar, "authorId");
            result.ReleaseStatus = GetJsonString(avatar, "releaseStatus");
            result.ImageUrl = GetJsonString(avatar, "imageUrl");
            result.UnityPackageUrl = GetJsonString(avatar, "unityPackageUrl");
            result.ActiveAssetReviewId = GetJsonString(avatar, "activeAssetReviewId");
            result.PendingUpload = GetJsonBoolString(avatar, "pendingUpload");
            result.Version = GetJsonInt(avatar, "version", -1);
            result.UnityPackages = new List<UnityPackageInspect>();

            var packages = GetJsonList(avatar, "unityPackages");
            if (packages != null)
            {
                foreach (var item in packages)
                {
                    var pkg = AsDictionary(item);
                    var info = new UnityPackageInspect();
                    info.Id = GetJsonString(pkg, "id");
                    info.AssetUrl = GetJsonString(pkg, "assetUrl");
                    info.UnityVersion = GetJsonString(pkg, "unityVersion");
                    info.Platform = GetJsonString(pkg, "platform");
                    info.Variant = GetJsonString(pkg, "variant");
                    info.AssetVersion = GetJsonInt(pkg, "assetVersion", -1);
                    info.FileId = ExtractFileId(info.AssetUrl);
                    result.UnityPackages.Add(info);
                }
            }

            result.FileStatuses = new List<FileInspect>();
            foreach (var pkg in result.UnityPackages)
                if (!string.IsNullOrEmpty(pkg.FileId))
                    result.FileStatuses.Add(InspectFile(pkg.FileId, cookies));

            var imageFileId = ExtractFileId(result.ImageUrl);
            if (!string.IsNullOrEmpty(imageFileId))
                result.FileStatuses.Add(InspectFile(imageFileId, cookies));

            return result;
        }

        public string DownloadAvatarBundle(string avatarId, string targetPath)
        {
            if (string.IsNullOrEmpty(targetPath))
                throw new InvalidOperationException("需要输出路径。");

            var cache = LoadAuthCache();
            if (cache == null || string.IsNullOrEmpty(cache.Auth))
                throw new NotLoggedInException();
            var cookies = BuildCookiesFromCache(cache);

            var inspect = InspectAvatar(avatarId);
            if (inspect.UnityPackages == null || inspect.UnityPackages.Count == 0)
                throw new InvalidOperationException("Avatar 没有 unityPackages。");

            var assetUrl = "";
            foreach (var pkg in inspect.UnityPackages)
            {
                if (string.IsNullOrEmpty(pkg.AssetUrl)) continue;
                assetUrl = pkg.AssetUrl;
                break;
            }
            if (string.IsNullOrEmpty(assetUrl))
                throw new InvalidOperationException("Avatar unityPackage 没有 assetUrl。");

            var fullPath = Path.GetFullPath(targetPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            Log("[DOWNLOAD] 下载 avatar bundle: " + avatarId);
            DownloadUrlToFile(assetUrl, fullPath, cookies);
            Log("[DOWNLOAD] 完成: " + fullPath + " (" + FormatByteCount(new FileInfo(fullPath).Length) + ")");
            return fullPath;
        }

        public List<VrcaFileEntry> ListVrcaFiles()
        {
            var cache = LoadAuthCache();
            if (cache == null || string.IsNullOrEmpty(cache.Auth))
                throw new NotLoggedInException();

            var cookies = BuildCookiesFromCache(cache);
            var result = new List<VrcaFileEntry>();
            var avatarMetadata = ListOwnAvatarDownloadMetadata(cookies);
            const int pageSize = 100;

            for (var offset = 0; ; offset += pageSize)
            {
                var response = SendApi("GET", "files?n=" + pageSize.ToString(CultureInfo.InvariantCulture) +
                    "&offset=" + offset.ToString(CultureInfo.InvariantCulture), cookies, null, null);
                if (response.StatusCode == 401 || response.StatusCode == 403)
                    throw new NotLoggedInException();
                EnsureSuccess(response, "files");

                var page = ParseJsonList(response.Text);
                if (page == null || page.Count == 0)
                    break;

                foreach (var item in page)
                {
                    var file = AsDictionary(item);
                    if (!string.Equals(GetJsonString(file, "extension"), ".vrca", StringComparison.OrdinalIgnoreCase))
                        continue;

                    Dictionary<string, object> latest;
                    try { latest = GetLatestVersionEntry(file); }
                    catch { continue; }

                    var descriptor = AsDictionary(GetJsonValue(latest, "file"));
                    var url = GetJsonString(descriptor, "url");
                    if (string.IsNullOrEmpty(url))
                        continue;

                    var entry = new VrcaFileEntry();
                    entry.Id = GetJsonString(file, "id");
                    entry.Name = GetJsonString(file, "name");
                    if (string.IsNullOrEmpty(entry.Name))
                        entry.Name = string.IsNullOrEmpty(entry.Id) ? "VRCA" : entry.Id;
                    entry.Extension = GetJsonString(file, "extension");
                    entry.Version = GetJsonInt(latest, "version", -1);
                    entry.CreatedAt = FirstNonEmpty(GetJsonString(latest, "created_at"), GetJsonString(latest, "createdAt"));
                    entry.UpdatedAt = FirstNonEmpty(GetJsonString(file, "updated_at"), GetJsonString(file, "updatedAt"));
                    entry.Url = url;
                    entry.SizeBytes = GetJsonLong(descriptor, "sizeInBytes", -1);

                    AvatarDownloadMetadata metadata;
                    if (!string.IsNullOrEmpty(entry.Id) && avatarMetadata.TryGetValue(entry.Id, out metadata))
                    {
                        entry.AvatarId = metadata.AvatarId;
                        entry.AvatarName = metadata.AvatarName;
                        entry.Description = metadata.Description;
                        entry.ReleaseStatus = metadata.ReleaseStatus;
                        entry.ImageUrl = metadata.ImageUrl;
                        entry.ThumbnailImageUrl = metadata.ThumbnailImageUrl;
                    }
                    result.Add(entry);
                }

                if (page.Count < pageSize)
                    break;
                Thread.Sleep(50);
            }

            result.Sort(delegate(VrcaFileEntry a, VrcaFileEntry b)
            {
                var left = FirstNonEmpty(b.CreatedAt, b.UpdatedAt);
                var right = FirstNonEmpty(a.CreatedAt, a.UpdatedAt);
                return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        Dictionary<string, AvatarDownloadMetadata> ListOwnAvatarDownloadMetadata(Dictionary<string, string> cookies)
        {
            var result = new Dictionary<string, AvatarDownloadMetadata>(StringComparer.OrdinalIgnoreCase);
            const int pageSize = 100;

            for (var offset = 0; ; offset += pageSize)
            {
                var endpoint = "avatars?user=me&releaseStatus=all&sort=updated&order=descending&n=" +
                    pageSize.ToString(CultureInfo.InvariantCulture) + "&offset=" +
                    offset.ToString(CultureInfo.InvariantCulture);
                var response = SendApi("GET", endpoint, cookies, null, null);
                if (response.StatusCode == 401 || response.StatusCode == 403)
                    throw new NotLoggedInException();
                if (!IsSuccess(response))
                {
                    Log("[DOWNLOAD] 无法读取模型封面元数据，继续显示文件列表: HTTP " + response.StatusCode);
                    break;
                }

                var page = ParseJsonList(response.Text);
                if (page == null || page.Count == 0)
                    break;

                foreach (var item in page)
                {
                    var avatar = AsDictionary(item);
                    var metadata = new AvatarDownloadMetadata();
                    metadata.AvatarId = GetJsonString(avatar, "id");
                    metadata.AvatarName = GetJsonString(avatar, "name");
                    metadata.Description = GetJsonString(avatar, "description");
                    metadata.ReleaseStatus = GetJsonString(avatar, "releaseStatus");
                    metadata.ImageUrl = GetJsonString(avatar, "imageUrl");
                    metadata.ThumbnailImageUrl = FirstNonEmpty(
                        GetJsonString(avatar, "thumbnailImageUrl"), metadata.ImageUrl);

                    var packages = GetJsonList(avatar, "unityPackages");
                    if (packages != null)
                    {
                        foreach (var packageItem in packages)
                        {
                            var package = AsDictionary(packageItem);
                            AddAvatarDownloadMetadata(result, ExtractFileId(GetJsonString(package, "assetUrl")), metadata);
                        }
                    }

                    AddAvatarDownloadMetadata(result, ExtractFileId(GetJsonString(avatar, "unityPackageUrl")), metadata);
                    AddAvatarDownloadMetadata(result, ExtractFileId(GetJsonString(avatar, "assetUrl")), metadata);
                }

                if (page.Count < pageSize)
                    break;
                Thread.Sleep(50);
            }

            return result;
        }

        static void AddAvatarDownloadMetadata(
            Dictionary<string, AvatarDownloadMetadata> map,
            string fileId,
            AvatarDownloadMetadata metadata)
        {
            if (map == null || metadata == null || string.IsNullOrEmpty(fileId)) return;
            // Avatars are requested newest-first. Keep the first association if an
            // old file is referenced by more than one record.
            if (!map.ContainsKey(fileId)) map[fileId] = metadata;
        }

        public string DownloadVrcaFile(VrcaFileEntry entry, string targetPath, Action<long, long> progress)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Url))
                throw new InvalidOperationException("未选择可下载的 VRCA 文件。");
            return DownloadVrcaUrl(entry.Url, targetPath, progress);
        }

        public byte[] DownloadVrcaImageBytes(VrcaFileEntry entry, bool preferFullSize)
        {
            if (entry == null) return null;

            var url = preferFullSize
                ? FirstNonEmpty(entry.ImageUrl, entry.ThumbnailImageUrl)
                : FirstNonEmpty(entry.ThumbnailImageUrl, entry.ImageUrl);
            if (string.IsNullOrEmpty(url)) return null;

            var cache = LoadAuthCache();
            if (cache == null || string.IsNullOrEmpty(cache.Auth))
                throw new NotLoggedInException();

            return DownloadUrlToBytes(url, BuildCookiesFromCache(cache), 32 * 1024 * 1024);
        }

        public string DownloadVrcaUrl(string url, string targetPath, Action<long, long> progress)
        {
            if (string.IsNullOrEmpty(url))
                throw new InvalidOperationException("下载地址为空。");
            if (string.IsNullOrEmpty(targetPath))
                throw new InvalidOperationException("需要选择保存位置。");

            var cache = LoadAuthCache();
            if (cache == null || string.IsNullOrEmpty(cache.Auth))
                throw new NotLoggedInException();
            var cookies = BuildCookiesFromCache(cache);

            var fullPath = Path.GetFullPath(targetPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            Log("[DOWNLOAD] 开始下载模型文件...");
            var partialPath = fullPath + ".part-" + Guid.NewGuid().ToString("N");
            try
            {
                DownloadUrlToFile(url, partialPath, cookies, progress);
                if (File.Exists(fullPath)) File.Delete(fullPath);
                File.Move(partialPath, fullPath);
            }
            finally
            {
                try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            }
            Log("[DOWNLOAD] 下载完成: " + fullPath + " (" + FormatByteCount(new FileInfo(fullPath).Length) + ")");
            return fullPath;
        }

        void DownloadUrlToFile(string url, string targetPath, Dictionary<string, string> cookies)
        {
            DownloadUrlToFile(url, targetPath, cookies, null);
        }

        void DownloadUrlToFile(string url, string targetPath, Dictionary<string, string> cookies, Action<long, long> progress)
        {
            ConfigureNetwork();
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 60 * 60 * 1000;
            request.ReadWriteTimeout = 60 * 60 * 1000;
            ApplyUploadHeaders(request, cookies);

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = File.Open(targetPath, FileMode.Create, FileAccess.Write))
            {
                if (input == null) return;
                var total = response.ContentLength > 0 ? response.ContentLength : -1;
                long written = 0;
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    written += read;
                    if (progress != null) progress(written, total);
                }
                if (progress != null) progress(written, total);
            }
        }

        byte[] DownloadUrlToBytes(string url, Dictionary<string, string> cookies, int maxBytes)
        {
            ConfigureNetwork();
            var request = (HttpWebRequest)WebRequest.Create(url);
            RegisterActiveRequest(request);
            request.Method = "GET";
            request.Timeout = 120000;
            request.ReadWriteTimeout = 120000;
            ApplyUploadHeaders(request, cookies);

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var input = response.GetResponseStream())
                using (var output = new MemoryStream())
                {
                    if (input == null) return null;
                    if (response.ContentLength > maxBytes)
                        throw new InvalidOperationException("模型封面超过允许大小。");

                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > maxBytes)
                            throw new InvalidOperationException("模型封面超过允许大小。");
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null &&
                    ((int)response.StatusCode == 401 || (int)response.StatusCode == 403))
                    throw new NotLoggedInException();
                throw;
            }
            finally
            {
                UnregisterActiveRequest(request);
            }
        }

        public void Logout()
        {
            var path = AuthCachePath();
            if (File.Exists(path)) File.Delete(path);
            if (!_isolatedAuthSession)
            {
                path = LegacyAuthCachePath();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // =====================================================================
        //  上传 (返回 avatarId; 失败抛异常)
        // =====================================================================
        // =====================================================================
        //  世界上传
        // =====================================================================
        public WorldUploadResult UploadWorldDetailed(WorldUploadRequest req)
        {
            CheckCancelled();
            if (req == null)
                throw new ArgumentNullException("req");
            if (string.IsNullOrEmpty(req.VrcwPath) || !File.Exists(req.VrcwPath))
                throw new InvalidOperationException("需要有效的 .vrcw 文件路径。");
            var name = req.Name;
            var uploadPlatform = NormalizeWorldPlatform(req.Platform);
            var expectedTargetPlatform = uploadPlatform == "android" ? 13 : 19;
            var reuseImageUrl = string.IsNullOrWhiteSpace(req.ExistingImageUrl) ? "" : req.ExistingImageUrl.Trim();
            if (string.IsNullOrEmpty(reuseImageUrl) && (string.IsNullOrEmpty(req.ImagePath) || !File.Exists(req.ImagePath)))
                throw new InvalidOperationException("需要有效的封面图片。");
            var releaseStatus = req.ReleaseStatus;
            var description = req.Description;
            var updateExisting = !string.IsNullOrWhiteSpace(req.WorldId) && req.WorldId.StartsWith("wrld_");
            var suppliedWorldSignature = GetSuppliedWorldSignature(req);

            var cache = LoadAuthCache();
            if (cache == null || string.IsNullOrEmpty(cache.Auth))
                throw new NotLoggedInException();
            var cookies = BuildCookiesFromCache(cache);

            CheckCancelled();
            Log("[UPLOAD] 检查登录...");
            var check = SendApi("GET", "auth/user", cookies, null, null);
            if (check.StatusCode == 401 || check.StatusCode == 403)
                throw new NotLoggedInException();
            EnsureSuccess(check, "auth/user");

            CheckCancelled();

            

            CheckCancelled();
            WorldPreflightResult preflight = null;
            if (IsEnvironmentFlagEnabled(WorldPreflightEnv))
            {
                Log("[PREFLIGHT] 已启用世界完整预检查（技术排障模式）...");
                preflight = RunWorldPreflightTool(req.VrcwPath, "", false);
                if (preflight != null && preflight.TargetPlatform != 0 &&
                    preflight.TargetPlatform != expectedTargetPlatform)
                    throw new InvalidOperationException(
                        "VRCW 平台不匹配：任务要求 " + uploadPlatform +
                        "，文件 TargetPlatform=" + preflight.TargetPlatform + "。");
            }
            else
            {
                Log("[PREFLIGHT] 已跳过世界完整预检查；平台与结构将在本地改写阶段校验。");
            }

            var headerUnityVersion = ReadUnityFsEngineVersion(req.VrcwPath);
            _activeUnityVersion = LooksLikeUnityVersion(headerUnityVersion)
                ? headerUnityVersion
                : ResolveUnityVersionFromWorldPreflight(preflight, DefaultUnityVersion);
            Log("[UPLOAD] 使用 bundle UnityVersion: " + _activeUnityVersion + " platform=" + uploadPlatform);

            // 世界 id: 若调用方指定了已有 world id 则复用(更新该世界),否则生成全新 id(新建)。
            string newWorldId;
            IList existingTags = null;
            var worldCapacity = 32;
            var worldRecommendedCapacity = 16;
            if (updateExisting)
            {
                newWorldId = req.WorldId.Trim();
                Log("[UPLOAD] 更新已有 world id: " + newWorldId);

                var existingResponse = SendApi("GET", "worlds/" + Uri.EscapeDataString(newWorldId), cookies, null, null);
                EnsureSuccess(existingResponse, "worlds/" + newWorldId);
                var existingWorld = ParseJsonObject(existingResponse.Text);
                var currentUser = ParseJsonObject(check.Text);
                var authorId = GetJsonString(existingWorld, "authorId");
                var currentUserId = GetJsonString(currentUser, "id");
                if (!string.IsNullOrEmpty(authorId) &&
                    !string.IsNullOrEmpty(currentUserId) &&
                    !string.Equals(authorId, currentUserId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("目标世界不属于当前登录账号，已停止更新。");

                if (string.IsNullOrEmpty(name)) name = GetJsonString(existingWorld, "name");
                if (description == null) description = GetJsonString(existingWorld, "description");
                if (string.IsNullOrEmpty(releaseStatus)) releaseStatus = GetJsonString(existingWorld, "releaseStatus");
                worldCapacity = GetJsonInt(existingWorld, "capacity", worldCapacity);
                worldRecommendedCapacity = GetJsonInt(existingWorld, "recommendedCapacity", worldRecommendedCapacity);
                existingTags = GetJsonList(existingWorld, "tags");
                Log("[UPLOAD] 已核对现有世界元数据: name=" + name +
                    " capacity=" + worldCapacity +
                    " recommendedCapacity=" + worldRecommendedCapacity +
                    " release=" + releaseStatus);
            }
            else
            {
                newWorldId = "wrld_" + Guid.NewGuid().ToString("D");
                Log("[UPLOAD] 生成新 world id: " + newWorldId);
            }
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(req.VrcwPath);
            if (description == null) description = "";
            if (string.IsNullOrEmpty(releaseStatus)) releaseStatus = "private";

            var worldCompression = ResolveHotswapCompression(req.HotswapCompression);
            var inputAlreadyPrepared = preflight != null &&
                string.Equals(preflight.WorldId, newWorldId, StringComparison.OrdinalIgnoreCase);
            // A new upload gets a fresh wrld_ id every time, so an id-bound
            // prepared cache can never be reused by the next new-world task.
            string preparedCachePath = !updateExisting || inputAlreadyPrepared
                ? null
                : BuildPreparedWorldCachePath(req.VrcwPath, newWorldId, worldCompression, uploadPlatform);

            // 输出必须是 .vrcw 扩展名, 否则服务器 POST /worlds 会以 "Invalid file extension" 拒绝。
            var swapped = Path.Combine(Path.GetTempPath(), "vrca_" + Guid.NewGuid().ToString("N") + ".vrcw");
            RegisterActiveTempArtifact(swapped);
            RegisterActiveTempArtifact(swapped + ".raw");
            RegisterActiveTempArtifact(swapped + ".source-unpacked");

            var bundleFriendly = "World - " + name + " - Asset bundle - " + _activeUnityVersion + "_1_" + uploadPlatform + "_release";
            var imageFriendly = "World - " + name + " - Image - " + _activeUnityVersion + "_1_" + uploadPlatform + "_release";
            string signaturePath = "";
            string uploadBundlePath = swapped;
            string activePreparedCachePath = "";
            try
            {
                CheckCancelled();
                if (inputAlreadyPrepared)
                {
                    Log("[FAST] 输入包已烤入目标 world id，跳过解包和重打包。");
                    uploadBundlePath = req.VrcwPath;
                }
                else if (!string.IsNullOrEmpty(preparedCachePath) && File.Exists(preparedCachePath) && new FileInfo(preparedCachePath).Length > 1024 * 1024)
                {
                    Log("[FAST] 命中已准备世界包缓存，跳过解包和重打包: " + preparedCachePath);
                    uploadBundlePath = preparedCachePath;
                    activePreparedCachePath = preparedCachePath;
                    RegisterActiveTempArtifact(activePreparedCachePath);
                }
                else
                {
                    Log("[UPLOAD] 改写 bundle world id -> " + newWorldId);
                    RunHotswap(req.VrcwPath, swapped, newWorldId, worldCompression, true, expectedTargetPlatform);
                    if (!File.Exists(swapped))
                        throw new FileNotFoundException("改写后的 .vrcw 未生成。", swapped);
                    uploadBundlePath = swapped;
                    Log("[UPLOAD] 改写完成: " + (new FileInfo(swapped).Length / 1048576.0).ToString("F1") + " MB");

                    if (!string.IsNullOrEmpty(preparedCachePath))
                    {
                        var cacheTemp = preparedCachePath + ".building";
                        TryDeleteWithRetry(cacheTemp);
                        File.Copy(swapped, cacheTemp, true);
                        if (File.Exists(preparedCachePath)) TryDeleteWithRetry(preparedCachePath);
                        File.Move(cacheTemp, preparedCachePath);
                        EnforcePreparedWorldCacheQuota(preparedCachePath);
                        Log("[FAST] 已保存目标 world id 缓存，后续重试将跳过约 27 分钟重打包: " + preparedCachePath);
                    }
                }

                CheckCancelled();
                ValidatePreparedPayload(newWorldId, uploadBundlePath,
                    "world", uploadPlatform);

                CheckCancelled();
                Log(suppliedWorldSignature == null ? "[SIGN] 提取世界签名..." : "[SIGN] 使用本次更新提供的世界签名。");
                var worldSignature = suppliedWorldSignature ?? ExtractWorldSignature(uploadBundlePath);
                if (string.IsNullOrEmpty(worldSignature))
                    Log("[SIGN] 未找到内嵌签名；将不带 worldSignature 上传（与头像热上传同理，可能仍可通过）。");
                else
                    Log("[SIGN] 世界签名已准备，将随世界记录提交。");

                CheckCancelled();
                Log("[UPLOAD] 上传世界 bundle...");
                var bundleUrl = UploadFile(uploadBundlePath, bundleFriendly, cookies, out signaturePath);
                if (string.IsNullOrEmpty(bundleUrl))
                    throw new InvalidOperationException("bundle 上传未返回 URL。");
                TryDelete(signaturePath);
                UnregisterActiveTempArtifact(signaturePath);

                CheckCancelled();
                var imageUrl = reuseImageUrl;
                if (string.IsNullOrEmpty(imageUrl))
                {
                    Log("[UPLOAD] 上传封面图片...");
                    string imgSigPath;
                    imageUrl = UploadFile(req.ImagePath, imageFriendly, cookies, out imgSigPath);
                    if (string.IsNullOrEmpty(imageUrl))
                        throw new InvalidOperationException("封面上传未返回 URL。");
                    TryDelete(imgSigPath);
                    UnregisterActiveTempArtifact(imgSigPath);
                }
                else
                {
                    Log("[UPLOAD] 复用首个平台封面。");
                }

                CheckCancelled();
                // New worlds use POST /worlds. Existing worlds must use
                // PUT /worlds/{worldId}; posting an existing id returns 409.
                Log(updateExisting
                    ? "[UPLOAD] 更新世界记录 (PUT /worlds/" + newWorldId + ")..."
                    : "[UPLOAD] 创建世界记录 (POST /worlds)...");
                var body = new Dictionary<string, object>();
                if (!updateExisting)
                    body["id"] = newWorldId;
                body["name"] = name;
                body["description"] = description;
                body["assetUrl"] = bundleUrl;
                body["imageUrl"] = imageUrl;
                body["tags"] = existingTags ?? (object)new object[0];
                body["releaseStatus"] = releaseStatus;
                body["platform"] = uploadPlatform;
                body["unityVersion"] = _activeUnityVersion;
                body["assetVersion"] = 4;
                body["capacity"] = worldCapacity;
                body["recommendedCapacity"] = worldRecommendedCapacity;
                if (!string.IsNullOrEmpty(worldSignature))
                    body["worldSignature"] = worldSignature;

                var recordMethod = updateExisting ? "PUT" : "POST";
                var recordEndpoint = updateExisting
                    ? "worlds/" + Uri.EscapeDataString(newWorldId)
                    : "worlds";
                var recordResponse = SendApiLogged(recordMethod, recordEndpoint, cookies, body);
                EnsureSuccess(recordResponse, recordEndpoint);

                var result = new WorldUploadResult();
                result.WorldId = newWorldId;
                result.BundleFileId = ExtractFileId(bundleUrl);
                result.CreatedNew = !updateExisting;
                result.ImageUrl = imageUrl;
                result.Platform = uploadPlatform;

                if (!string.IsNullOrEmpty(result.BundleFileId))
                {
                    CheckCancelled();
                    Log("[SECURITY] 等待 VRChat 服务器处理...");
                    var security = WaitForWorldSecurityAnalysis(result.BundleFileId, cookies);
                    result.SecurityStatusCode = security != null ? security.SecurityAnalysisStatusCode : 0;
                    result.SecurityAnalysis = security != null ? (security.SecurityAnalysis ?? "") : "";
                    Log("[SECURITY] status=" + result.SecurityStatusCode + " analysis=" + result.SecurityAnalysis);
                }

                Log("[UPLOAD] WORLD_UPLOAD_OK|" + newWorldId);
                Log("[DONE] 世界上传完成。");
                return result;
            }
            finally
            {
                TryDeleteWithRetry(swapped);
                TryDeleteWithRetry(swapped + ".raw");
                TryDeleteWithRetry(swapped + ".source-unpacked");
                CleanupHotswapSidecars(swapped);
                UnregisterActiveTempArtifact(swapped);
                UnregisterActiveTempArtifact(swapped + ".raw");
                UnregisterActiveTempArtifact(swapped + ".source-unpacked");
                UnregisterActiveTempArtifact(activePreparedCachePath);
                TryDeleteWithRetry(signaturePath);
                UnregisterActiveTempArtifact(signaturePath);
                CleanupOldUploadArtifactsOnce();
                CleanupRuntimeCacheOnce(false);
            }
        }

        // Finish an update after the large bundle and image were uploaded but
        // the old executable incorrectly attempted POST /worlds and received
        // HTTP 409. This reuses those completed file records and performs only
        // the missing PUT /worlds/{worldId} request.
        public WorldUploadResult FinishWorldUpdate(WorldRecordUpdateRequest req)
        {
            CheckCancelled();
            if (req == null || string.IsNullOrWhiteSpace(req.WorldId) || !req.WorldId.StartsWith("wrld_"))
                throw new InvalidOperationException("A valid --world-id is required.");
            if (string.IsNullOrWhiteSpace(req.BundleFileId) || !req.BundleFileId.StartsWith("file_"))
                throw new InvalidOperationException("A valid --bundle-file-id is required.");
            if (string.IsNullOrWhiteSpace(req.ImageFileId) || !req.ImageFileId.StartsWith("file_"))
                throw new InvalidOperationException("A valid --image-file-id is required.");
            if (req.WorldSignature != null && string.IsNullOrWhiteSpace(req.WorldSignature))
                throw new ArgumentException("worldSignature 不能为空。", "req");

            var cache = LoadAuthCache();
            if (cache == null || string.IsNullOrEmpty(cache.Auth))
                throw new NotLoggedInException();
            var cookies = BuildCookiesFromCache(cache);

            var check = SendApi("GET", "auth/user", cookies, null, null);
            if (check.StatusCode == 401 || check.StatusCode == 403)
                throw new NotLoggedInException();
            EnsureSuccess(check, "auth/user");


            var worldId = req.WorldId.Trim();
            var bundleFileId = req.BundleFileId.Trim();
            var imageFileId = req.ImageFileId.Trim();
            var bundleInspect = SendApiLogged("GET", "file/" + Uri.EscapeDataString(bundleFileId), cookies, null);
            EnsureSuccess(bundleInspect, "file/" + bundleFileId);
            var imageInspect = SendApiLogged("GET", "file/" + Uri.EscapeDataString(imageFileId), cookies, null);
            EnsureSuccess(imageInspect, "file/" + imageFileId);

            var bundleUrl = ApiBaseUrl + "file/" + bundleFileId + "/1/file";
            var imageUrl = ApiBaseUrl + "file/" + imageFileId + "/1/file";
            var body = new Dictionary<string, object>();
            body["name"] = string.IsNullOrWhiteSpace(req.Name) ? "World" : req.Name;
            body["description"] = req.Description ?? "";
            body["assetUrl"] = bundleUrl;
            body["imageUrl"] = imageUrl;
            body["releaseStatus"] = string.IsNullOrWhiteSpace(req.ReleaseStatus) ? "private" : req.ReleaseStatus;
            body["platform"] = Platform;
            body["unityVersion"] = string.IsNullOrWhiteSpace(req.UnityVersion) ? DefaultUnityVersion : req.UnityVersion;
            body["assetVersion"] = 4;
            AddWorldUpdateSignature(body, req.WorldSignature);

            Log("[UPLOAD] 复用已上传文件并更新世界记录 (PUT /worlds/" + worldId + ")...");
            var update = SendApiLogged("PUT", "worlds/" + Uri.EscapeDataString(worldId), cookies, body);
            EnsureSuccess(update, "worlds/" + worldId);

            var result = new WorldUploadResult();
            result.WorldId = worldId;
            result.BundleFileId = bundleFileId;
            result.CreatedNew = false;
            var security = WaitForWorldSecurityAnalysis(bundleFileId, cookies);
            result.SecurityStatusCode = security != null ? security.SecurityAnalysisStatusCode : 0;
            result.SecurityAnalysis = security != null ? (security.SecurityAnalysis ?? "") : "";
            Log("[UPLOAD] WORLD_UPLOAD_OK|" + worldId);
            Log("[DONE] 已有世界更新完成（复用已上传文件）。");
            return result;
        }

        static string GetSuppliedWorldSignature(WorldUploadRequest req)
        {
            if (req.WorldSignature == null) return null;
            if (string.IsNullOrWhiteSpace(req.WorldSignature))
                throw new ArgumentException("worldSignature 不能为空。", "req");
            if (!System.Text.RegularExpressions.Regex.IsMatch(req.WorldId ?? "",
                "^wrld_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"))
                throw new ArgumentException("外部世界签名只能用于指定有效 World ID 的更新任务。", "req");
            return req.WorldSignature.Trim();
        }

        static void AddWorldUpdateSignature(Dictionary<string, object> body, string signature)
        {
            if (signature == null) return;
            if (string.IsNullOrWhiteSpace(signature))
                throw new ArgumentException("worldSignature 不能为空。", "signature");
            body["worldSignature"] = signature.Trim();
        }

        // 查询 world 是否已存在（属于当前用户）
        bool WorldExists(string worldId, Dictionary<string, string> cookies)
        {
            try
            {
                var resp = SendApi("GET", "worlds/" + Uri.EscapeDataString(worldId), cookies, null, null);
                return resp.StatusCode == 200;
            }
            catch
            {
                return false;
            }
        }

        // 发送 API 并在失败时记录响应体，便于诊断 400/401 等错误
        HttpResult SendApiLogged(string method, string endpoint, Dictionary<string, string> cookies, object body)
        {
            var resp = SendApi(method, endpoint, cookies, body, null);
            if (resp.StatusCode < 200 || resp.StatusCode >= 300)
            {
                var snippet = CompactText(resp.Text ?? "", 500);
                Log("[API_ERROR] " + method + " " + endpoint + " -> HTTP " + resp.StatusCode + " body=" + snippet);
            }
            return resp;
        }

        void ValidatePreparedPayload(string resourceId, string filePath,
            string resourceKind, string platform)
        {
            CheckCancelled();
            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                throw new InvalidOperationException("本地处理输出为空，已停止上传。");
            Log("[UPLOAD] 本地文件已就绪。");
        }

        FileInspect WaitForWorldSecurityAnalysis(string fileId, Dictionary<string, string> cookies)
        {
            FileInspect last = null;
            for (var attempt = 1; attempt <= 12; attempt++)
            {
                CheckCancelled();
                var resp = SendApi("GET", "file/" + Uri.EscapeDataString(fileId), cookies, null, null);
                if (!IsSuccess(resp)) break;
                var file = ParseJsonObject(resp.Text);
                var latest = GetLatestVersionEntry(file);
                var fd = AsDictionary(GetJsonValue(latest, "file"));
                var status = GetJsonString(fd, "status");
                Log("[SECURITY] check " + attempt + "/12: fileStatus=" + status);
                if (string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    last = new FileInspect();
                    last.Id = fileId;
                    last.FileStatus = status;
                    last.SecurityAnalysisStatusCode = 200;
                    last.SecurityAnalysis = "complete";
                    return last;
                }
                SleepCancelable(3000);
            }
            return last;
        }

        WorldPreflightResult RunWorldPreflightTool(string vrcwPath, string expectedWorldId, bool quick)
        {
            var result = new WorldPreflightResult();
            var reportDir = Path.Combine(_runtimeDir, "preflight-reports");
            Directory.CreateDirectory(reportDir);
            result.ReportPath = Path.Combine(reportDir, "world-preflight-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".txt");

            var exe = Path.Combine(_toolsDir, "WorldPreflightTool.exe");
            if (!File.Exists(exe))
            {
                result.ErrorCount++;
                return result;
            }

            var argList = new List<string>();
            argList.Add(Quote(vrcwPath));
            if (!string.IsNullOrEmpty(expectedWorldId))
            {
                argList.Add("--world-id");
                argList.Add(Quote(expectedWorldId));
            }
            if (quick) argList.Add("--quick");

            var lines = new List<string>();
            var psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = string.Join(" ", argList.ToArray());
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (var process = new Process())
            {
                process.StartInfo = psi;
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                    ParseWorldPreflightLine(result, e.Data);
                    Log("[world-preflight] " + e.Data);
                };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) Log("[world-preflight:err] " + e.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(10 * 60 * 1000))
                {
                    SafeKill(process);
                    result.ErrorCount++;
                }
                process.WaitForExit();
            }

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("VRCW 上传前预检查报告");
                sb.AppendLine("Time: " + DateTime.Now.ToString("u"));
                foreach (var l in lines) sb.AppendLine(l);
                File.WriteAllText(result.ReportPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }

            return result;
        }

        void ParseWorldPreflightLine(WorldPreflightResult result, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var parts = line.Split(new char[] { '|' }, 3);
            if (parts.Length < 1) return;
            if (parts[0] == "ERROR") result.ErrorCount++;
            else if (parts[0] == "BUILD_REQUIRED") result.BuildRequiredCount++;
            else if (parts[0] == "WARN") result.WarningCount++;
            else if (parts[0] == "INFO" && parts.Length >= 3)
            {
                if (parts[1] == "BUNDLE_HEADER") result.BundleHeaderLine = parts[2];
                else if (parts[1] == "WORLD_METADATA")
                {
                    var platformText = ExtractPreflightValue(parts[2], "TargetPlatform=");
                    int platform;
                    if (int.TryParse(platformText, NumberStyles.Integer, CultureInfo.InvariantCulture, out platform))
                        result.TargetPlatform = platform;
                }
                else if (parts[1] == "WORLD_IDS")
                {
                    var m = System.Text.RegularExpressions.Regex.Match(parts[2],
                        "wrld_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                    if (m.Success && string.IsNullOrEmpty(result.WorldId))
                        result.WorldId = m.Value;
                }
            }
        }

        string ResolveUnityVersionFromWorldPreflight(WorldPreflightResult result, string fallback)
        {
            if (result != null && !string.IsNullOrEmpty(result.BundleHeaderLine))
            {
                var version = ExtractPreflightValue(result.BundleHeaderLine, "EngineVersion=");
                if (LooksLikeUnityVersion(version)) return version;
            }
            return SafeUnityVersion(fallback);
        }

        string ReadUnityFsEngineVersion(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var signature = ReadNullTerminatedHeaderString(reader, 32);
                    if (!string.Equals(signature, "UnityFS", StringComparison.Ordinal) &&
                        !string.Equals(signature, "UnityRaw", StringComparison.Ordinal) &&
                        !string.Equals(signature, "UnityWeb", StringComparison.Ordinal))
                    {
                        Log("[HEADER] 未识别到 Unity bundle 文件头，UnityVersion 使用兼容默认值。");
                        return "";
                    }

                    if (stream.Length - stream.Position < 4)
                        return "";
                    reader.ReadBytes(4); // Unity bundle format version (big-endian).
                    ReadNullTerminatedHeaderString(reader, 128); // Minimum player version.
                    var engineVersion = ReadNullTerminatedHeaderString(reader, 128);
                    if (LooksLikeUnityVersion(engineVersion))
                    {
                        Log("[HEADER] 已从 UnityFS 文件头读取 UnityVersion: " + engineVersion);
                        return engineVersion;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[HEADER] 读取 UnityVersion 失败，使用兼容默认值: " + ex.GetType().Name);
            }
            return "";
        }

        static string ReadNullTerminatedHeaderString(BinaryReader reader, int maxBytes)
        {
            var bytes = new List<byte>();
            for (var i = 0; i < maxBytes; i++)
            {
                if (reader.BaseStream.Position >= reader.BaseStream.Length)
                    throw new EndOfStreamException("Unity bundle header ended unexpectedly.");
                var value = reader.ReadByte();
                if (value == 0)
                    return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(value);
            }
            throw new InvalidDataException("Unity bundle header string is too long.");
        }

        static bool IsEnvironmentFlagEnabled(string name)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim();
            return raw == "1" ||
                   raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   raw.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizeWorldPlatform(string value)
        {
            var platform = (value ?? "").Trim().ToLowerInvariant();
            if (platform == "android" || platform == "quest") return "android";
            if (platform == "" || platform == "pc" || platform == "windows" ||
                platform == "standalonewindows" || platform == "standalonewindows64")
                return "standalonewindows";
            throw new InvalidOperationException("不支持的世界平台：" + value);
        }

        string ExtractWorldSignature(string vrcwPath)
        {
            var exe = Path.Combine(_toolsDir, "WorldSignTool.exe");
            if (!File.Exists(exe))
            {
                Log("[SIGN] WorldSignTool.exe 不存在，跳过签名提取。");
                return "";
            }
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = Quote(vrcwPath);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.UTF8;

                string output = "";
                using (var process = new Process())
                {
                    process.StartInfo = psi;
                    process.Start();
                    output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(30000);
                }

                if (output.StartsWith("SIGNATURE|", StringComparison.OrdinalIgnoreCase))
                    return output.Substring("SIGNATURE|".Length).Trim();
                if (output.StartsWith("NO_FOOTER|", StringComparison.OrdinalIgnoreCase))
                    return "";
                Log("[SIGN] WorldSignTool 返回: " + output);
                return "";
            }
            catch (Exception ex)
            {
                Log("[SIGN] WorldSignTool 调用失败: " + ex.Message);
                return "";
            }
        }

        public string Upload(UploadRequest req)
        {
            return UploadDetailed(req).AvatarId;
        }

        public UploadResult UploadDetailed(UploadRequest req)
        {
            CheckCancelled();
            if (string.IsNullOrEmpty(req.VrcaPath) || !File.Exists(req.VrcaPath))
                throw new InvalidOperationException("需要有效的 .vrca 文件路径。");
            var name = string.IsNullOrEmpty(req.Name) ? Path.GetFileNameWithoutExtension(req.VrcaPath) : req.Name;
            if (string.IsNullOrEmpty(req.ImagePath) || !File.Exists(req.ImagePath))
                throw new InvalidOperationException("需要有效的封面图片 (VRChat 要求每个 avatar 都有封面)。");
            var releaseStatus = string.IsNullOrEmpty(req.ReleaseStatus) ? "private" : req.ReleaseStatus;
            var description = req.Description ?? "";

            var cache = LoadAuthCache();
            if (cache == null || string.IsNullOrEmpty(cache.Auth))
                throw new NotLoggedInException();
            var cookies = BuildCookiesFromCache(cache);

            CheckCancelled();
            Log("[UPLOAD] 检查登录...");
            var check = SendApi("GET", "auth/user", cookies, null, null);
            if (check.StatusCode == 401 || check.StatusCode == 403)
                throw new NotLoggedInException();
            EnsureSuccess(check, "auth/user");

            CheckCancelled();

            

            CheckCancelled();
            Log("[PREFLIGHT] 上传前快速安全兼容检查...");
            var sourcePreflight = RunPreflightTool(req.VrcaPath, "", true, true);
            if (IsPreflightBlocking(sourcePreflight))
                throw new InvalidOperationException(BuildPreflightStopMessage(sourcePreflight, "源文件未通过安全兼容预检查，已停止上传且未创建 avatar 记录。"));
            _activeUnityVersion = ResolveUnityVersionFromPreflight(sourcePreflight, DefaultUnityVersion);
            Log("[UPLOAD] 使用 bundle UnityVersion: " + _activeUnityVersion);

            CheckCancelled();
            Log("[UPLOAD] 创建 avatar 记录...");
            var avatar = CreateAvatarRecord(name, description, releaseStatus, cookies, _activeUnityVersion);
            var avatarId = GetJsonString(avatar, "id");
            if (string.IsNullOrEmpty(avatarId))
                throw new InvalidOperationException("avatar 响应未包含 id。");
            Log("[UPLOAD] 预留 avatar id: " + avatarId);

            var swapped = Path.Combine(Path.GetTempPath(), "vrca_" + Guid.NewGuid().ToString("N") + ".vrca");
            RegisterActiveTempArtifact(swapped);
            RegisterActiveTempArtifact(swapped + ".raw");
            RegisterActiveTempArtifact(swapped + ".source-unpacked");
            string signaturePath = "";
            string bundleFileId = "";
            string imageFileId = "";
            try
            {
                CheckCancelled();
                Log("[UPLOAD] 改写 bundle blueprintId -> " + avatarId);
                Log("[UPLOAD] Avatar 使用标准 LZMA 重打包。");
                RunHotswap(req.VrcaPath, swapped, avatarId, 1);
                if (!File.Exists(swapped))
                    throw new FileNotFoundException("改写后的 .vrca 未生成。", swapped);
                Log("[UPLOAD] 改写完成: " + (new FileInfo(swapped).Length / 1048576.0).ToString("F1") + " MB");

                CheckCancelled();
                Log("[PREFLIGHT] 上传前快速校验...");
                var preflight = RunPreflightTool(swapped, avatarId, true);
                if (IsPreflightBlocking(preflight, true))
                    throw new InvalidOperationException(BuildPreflightStopMessage(preflight, "上传前校验发现仍需处理的问题，已阻止上传。"));
                if (HasPreflightItem(preflight, "OLD_AVATAR_ID_REMAINS"))
                    Log("[PREFLIGHT] 旧 Avatar ID 仅存在于已验证的内部保留引用，继续上传。");

                ValidatePreparedPayload(avatarId, swapped, "avatar", Platform);

                var bundleFriendly = "Avatar - " + name + " - Asset bundle - " + _activeUnityVersion + "_1_" + Platform + "_release";
                var imageFriendly = "Avatar - " + name + " - Image - " + _activeUnityVersion + "_1_" + Platform + "_release";

                CheckCancelled();
                Log("[UPLOAD] 上传 avatar bundle...");
                var bundleUrl = UploadFile(swapped, bundleFriendly, cookies, out signaturePath);
                if (string.IsNullOrEmpty(bundleUrl))
                    throw new InvalidOperationException("bundle 上传未返回 URL。");
                TryDelete(signaturePath);
                UnregisterActiveTempArtifact(signaturePath);

                var result = new UploadResult();
                result.AvatarId = avatarId;
                bundleFileId = ExtractFileId(bundleUrl);
                result.BundleFileId = bundleFileId;
                if (string.IsNullOrEmpty(bundleFileId))
                    throw new InvalidOperationException("无法从 bundle URL 解析 fileId，已停止提交 avatar 记录。");

                CheckCancelled();
                Log("[UPLOAD] 上传封面图片...");
                var imageUrl = UploadFile(req.ImagePath, imageFriendly, cookies, out signaturePath);
                if (string.IsNullOrEmpty(imageUrl))
                    throw new InvalidOperationException("封面上传未返回 URL。");
                imageFileId = ExtractFileId(imageUrl);
                if (string.IsNullOrEmpty(imageFileId))
                    throw new InvalidOperationException("无法从封面 URL 解析 fileId，已停止提交 avatar 记录。");

                CheckCancelled();
                Log("[UPLOAD] 更新 avatar 元数据...");
                var updateBody = new Dictionary<string, object>();
                updateBody["name"] = name;
                updateBody["description"] = description;
                updateBody["assetUrl"] = bundleUrl;
                updateBody["imageUrl"] = imageUrl;
                updateBody["tags"] = new object[0];
                updateBody["releaseStatus"] = releaseStatus;
                updateBody["platform"] = Platform;
                updateBody["unityVersion"] = _activeUnityVersion;
                updateBody["assetVersion"] = 1;

                var update = SendApi("PUT", "avatars/" + Uri.EscapeDataString(avatarId), cookies, updateBody, null);
                EnsureSuccess(update, "avatars/" + avatarId);

                CheckCancelled();
                Log("[SECURITY] avatar 元数据已提交，等待 VRChat 服务器安全分析...");
                var security = WaitForServerSecurityAnalysis(bundleFileId, cookies);
                ApplySecurityResult(result, security);
                Log("[SECURITY] status=" + result.SecurityStatusCode +
                    " analysis=" + result.SecurityAnalysis +
                    " file=" + result.BundleFileId);

                if (IsServerSecurityForbidden(result))
                    throw new ServerSecurityException(result);
                if (!result.ServerSecurityChecked || result.SecurityStatusCode != 200)
                    throw new InvalidOperationException(
                        "服务器安全分析未返回明确成功，已回滚本次 avatar。" +
                        " status=" + result.SecurityStatusCode +
                        " analysis=" + (result.SecurityAnalysis ?? ""));

                Log("[UPLOAD] DIRECT_UPLOAD_OK|" + avatarId);
                Log("[DONE] 上传成功，服务器安全分析已返回可用结果。");
                return result;
            }
            catch
            {
                TryDeleteReservedAvatar(avatarId, cookies);
                TryDeleteUploadedFile(imageFileId, cookies);
                TryDeleteUploadedFile(bundleFileId, cookies);
                throw;
            }
            finally
            {
                TryDeleteWithRetry(swapped);
                TryDeleteWithRetry(swapped + ".raw");
                TryDeleteWithRetry(swapped + ".source-unpacked");
                CleanupHotswapSidecars(swapped);
                TryDeleteWithRetry(signaturePath);
                UnregisterActiveTempArtifact(swapped);
                UnregisterActiveTempArtifact(swapped + ".raw");
                UnregisterActiveTempArtifact(swapped + ".source-unpacked");
                UnregisterActiveTempArtifact(signaturePath);
                CleanupOldUploadArtifactsOnce();
                CleanupRuntimeCacheOnce(false);
            }
        }

        FileInspect WaitForServerSecurityAnalysis(string fileId, Dictionary<string, string> cookies)
        {
            FileInspect last = null;
            const int maxAttempts = 100;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                CheckCancelled();
                last = InspectFile(fileId, cookies);
                Log("[SECURITY] check " + attempt + "/" + maxAttempts + ": status=" + last.SecurityAnalysisStatusCode +
                    " analysis=" + last.SecurityAnalysis);

                if (last.SecurityAnalysisStatusCode == 200 || last.SecurityAnalysisStatusCode == 403)
                    return last;
                if (last.SecurityAnalysisStatusCode >= 400 &&
                    last.SecurityAnalysisStatusCode != 404 &&
                    last.SecurityAnalysisStatusCode != 429)
                    return last;

                SleepCancelable(3000);
            }
            return last;
        }

        void TryDeleteReservedAvatar(string avatarId, Dictionary<string, string> cookies)
        {
            if (string.IsNullOrEmpty(avatarId) || cookies == null) return;
            try
            {
                Log("[ROLLBACK] 删除未提交完成的 avatar 记录: " + avatarId);
                var response = SendApi("DELETE", "avatars/" + Uri.EscapeDataString(avatarId), cookies, null, null);
                if (IsSuccess(response) || response.StatusCode == 404)
                    Log("[ROLLBACK] avatar 记录已清理。");
                else
                    Log("[ROLLBACK] avatar 记录清理失败: HTTP " + response.StatusCode + " body=" + CompactText(response.Text ?? "", 300));
            }
            catch (Exception ex)
            {
                Log("[ROLLBACK] avatar 记录清理异常: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        void TryDeleteUploadedFile(string fileId, Dictionary<string, string> cookies)
        {
            if (string.IsNullOrEmpty(fileId) || cookies == null) return;
            try
            {
                Log("[ROLLBACK] 删除本次新建的文件记录: " + fileId);
                var response = SendApi("DELETE", "file/" + Uri.EscapeDataString(fileId), cookies, null, null);
                if (IsSuccess(response) || response.StatusCode == 404)
                    Log("[ROLLBACK] 文件记录已清理。");
                else
                    Log("[ROLLBACK] 文件记录清理失败: HTTP " + response.StatusCode + " body=" + CompactText(response.Text ?? "", 300));
            }
            catch (Exception ex)
            {
                Log("[ROLLBACK] 文件记录清理异常: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void ApplySecurityResult(UploadResult result, FileInspect security)
        {
            if (result == null || security == null) return;
            result.ServerSecurityChecked = security.SecurityAnalysisStatusCode > 0;
            result.SecurityStatusCode = security.SecurityAnalysisStatusCode;
            result.SecurityAnalysis = security.SecurityAnalysis ?? "";
            result.SecurityMessage = security.SecurityAnalysisMessage ?? "";
            result.BundleFileStatus = security.FileStatus ?? "";
            result.SignatureStatus = security.SignatureStatus ?? "";
        }

        static bool IsServerSecurityForbidden(UploadResult result)
        {
            return result != null &&
                (result.SecurityStatusCode == 403 ||
                 string.Equals(result.SecurityAnalysis, "forbidden", StringComparison.OrdinalIgnoreCase));
        }

        // =====================================================================
        //  认证内部
        // =====================================================================
        // =====================================================================
        //  上传前本地校验
        // =====================================================================
        public PreflightResult Preflight(string vrcaPath)
        {
            return RunPreflightTool(vrcaPath, "", false, true);
        }

        PreflightResult RunPreflightTool(string vrcaPath, string expectedAvatarId, bool quick, bool inputMode = false)
        {
            var result = new PreflightResult();
            result.Items = new List<PreflightItem>();

            var reportDir = Path.Combine(_runtimeDir, "preflight-reports");
            Directory.CreateDirectory(reportDir);
            result.ReportPath = Path.Combine(reportDir, "preflight-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) + ".txt");

            var exe = Path.Combine(_toolsDir, "PreflightTool.exe");
            if (!File.Exists(exe))
            {
                AddPreflightItem(result, "ERROR", "TOOL_MISSING", "找不到 PreflightTool.exe，请重新编译工具。");
                WritePreflightReport(result, new string[0]);
                return result;
            }

            var args = new List<string>();
            args.Add(Quote(vrcaPath));
            if (!string.IsNullOrEmpty(expectedAvatarId))
            {
                args.Add("--expected-avatar");
                args.Add(Quote(expectedAvatarId));
            }
            if (quick) args.Add("--quick");
            if (inputMode) args.Add("--input");

            var output = new List<string>();
            var psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = string.Join(" ", args.ToArray());
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (var process = new Process())
            {
                process.StartInfo = psi;
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (output) output.Add(e.Data);
                    ParsePreflightLine(result, e.Data);
                    Log("[preflight] " + e.Data);
                };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (output) output.Add("ERR|" + e.Data);
                    Log("[preflight:err] " + e.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(15 * 60 * 1000))
                {
                    SafeKill(process);
                    AddPreflightItem(result, "ERROR", "TIMEOUT", "上传前校验超时。");
                }
                process.WaitForExit();
                if (process.ExitCode != 0 && !IsPreflightBlocking(result))
                    AddPreflightItem(result, "ERROR", "EXIT_CODE", "PreflightTool 退出码 " + process.ExitCode);
            }

            WritePreflightReport(result, output.ToArray());
            Log("[PREFLIGHT] 结果: errors=" + result.ErrorCount +
                ", repairable=" + result.RepairableCount +
                ", buildRequired=" + result.BuildRequiredCount +
                ", warnings=" + result.WarningCount +
                ", report=" + result.ReportPath);
            return result;
        }

        bool IsPreflightBlocking(PreflightResult result)
        {
            return IsPreflightBlocking(result, false);
        }

        bool IsPreflightBlocking(PreflightResult result, bool allowPreservedAvatarReferences)
        {
            if (result == null) return true;
            if (result.ErrorCount > 0 || result.BuildRequiredCount > 0) return true;
            if (result.RepairableCount == 0) return false;
            if (!allowPreservedAvatarReferences) return true;

            foreach (var item in result.Items)
            {
                if (item == null || !string.Equals(item.Level, "REPAIRABLE", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(item.Code, "OLD_AVATAR_ID_REMAINS", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        bool HasPreflightItem(PreflightResult result, string code)
        {
            if (result == null || result.Items == null || string.IsNullOrEmpty(code)) return false;
            foreach (var item in result.Items)
                if (item != null && string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        string ResolveUnityVersionFromPreflight(PreflightResult result, string fallback)
        {
            if (result != null && result.Items != null)
            {
                foreach (var item in result.Items)
                {
                    if (item == null) continue;
                    if (!string.Equals(item.Code, "BUNDLE_HEADER", StringComparison.OrdinalIgnoreCase)) continue;
                    var version = ExtractPreflightValue(item.Message, "EngineVersion=");
                    if (LooksLikeUnityVersion(version))
                        return version;
                }
            }
            return SafeUnityVersion(fallback);
        }

        string ExtractPreflightValue(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return "";
            var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return "";
            index += key.Length;
            var end = index;
            while (end < text.Length && text[end] != ',' && text[end] != ';' && !char.IsWhiteSpace(text[end]))
                end++;
            return text.Substring(index, end - index).Trim();
        }

        bool LooksLikeUnityVersion(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOf('.') > 0 &&
                   value.IndexOf('f') > 0 &&
                   value.Length >= 8 &&
                   value.Length <= 32;
        }

        string SafeUnityVersion(string value)
        {
            return LooksLikeUnityVersion(value) ? value : DefaultUnityVersion;
        }

        string BuildPreflightStopMessage(PreflightResult result, string prefix)
        {
            var sb = new StringBuilder();
            sb.Append(prefix);
            if (result.BuildRequiredCount > 0)
                sb.Append(" 需要官方 SDK 完整构建或继续做构建流程复现实验。");
            if (result.RepairableCount > 0)
                sb.Append(" 存在可自动修复项，请先运行修复/改写后重新检查。");
            if (result.ErrorCount > 0)
                sb.Append(" 存在不可继续的错误。");
            if (result.Items != null)
                foreach (var item in result.Items)
                    if (item != null && item.Level == "ERROR" &&
                        (item.Code == "INPUT_SIZE_LIMIT" || item.Code == "DOWNLOAD_SIZE_LIMIT" ||
                         item.Code == "UNCOMPRESSED_SIZE_LIMIT" || item.Code == "OUTPUT_COMPRESSION"))
                        sb.Append(" ").Append(item.Message);
            sb.Append(" 报告: ").Append(result.ReportPath);
            return sb.ToString();
        }

        void ParsePreflightLine(PreflightResult result, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var parts = line.Split(new char[] { '|' }, 3);
            if (parts.Length < 3) return;
            if (parts[0] == "ERROR" || parts[0] == "REPAIRABLE" || parts[0] == "BUILD_REQUIRED" || parts[0] == "WARN" || parts[0] == "INFO")
                AddPreflightItem(result, parts[0], parts[1], parts[2]);
        }

        void AddPreflightItem(PreflightResult result, string level, string code, string message)
        {
            var item = new PreflightItem { Level = level, Code = code, Message = message };
            result.Items.Add(item);
            if (level == "ERROR") result.ErrorCount++;
            else if (level == "REPAIRABLE") result.RepairableCount++;
            else if (level == "BUILD_REQUIRED") result.BuildRequiredCount++;
            else if (level == "WARN") result.WarningCount++;
            else result.InfoCount++;
        }

        void WritePreflightReport(PreflightResult result, string[] rawLines)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("VRCA 上传前校验报告");
                sb.AppendLine("Time: " + DateTime.Now.ToString("u"));
                sb.AppendLine("Errors: " + result.ErrorCount);
                sb.AppendLine("Repairable: " + result.RepairableCount);
                sb.AppendLine("BuildRequired: " + result.BuildRequiredCount);
                sb.AppendLine("Warnings: " + result.WarningCount);
                sb.AppendLine("Infos: " + result.InfoCount);
                sb.AppendLine();
                foreach (var item in result.Items)
                    sb.AppendLine("[" + item.Level + "] " + item.Code + " - " + item.Message);
                sb.AppendLine();
                sb.AppendLine("Raw Output:");
                if (rawLines != null)
                    foreach (var line in rawLines) sb.AppendLine(line);
                File.WriteAllText(result.ReportPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        Dictionary<string, object> LoginWithPassword(string username, string password, Dictionary<string, string> cookies)
        {
            // HTTP Basic authentication encodes the raw credentials as UTF-8.
            // URL-escaping here changes passwords containing characters such as
            // '+', '@', '%', or '!' and causes repeated false authentication
            // failures until VRChat rate-limits the account/IP with HTTP 429.
            var raw = (username ?? "") + ":" + (password ?? "");
            var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            var response = SendApi("GET", "auth/user", cookies, null, basic);
            AddResponseCookies(response.Headers, cookies);
            if (response.StatusCode == 429)
                throw new ApiException(response.StatusCode, BuildRateLimitMessage(response), "auth/user");
            if (!IsSuccess(response) && LooksLikeTwoFactorRequired(response.Text))
                return ParseJsonObject(response.Text);
            EnsureSuccess(response, "auth/user");
            return ParseJsonObject(response.Text);
        }

        static string BuildRateLimitMessage(HttpResult response)
        {
            var retryAfter = "";
            try
            {
                if (response != null && response.Headers != null)
                    retryAfter = response.Headers["Retry-After"];
            }
            catch { }

            return string.IsNullOrWhiteSpace(retryAfter)
                ? "VRChat 暂时限制了登录请求(HTTP 429)，请等待几分钟后只重试一次。不要连续点击登录。"
                : "VRChat 暂时限制了登录请求(HTTP 429)，请等待 " + retryAfter.Trim() +
                  " 秒后只重试一次。不要连续点击登录。";
        }

        void VerifyTwoFactor(string code, string type, Dictionary<string, string> cookies)
        {
            var endpoint = string.Equals(type, "email", StringComparison.OrdinalIgnoreCase)
                ? "auth/twofactorauth/emailotp/verify"
                : "auth/twofactorauth/totp/verify";
            var body = new Dictionary<string, object>();
            body["code"] = code;
            Log("[LOGIN] 校验 2FA...");
            var response = SendApi("POST", endpoint, cookies, body, null);
            AddResponseCookies(response.Headers, cookies);
            EnsureSuccess(response, endpoint);
            Log("[LOGIN] 2FA 通过。");
        }

        Dictionary<string, object> CreateAvatarRecord(string name, string description, string releaseStatus, Dictionary<string, string> cookies, string unityVersion)
        {
            var body = new Dictionary<string, object>();
            body["name"] = name;
            body["description"] = description ?? "";
            body["tags"] = new object[0];
            body["releaseStatus"] = releaseStatus;
            body["platform"] = Platform;
            body["unityVersion"] = SafeUnityVersion(unityVersion);
            body["assetVersion"] = 1;
            var response = SendApi("POST", "avatars", cookies, body, null);
            EnsureSuccess(response, "avatars");
            return ParseJsonObject(response.Text);
        }

        // =====================================================================
        //  文件上传
        // =====================================================================
        string UploadFile(string filename, string friendlyFileName, Dictionary<string, string> cookies, out string signatureFilePath)
        {
            CheckCancelled();
            signatureFilePath = "";
            var extension = Path.GetExtension(filename);
            var mimeType = GetMimeType(extension);
            var createBody = new Dictionary<string, object>();
            createBody["name"] = friendlyFileName;
            createBody["mimeType"] = mimeType;
            createBody["extension"] = extension;

            Log("[FILE] 创建文件记录: " + friendlyFileName);
            var create = SendApi("POST", "file", cookies, createBody, null);
            EnsureSuccess(create, "file");
            var currentFile = ParseJsonObject(create.Text);
            var fileId = GetJsonString(currentFile, "id");
            if (string.IsNullOrEmpty(fileId))
                throw new InvalidOperationException("file 响应未包含 id。");

            var fileSize = new FileInfo(filename).Length;

            signatureFilePath = Path.Combine(Path.GetTempPath(), "vrca_sig_" + Guid.NewGuid().ToString("N") + ".sig");
            RegisterActiveTempArtifact(signatureFilePath);
            byte[] fileMd5Bytes;
            byte[] sigMd5Bytes;
            GenerateSignature(filename, signatureFilePath, out fileMd5Bytes, out sigMd5Bytes);
            CheckCancelled();
            var fileMd5 = Convert.ToBase64String(fileMd5Bytes);
            var sigMd5 = Convert.ToBase64String(sigMd5Bytes);
            var sigSize = new FileInfo(signatureFilePath).Length;

            var versionBody = new Dictionary<string, object>();
            versionBody["signatureMd5"] = sigMd5;
            versionBody["signatureSizeInBytes"] = sigSize;
            versionBody["fileMd5"] = fileMd5;
            versionBody["fileSizeInBytes"] = fileSize;

            var version = SendApi("POST", "file/" + Uri.EscapeDataString(fileId), cookies, versionBody, null);
            EnsureSuccess(version, "file/" + fileId);
            currentFile = ParseJsonObject(version.Text);

            var latest = GetLatestVersionEntry(currentFile);
            var versionNumber = GetLatestVersionNumber(currentFile);
            var fileDescriptor = AsDictionary(GetJsonValue(latest, "file"));
            var signatureDescriptor = AsDictionary(GetJsonValue(latest, "signature"));

            Log("[FILE] 上传文件数据...");
            CheckCancelled();
            UploadDescriptor(filename, "file", fileId, versionNumber, fileDescriptor, mimeType, fileMd5Bytes, cookies);

            Log("[FILE] 上传签名数据...");
            CheckCancelled();
            UploadDescriptor(signatureFilePath, "signature", fileId, versionNumber, signatureDescriptor, GetMimeType(".sig"), sigMd5Bytes, cookies);

            CheckCancelled();
            return WaitForFileUrl(fileId, cookies);
        }

        void UploadDescriptor(string filename, string uploadType, string fileId, int versionNumber, Dictionary<string, object> descriptor, string mimeType, byte[] md5Bytes, Dictionary<string, string> cookies)
        {
            var status = GetJsonString(descriptor, "status");
            var category = GetJsonString(descriptor, "category");
            if (!string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(uploadType + " 上传状态非 waiting: " + status);
            if (string.Equals(category, "simple", StringComparison.OrdinalIgnoreCase))
                UploadSimple(filename, uploadType, fileId, versionNumber, mimeType, md5Bytes, cookies);
            else
                UploadMultipart(filename, uploadType, fileId, versionNumber, cookies);
        }

        void UploadSimple(string filename, string uploadType, string fileId, int versionNumber, string mimeType, byte[] md5Bytes, Dictionary<string, string> cookies)
        {
            CheckCancelled();
            var start = SendApi("PUT", "file/" + fileId + "/" + versionNumber + "/" + uploadType + "/start", cookies, null, null);
            EnsureSuccess(start, uploadType + "/start");
            var startJson = ParseJsonObject(start.Text);
            var uploadUrl = GetJsonString(startJson, "url");
            if (string.IsNullOrEmpty(uploadUrl))
                throw new InvalidOperationException("upload start 未返回 URL。");

            var result = WithRetry("simple " + uploadType, delegate
            {
                CheckCancelled();
                var response = UploadFileToUrl(uploadUrl, filename, mimeType, md5Bytes, cookies);
                ThrowIfTransientUploadResponse(response, "simple " + uploadType);
                return response;
            });
            EnsureSuccess(result, "simple " + uploadType);

            CheckCancelled();
            var finish = SendApi("PUT", "file/" + fileId + "/" + versionNumber + "/" + uploadType + "/finish", cookies, null, null);
            EnsureSuccess(finish, uploadType + "/finish");
            Log("[FILE] " + uploadType + " 上传完成。");
        }

        void UploadMultipart(string filename, string uploadType, string fileId, int versionNumber, Dictionary<string, string> cookies)
        {
            CheckCancelled();
            var fileSize = new FileInfo(filename).Length;
            var parallelParts = GetParallelMultipartCount(fileSize);
            if (parallelParts > 1)
            {
                UploadMultipartParallel(filename, uploadType, fileId, versionNumber, cookies, parallelParts);
                return;
            }

            var partSize = GetMultipartPartSize(fileSize);
            var statusResp = SendApi("GET", "file/" + fileId + "/" + versionNumber + "/" + uploadType + "/status", cookies, null, null);
            EnsureSuccess(statusResp, uploadType + "/status");
            var statusJson = ParseJsonObject(statusResp.Text);
            var nextPartNumber = Math.Max(1, GetJsonInt(statusJson, "nextPartNumber", 1));
            var etags = new List<object>();
            var existingEtags = GetJsonList(statusJson, "etags");
            if (existingEtags != null)
                foreach (var etag in existingEtags)
                    if (etag != null) etags.Add(Convert.ToString(etag));

            using (var stream = File.OpenRead(filename))
            {
                var parts = Math.Max(1, (int)Math.Ceiling((double)stream.Length / partSize));
                Log("[FILE] " + uploadType + " 分块大小: " + FormatByteCount(partSize) + ", 共 " + parts + " 片。");
                var buffer = new byte[partSize];
                if (nextPartNumber > 1)
                    stream.Position = (long)(nextPartNumber - 1) * partSize;

                for (var partNumber = nextPartNumber; partNumber <= parts; partNumber++)
                {
                    CheckCancelled();
                    var bytesToRead = partNumber < parts ? partSize : (int)(stream.Length - stream.Position);
                    var bytesRead = ReadExact(stream, buffer, bytesToRead);
                    var sized = new byte[bytesRead];
                    Array.Copy(buffer, sized, bytesRead);

                    Log("[FILE] 上传 " + uploadType + " part " + partNumber + "/" + parts + "...");
                    var start = SendApi("PUT", "file/" + fileId + "/" + versionNumber + "/" + uploadType + "/start?partNumber=" + partNumber, cookies, null, null);
                    EnsureSuccess(start, uploadType + "/start part " + partNumber);
                    var startJson = ParseJsonObject(start.Text);
                    var uploadUrl = GetJsonString(startJson, "url");
                    if (string.IsNullOrEmpty(uploadUrl))
                        throw new InvalidOperationException("multipart start 未返回 URL。");

                    var upload = WithRetry(uploadType + " part " + partNumber, delegate
                    {
                        CheckCancelled();
                        var response = UploadBytesToUrl(uploadUrl, sized, null, null, cookies);
                        ThrowIfTransientUploadResponse(response, uploadType + " part " + partNumber);
                        return response;
                    });
                    EnsureSuccess(upload, uploadType + " part " + partNumber);
                    var etag = upload.Headers == null ? "" : upload.Headers["ETag"];
                    if (!string.IsNullOrEmpty(etag))
                        etags.Add(etag.Trim('"', '\''));
                    SleepCancelable(1000);
                }
            }

            CheckCancelled();
            var finishBody = new Dictionary<string, object>();
            finishBody["etags"] = etags.ToArray();
            var finish = SendApi("PUT", "file/" + fileId + "/" + versionNumber + "/" + uploadType + "/finish", cookies, finishBody, null);
            EnsureSuccess(finish, uploadType + "/finish");
            Log("[FILE] " + uploadType + " 分块上传完成。");
        }

        void UploadMultipartParallel(string filename, string uploadType, string fileId, int versionNumber, Dictionary<string, string> cookies, int maxParallel)
        {
            CheckCancelled();
            var fileSize = new FileInfo(filename).Length;
            var partSize = GetMultipartPartSize(fileSize);
            var statusResp = SendApi("GET", "file/" + fileId + "/" + versionNumber + "/" + uploadType + "/status", cookies, null, null);
            EnsureSuccess(statusResp, uploadType + "/status");
            var statusJson = ParseJsonObject(statusResp.Text);
            var nextPartNumber = Math.Max(1, GetJsonInt(statusJson, "nextPartNumber", 1));
            var parts = Math.Max(1, (int)Math.Ceiling((double)fileSize / partSize));
            var etagsByPart = new string[parts + 1];
            var existingEtags = GetJsonList(statusJson, "etags");
            if (existingEtags != null)
            {
                for (var i = 0; i < existingEtags.Count && i + 1 < etagsByPart.Length; i++)
                    if (existingEtags[i] != null) etagsByPart[i + 1] = Convert.ToString(existingEtags[i]);
            }

            Log("[FILE] " + uploadType + " 分块大小: " + FormatByteCount(partSize) +
                ", 共 " + parts + " 片, 并行 " + maxParallel + " 片。");

            for (var partNumber = nextPartNumber; partNumber <= parts;)
            {
                CheckCancelled();
                var batch = new List<int>();
                for (var i = 0; i < maxParallel && partNumber <= parts; i++, partNumber++)
                    batch.Add(partNumber);

                var threads = new List<Thread>();
                var errors = new List<Exception>();
                var errorLock = new object();
                var etagLock = new object();

                foreach (var item in batch)
                {
                    var currentPart = item;
                    var thread = new Thread(new ThreadStart(delegate
                    {
                        try
                        {
                            var etag = UploadMultipartPart(filename, uploadType, fileId, versionNumber, currentPart, parts, partSize, fileSize, cookies);
                            lock (etagLock) etagsByPart[currentPart] = etag;
                        }
                        catch (OperationCanceledException ex)
                        {
                            lock (errorLock)
                                errors.Add(ex);
                        }
                        catch (Exception ex)
                        {
                            lock (errorLock)
                                errors.Add(new InvalidOperationException(uploadType + " part " + currentPart + "/" + parts + " 上传失败: " + ex.Message, ex));
                        }
                    }));
                    thread.IsBackground = true;
                    threads.Add(thread);
                    thread.Start();
                }

                foreach (var thread in threads)
                    thread.Join();

                CheckCancelled();
                if (errors.Count > 0)
                    throw errors[0];
            }

            CheckCancelled();
            var etags = new List<object>();
            for (var i = 1; i <= parts; i++)
            {
                if (string.IsNullOrEmpty(etagsByPart[i]))
                    throw new InvalidOperationException(uploadType + " part " + i + "/" + parts + " 未返回 ETag。");
                etags.Add(etagsByPart[i]);
            }

            var finishBody = new Dictionary<string, object>();
            finishBody["etags"] = etags.ToArray();
            var finish = SendApi("PUT", "file/" + fileId + "/" + versionNumber + "/" + uploadType + "/finish", cookies, finishBody, null);
            EnsureSuccess(finish, uploadType + "/finish");
            Log("[FILE] " + uploadType + " 分块上传完成。");
        }

        string UploadMultipartPart(string filename, string uploadType, string fileId, int versionNumber, int partNumber, int parts, int partSize, long fileSize, Dictionary<string, string> cookies)
        {
            CheckCancelled();
            var offset = (long)(partNumber - 1) * partSize;
            var bytesToRead = partNumber < parts ? partSize : (int)(fileSize - offset);
            AcquireMultipartSlot(GetGlobalMultipartCount());
            try
            {
                Log("[FILE] 上传 " + uploadType + " part " + partNumber + "/" + parts + "...");
                var start = SendApi("PUT", "file/" + fileId + "/" + versionNumber + "/" + uploadType + "/start?partNumber=" + partNumber, cookies, null, null);
                EnsureSuccess(start, uploadType + "/start part " + partNumber);
                var startJson = ParseJsonObject(start.Text);
                var uploadUrl = GetJsonString(startJson, "url");
                if (string.IsNullOrEmpty(uploadUrl))
                    throw new InvalidOperationException("multipart start 未返回 URL。");

                var upload = WithRetry(uploadType + " part " + partNumber, delegate
                {
                    CheckCancelled();
                    var response = UploadFileSegmentToUrl(uploadUrl, filename, offset, bytesToRead, cookies);
                    ThrowIfTransientUploadResponse(response, uploadType + " part " + partNumber);
                    return response;
                });
                EnsureSuccess(upload, uploadType + " part " + partNumber);
                var etag = upload.Headers == null ? "" : upload.Headers["ETag"];
                if (string.IsNullOrEmpty(etag))
                    throw new InvalidOperationException("multipart part 未返回 ETag。");
                return etag.Trim('"', '\'');
            }
            finally
            {
                ReleaseMultipartSlot();
            }
        }

        string WaitForFileUrl(string fileId, Dictionary<string, string> cookies)
        {
            string lastUrl = "";
            string lastFileStatus = "";
            string lastSignatureStatus = "";
            const int maxAttempts = 100;
            for (var i = 0; i < maxAttempts; i++)
            {
                CheckCancelled();
                var response = WithRetry("file completion status", delegate
                {
                    var statusResponse = SendApi("GET", "file/" + Uri.EscapeDataString(fileId), cookies, null, null);
                    ThrowIfTransientUploadResponse(statusResponse, "file completion status");
                    return statusResponse;
                });
                EnsureSuccess(response, "file/" + fileId);
                var currentFile = ParseJsonObject(response.Text);
                var latest = GetLatestVersionEntry(currentFile);
                var fileDescriptor = AsDictionary(GetJsonValue(latest, "file"));
                var sigDescriptor = AsDictionary(GetJsonValue(latest, "signature"));
                lastUrl = GetJsonString(fileDescriptor, "url");
                lastFileStatus = GetJsonString(fileDescriptor, "status");
                lastSignatureStatus = GetJsonString(sigDescriptor, "status");

                if (!string.IsNullOrEmpty(lastUrl) &&
                    string.Equals(lastFileStatus, "complete", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(lastSignatureStatus, "complete", StringComparison.OrdinalIgnoreCase))
                    return lastUrl;

                if (IsTerminalFileStatus(lastFileStatus) || IsTerminalFileStatus(lastSignatureStatus))
                    throw new InvalidOperationException(
                        "上传文件处理失败: file=" + lastFileStatus + ", signature=" + lastSignatureStatus);

                Log("[FILE] 等待处理... file=" + lastFileStatus + ", signature=" + lastSignatureStatus);
                SleepCancelable(GetFileStatusPollDelayMs(i));
            }
            throw new TimeoutException(
                "等待上传文件和签名完成超时: file=" + lastFileStatus +
                ", signature=" + lastSignatureStatus +
                ", urlPresent=" + (!string.IsNullOrEmpty(lastUrl)).ToString().ToLowerInvariant());
        }

        static bool IsTerminalFileStatus(string status)
        {
            return string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase);
        }

        static int GetFileStatusPollDelayMs(int attempt)
        {
            if (attempt < 3) return 800;
            if (attempt < 6) return 1500;
            return 3000;
        }

        void GenerateSignature(string sourceFilePath, string targetFilePath,
                               out byte[] sourceMd5, out byte[] signatureMd5)
        {
            CheckCancelled();
            using (var source = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read,
                                               FileShare.Read, UploadBufferBytes, FileOptions.SequentialScan))
            using (var sourceHash = MD5.Create())
            using (var hashingSource = new HashingReadStream(source, sourceHash))
            using (var signature = librsync.net.Librsync.ComputeSignature(hashingSource))
            using (var output = File.Open(targetFilePath, FileMode.Create, FileAccess.Write))
            using (var signatureHash = MD5.Create())
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = signature.Read(buffer, 0, buffer.Length)) > 0)
                {
                    CheckCancelled();
                    signatureHash.TransformBlock(buffer, 0, read, buffer, 0);
                    output.Write(buffer, 0, read);
                }
                signatureHash.TransformFinalBlock(new byte[0], 0, 0);
                if (hashingSource.HadSeek)
                {
                    sourceMd5 = GetFileMD5(sourceFilePath);
                }
                else
                {
                    sourceHash.TransformFinalBlock(new byte[0], 0, 0);
                    sourceMd5 = sourceHash.Hash;
                }
                signatureMd5 = signatureHash.Hash;
            }
            CheckCancelled();
        }

        sealed class HashingReadStream : Stream
        {
            readonly Stream _inner;
            readonly HashAlgorithm _hash;
            bool _hadSeek;

            public HashingReadStream(Stream inner, HashAlgorithm hash)
            {
                _inner = inner;
                _hash = hash;
            }

            public bool HadSeek { get { return _hadSeek; } }
            public override bool CanRead { get { return _inner.CanRead; } }
            public override bool CanSeek { get { return _inner.CanSeek; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _inner.Length; } }
            public override long Position
            {
                get { return _inner.Position; }
                set { _hadSeek = true; _inner.Position = value; }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                if (read > 0) _hash.TransformBlock(buffer, offset, read, buffer, offset);
                return read;
            }

            public override int ReadByte()
            {
                var value = _inner.ReadByte();
                if (value >= 0)
                {
                    var one = new byte[] { (byte)value };
                    _hash.TransformBlock(one, 0, 1, one, 0);
                }
                return value;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                _hadSeek = true;
                return _inner.Seek(offset, origin);
            }

            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { base.Dispose(disposing); }
        }

        // =====================================================================
        //  HotswapTool 子进程
        // =====================================================================
        void RunHotswap(string input, string output, string avatarId, int? compressionOverride)
        {
            RunHotswap(input, output, avatarId, compressionOverride, false);
        }

        void RunHotswap(string input, string output, string blueprintId, int? compressionOverride, bool worldMode)
        {
            RunHotswap(input, output, blueprintId, compressionOverride, worldMode, null);
        }


        void RunHotswap(string input, string output, string blueprintId, int? compressionOverride,
                        bool worldMode, int? expectedTargetPlatform)
        {
            CheckCancelled();
            var exe = Path.Combine(_toolsDir, "HotswapTool.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("找不到 HotswapTool.exe，请确认 tools 目录。", exe);

            AcquireHotswapSlot(GetHotswapMaxProcesses());
            try
            {
            var psi = new ProcessStartInfo();
            psi.FileName = exe;
            var args = new List<string>();
            args.Add(Quote(input));
            args.Add(Quote(output));
            args.Add(Quote(blueprintId));
            if (compressionOverride.HasValue || expectedTargetPlatform.HasValue)
            {
                args.Add(Quote(""));
                args.Add(Quote(expectedTargetPlatform.HasValue
                    ? expectedTargetPlatform.Value.ToString(CultureInfo.InvariantCulture)
                    : ""));
                if (compressionOverride.HasValue)
                    args.Add(Quote(compressionOverride.Value.ToString(CultureInfo.InvariantCulture)));
            }
            if (worldMode)
                args.Add("--world");
            psi.Arguments = string.Join(" ", args.ToArray());
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (var process = new Process())
            {
                process.StartInfo = psi;
                var transcript = new Queue<string>();
                var transcriptLock = new object();
                var startedUtc = DateTime.UtcNow;
                var lastHeartbeatUtc = startedUtc;
                var lastProgressUtc = startedUtc;
                var lastOutputUtc = startedUtc;
                var currentStage = "启动 HotswapTool";
                var heartbeatEvery = TimeSpan.FromSeconds(20);
                var stallTimeout = TimeSpan.FromMinutes(GetEnvInt(HotswapStallEnv, 20));
                var maxRuntime = TimeSpan.FromMinutes(GetEnvInt(HotswapTimeoutEnv, 45));
                long lastSize = GetFileSizeSafe(output);
                TimeSpan lastCpu = TimeSpan.Zero;
                bool firstSample = true;

                Action<string> remember = delegate(string line)
                {
                    if (string.IsNullOrEmpty(line)) return;
                    lock (transcriptLock)
                    {
                        transcript.Enqueue(line);
                        while (transcript.Count > 30) transcript.Dequeue();
                    }
                };

                Action<string, string> emit = delegate(string prefix, string line)
                {
                    if (string.IsNullOrEmpty(line)) return;
                    lastOutputUtc = DateTime.UtcNow;
                    currentStage = GuessHotswapStage(line, currentStage);
                    var text = prefix + line;
                    Log(text);
                    remember(text);
                };

                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    emit("[hotswap] ", e.Data);
                };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    emit("[hotswap:err] ", e.Data);
                };
                process.Start();
                RegisterActiveProcess(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                try { lastCpu = process.TotalProcessorTime; }
                catch { lastCpu = TimeSpan.Zero; }

                while (true)
                {
                    if (_cancelRequested)
                    {
                        SafeKill(process);
                        CheckCancelled();
                    }

                    if (process.WaitForExit(1000))
                        break;

                    var now = DateTime.UtcNow;
                    var currentSize = GetFileSizeSafe(output);
                    var currentCpu = TimeSpan.Zero;
                    try { currentCpu = process.TotalProcessorTime; }
                    catch { }

                    var cpuChanged = firstSample || currentCpu != lastCpu;
                    var sizeChanged = currentSize != lastSize;
                    if (cpuChanged || sizeChanged)
                    {
                        lastProgressUtc = now;
                        lastCpu = currentCpu;
                        lastSize = currentSize;
                        firstSample = false;
                    }

                    if (now - lastHeartbeatUtc >= heartbeatEvery)
                    {
                        Log("[hotswap] still working... stage=" + currentStage +
                            " elapsed=" + FormatDuration(now - startedUtc) +
                            " noOutput=" + FormatDuration(now - lastOutputUtc) +
                            " cpuActive=" + (cpuChanged ? "yes" : "no") +
                            " output=" + FormatByteCount(currentSize));
                        lastHeartbeatUtc = now;
                    }

                    if (maxRuntime > TimeSpan.Zero && now - startedUtc > maxRuntime)
                    {
                        SafeKill(process);
                        throw new TimeoutException(BuildHotswapTimeoutMessage("运行超时", startedUtc, lastProgressUtc, currentSize, transcript, transcriptLock));
                    }

                    if (stallTimeout > TimeSpan.Zero && now - lastProgressUtc > stallTimeout)
                    {
                        SafeKill(process);
                        throw new TimeoutException(BuildHotswapTimeoutMessage("长时间无进展", startedUtc, lastProgressUtc, currentSize, transcript, transcriptLock));
                    }
                }

                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("HotswapTool 退出码 " + process.ExitCode);
            }
            }
            finally
            {
                try
                {
                    // Process disposal happens above; this only removes stale cancellation tracking.
                    lock (_cancelLock)
                        _activeProcesses.RemoveAll(delegate(Process p)
                        {
                            try { return p == null || p.HasExited; } catch { return true; }
                        });
                }
                catch { }
                ReleaseHotswapSlot();
            }
        }


        void AcquireHotswapSlot(int maxProcesses)
        {
            lock (_hotswapGate)
            {
                CheckCancelled();
                while (_hotswapRunning >= maxProcesses)
                {
                    Monitor.Wait(_hotswapGate, 1000);
                    CheckCancelled();
                }
                _hotswapRunning++;
            }
        }

        static void ReleaseHotswapSlot()
        {
            lock (_hotswapGate)
            {
                if (_hotswapRunning > 0) _hotswapRunning--;
                Monitor.PulseAll(_hotswapGate);
            }
        }

        void AcquireMultipartSlot(int maxParts)
        {
            lock (_multipartGate)
            {
                CheckCancelled();
                while (_multipartRunning >= maxParts)
                {
                    Monitor.Wait(_multipartGate, 500);
                    CheckCancelled();
                }
                _multipartRunning++;
            }
        }

        static void ReleaseMultipartSlot()
        {
            lock (_multipartGate)
            {
                if (_multipartRunning > 0) _multipartRunning--;
                Monitor.PulseAll(_multipartGate);
            }
        }

        int? ResolveHotswapCompression(int? requestCompression)
        {
            if (requestCompression.HasValue)
            {
                Log("[UPLOAD] Hotswap 压缩覆盖: " + CompressionName(requestCompression.Value) + " (" + requestCompression.Value + ")");
                return requestCompression.Value;
            }
            return GetRequestedHotswapCompression();
        }

        int? GetRequestedHotswapCompression()
        {
            var raw = Environment.GetEnvironmentVariable(HotswapCompressionEnv);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            int parsed;
            if (TryParseCompressionOverride(raw, out parsed))
            {
                Log("[UPLOAD] Hotswap 压缩覆盖: " + raw.Trim() + " -> " + CompressionName(parsed) + " (" + parsed + ")");
                return parsed;
            }

            Log("[UPLOAD] 忽略无效的 Hotswap 压缩设置: " + raw);
            return null;
        }

        static bool TryParseCompressionOverride(string raw, out int compression)
        {
            compression = -1;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            raw = raw.Trim();
            int numeric;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                if (numeric >= 0 && numeric <= 3)
                {
                    compression = numeric;
                    return true;
                }
                return false;
            }

            var normalized = raw.Replace("_", "").Replace("-", "").Trim().ToUpperInvariant();
            if (normalized == "ORIGINAL" || normalized == "AUTO" || normalized == "DEFAULT")
                return false;
            if (normalized == "NONE")
            {
                compression = 0;
                return true;
            }
            if (normalized == "LZMA")
            {
                compression = 1;
                return true;
            }
            if (normalized == "LZ4")
            {
                compression = 2;
                return true;
            }
            if (normalized == "LZ4FAST" || normalized == "LZ4HC" || normalized == "LZ4HIGH")
            {
                compression = 3;
                return true;
            }
            return false;
        }

        static string CompressionName(int compression)
        {
            switch (compression)
            {
                case 0: return "None";
                case 1: return "LZMA";
                case 2: return "LZ4";
                case 3: return "LZ4Fast";
                default: return "Unknown";
            }
        }

        static string GuessHotswapStage(string line, string fallback)
        {
            if (string.IsNullOrEmpty(line)) return fallback;
            if (line.IndexOf("[1]", StringComparison.OrdinalIgnoreCase) >= 0)
                return "读取/解包 VRCA";
            if (line.IndexOf("Header.Version", StringComparison.OrdinalIgnoreCase) >= 0)
                return "分析 bundle 元数据";
            if (line.IndexOf("PipelineManager blueprintId", StringComparison.OrdinalIgnoreCase) >= 0)
                return "改写 blueprintId";
            if (line.IndexOf("[2]", StringComparison.OrdinalIgnoreCase) >= 0)
                return "写入修改后的 assets";
            if (line.IndexOf("[3]", StringComparison.OrdinalIgnoreCase) >= 0)
                return "生成 raw bundle";
            if (line.IndexOf("Pack(compression=LZMA", StringComparison.OrdinalIgnoreCase) >= 0)
                return "LZMA 重打包";
            if (line.IndexOf("Pack(compression=LZ4", StringComparison.OrdinalIgnoreCase) >= 0)
                return "LZ4 快速重打包";
            if (line.IndexOf("[4]", StringComparison.OrdinalIgnoreCase) >= 0)
                return "重打包 bundle";
            if (line.IndexOf("[5]", StringComparison.OrdinalIgnoreCase) >= 0)
                return "重打包完成";
            return fallback;
        }

        static long GetFileSizeSafe(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : -1;
            }
            catch
            {
                return -1;
            }
        }

        static string FormatByteCount(long bytes)
        {
            if (bytes < 0) return "n/a";
            return (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";
        }

        static string ComputeSha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }

        static string ComputeFileSha256Hex(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            lock (_sourceHashCacheLock)
            {
                SourceHashCacheEntry cached;
                if (_sourceHashCache.TryGetValue(fullPath, out cached) &&
                    cached.Length == info.Length &&
                    cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                    !string.IsNullOrEmpty(cached.Sha256))
                    return cached.Sha256;
            }

            string hash;
            using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");

            lock (_sourceHashCacheLock)
            {
                _sourceHashCache[fullPath] = new SourceHashCacheEntry
                {
                    Length = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Sha256 = hash
                };
            }
            return hash;
        }

        string BuildPreparedWorldCachePath(string sourcePath, string worldId, int? compression, string platform)
        {
            var sourceHash = ComputeFileSha256Hex(sourcePath);
            var material = "prepared-world-v2|" + sourceHash + "|" + worldId + "|" +
                (platform ?? "") + "|" +
                (compression.HasValue ? compression.Value.ToString(CultureInfo.InvariantCulture) : "original") + "|" +
                (Environment.GetEnvironmentVariable("HOTUPLOAD_CAB_OVERRIDE") ?? "") + "|" +
                (Environment.GetEnvironmentVariable("HOTUPLOAD_RANDOMIZE_CAB") ?? "");
            var key = ComputeSha256Hex(Encoding.UTF8.GetBytes(material)).Substring(0, 24).ToLowerInvariant();
            var directory = Path.Combine(_runtimeDir, "prepared-worlds");
            Directory.CreateDirectory(directory);
            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            return Path.Combine(directory, baseName + "." + key + ".vrcw");
        }

        static string FormatDuration(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            var totalSeconds = (long)Math.Floor(span.TotalSeconds);
            var hours = totalSeconds / 3600;
            var minutes = (totalSeconds % 3600) / 60;
            var seconds = totalSeconds % 60;
            return hours.ToString("00", CultureInfo.InvariantCulture) + ":" +
                minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        static int GetEnvInt(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            int parsed;
            if (!string.IsNullOrWhiteSpace(raw) &&
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
                parsed > 0)
                return parsed;
            return fallback;
        }

        static int GetHotswapMaxProcesses()
        {
            var fallback = Math.Max(1, Math.Min(3, (Environment.ProcessorCount + 3) / 4));
            var value = GetEnvInt(HotswapMaxProcessesEnv, fallback);
            if (value < 1) return 1;
            if (value > 8) return 8;
            return value;
        }

        static int GetMultipartPartSize(long fileSize)
        {
            var raw = Environment.GetEnvironmentVariable(MultipartPartSizeMbEnv);
            int mb;
            if (!string.IsNullOrWhiteSpace(raw) &&
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out mb))
            {
                if (mb < 5) mb = 5;
                if (mb > 100) mb = 100;
                return mb * 1024 * 1024;
            }

            if (fileSize <= 128L * 1024 * 1024) return 16 * 1024 * 1024;
            if (fileSize <= 384L * 1024 * 1024) return 24 * 1024 * 1024;
            if (fileSize <= 768L * 1024 * 1024) return DefaultMultipartPartSize;
            return 48 * 1024 * 1024;
        }

        static int GetParallelMultipartCount(long fileSize)
        {
            var raw = Environment.GetEnvironmentVariable(ParallelMultipartEnv);
            int count;
            if (!string.IsNullOrWhiteSpace(raw) &&
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
            {
                if (count < 1) count = 1;
                if (count > 12) count = 12;
                return count;
            }

            if (fileSize > 128L * 1024 * 1024) return DefaultParallelMultipartParts;
            return DefaultParallelMultipartParts;
        }

        static int GetGlobalMultipartCount()
        {
            var value = GetEnvInt(GlobalParallelMultipartEnv, DefaultGlobalMultipartParts);
            if (value < 2) return 2;
            if (value > 16) return 16;
            return value;
        }

        static void SafeKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch { }
        }

        static string BuildHotswapTimeoutMessage(string reason, DateTime startedUtc, DateTime lastProgressUtc, long currentSize, Queue<string> transcript, object transcriptLock)
        {
            var sb = new StringBuilder();
            sb.Append("HotswapTool ").Append(reason)
              .Append("，elapsed=").Append(FormatDuration(DateTime.UtcNow - startedUtc))
              .Append("，sinceProgress=").Append(FormatDuration(DateTime.UtcNow - lastProgressUtc))
              .Append("，output=").Append(FormatByteCount(currentSize));

            var tail = SnapshotTranscript(transcript, transcriptLock);
            if (!string.IsNullOrEmpty(tail))
            {
                sb.Append(Environment.NewLine).Append("最近输出:");
                sb.Append(Environment.NewLine).Append(tail);
            }
            return sb.ToString();
        }

        static string SnapshotTranscript(Queue<string> transcript, object transcriptLock)
        {
            if (transcript == null) return "";
            lock (transcriptLock)
            {
                if (transcript.Count == 0) return "";
                return string.Join(Environment.NewLine, transcript.ToArray());
            }
        }

        // =====================================================================
        //  HTTP 层
        // =====================================================================
        HttpResult SendApi(string method, string endpoint, Dictionary<string, string> cookies, object body, string authorization)
        {
            CheckCancelled();
            ConfigureNetwork();
            var url = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? endpoint : ApiBaseUrl + endpoint.TrimStart('/');

            var json = body == null ? null : NewJson().Serialize(body);
            var bodyBytes = json == null ? null : Encoding.UTF8.GetBytes(json);

            TraceRequest(method, url, body);

            var diagnosticClock = Stopwatch.StartNew();
            var request = (HttpWebRequest)WebRequest.Create(url);
            RegisterActiveRequest(request);
            request.Method = method;
            request.Accept = "application/json";
            request.UserAgent = ApiUserAgent;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            request.CookieContainer = BuildCookieContainer(cookies);
            request.Headers["X-MacAddress"] = GetOrCreateDeviceId();
            request.Headers["X-SDK-Version"] = SdkVersion;
            request.Headers["X-Platform"] = Platform;
            request.Headers["X-Unity-Version"] = SafeUnityVersion(_activeUnityVersion);
            if (!string.IsNullOrEmpty(authorization))
                request.Headers[HttpRequestHeader.Authorization] = authorization;

            try
            {
                if (bodyBytes != null)
                {
                    CheckCancelled();
                    request.ContentType = "application/json";
                    request.ContentLength = bodyBytes.Length;
                    using (var stream = request.GetRequestStream())
                        stream.Write(bodyBytes, 0, bodyBytes.Length);
                }
                else if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    request.ContentLength = 0;
                }

                CheckCancelled();
                var result = ReadResponse(request);
                if (cookies != null)
                {
                    bool authUpdated;
                    lock (cookies)
                        authUpdated = AddResponseCookies(result.Headers, cookies);
                    if (authUpdated)
                        PersistAuthCookies(cookies);
                }
                TraceResponse(method, url, result);
                return result;
            }
            catch (Exception ex)
            {
                Log("[HTTP_EXCEPTION] method=" + request.Method + " url=" + SafeUrl(url) + " elapsedMs=" + diagnosticClock.ElapsedMilliseconds + " " + ex.ToString());
                throw;
            }
            finally
            {
                Log("[HTTP_TIMING] method=" + request.Method + " url=" + SafeUrl(url) + " elapsedMs=" + diagnosticClock.ElapsedMilliseconds + " requestBytes=" + request.ContentLength);
                UnregisterActiveRequest(request);
            }
        }

        HttpResult UploadFileToUrl(string url, string filename, string contentType, byte[] contentMd5, Dictionary<string, string> cookies)
        {
            CheckCancelled();
            ConfigureNetwork();
            TraceRequest("PUT", url, null);
            var diagnosticClock = Stopwatch.StartNew();
            var request = (HttpWebRequest)WebRequest.Create(url);
            RegisterActiveRequest(request);
            request.Method = "PUT";
            ApplyUploadHeaders(request, cookies);
            request.Timeout = 60 * 60 * 1000;
            request.ReadWriteTimeout = 60 * 60 * 1000;
            request.AllowWriteStreamBuffering = false;
            request.ContentLength = new FileInfo(filename).Length;
            if (!string.IsNullOrEmpty(contentType)) request.ContentType = contentType;
            if (contentMd5 != null) request.Headers["Content-MD5"] = Convert.ToBase64String(contentMd5);

            try
            {
                using (var input = new FileStream(filename, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read, UploadBufferBytes, FileOptions.SequentialScan))
                using (var output = request.GetRequestStream())
                {
                    var buffer = new byte[UploadBufferBytes];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        CheckCancelled();
                        output.Write(buffer, 0, read);
                    }
                }
                CheckCancelled();
                var result = ReadResponse(request);
                TraceResponse("PUT", url, result);
                return result;
            }
            catch (Exception ex)
            {
                Log("[HTTP_EXCEPTION] method=" + request.Method + " url=" + SafeUrl(url) + " elapsedMs=" + diagnosticClock.ElapsedMilliseconds + " " + ex.ToString());
                throw;
            }
            finally
            {
                Log("[HTTP_TIMING] method=" + request.Method + " url=" + SafeUrl(url) + " elapsedMs=" + diagnosticClock.ElapsedMilliseconds + " requestBytes=" + request.ContentLength);
                UnregisterActiveRequest(request);
            }
        }

        HttpResult UploadFileSegmentToUrl(string url, string filename, long offset, int length, Dictionary<string, string> cookies)
        {
            CheckCancelled();
            ConfigureNetwork();
            TraceRequest("PUT", url, null);
            var diagnosticClock = Stopwatch.StartNew();
            var request = (HttpWebRequest)WebRequest.Create(url);
            RegisterActiveRequest(request);
            request.Method = "PUT";
            ApplyUploadHeaders(request, cookies);
            request.Timeout = 60 * 60 * 1000;
            request.ReadWriteTimeout = 60 * 60 * 1000;
            request.AllowWriteStreamBuffering = false;
            request.ContentLength = length;

            try
            {
                using (var input = new FileStream(filename, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read, UploadBufferBytes, FileOptions.RandomAccess))
                using (var output = request.GetRequestStream())
                {
                    input.Position = offset;
                    var buffer = new byte[UploadBufferBytes];
                    var remaining = length;
                    while (remaining > 0)
                    {
                        CheckCancelled();
                        var read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                        if (read <= 0)
                            throw new EndOfStreamException("分片读取提前结束。");
                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
                CheckCancelled();
                var result = ReadResponse(request);
                TraceResponse("PUT", url, result);
                return result;
            }
            catch (Exception ex)
            {
                Log("[HTTP_EXCEPTION] method=" + request.Method + " url=" + SafeUrl(url) + " elapsedMs=" + diagnosticClock.ElapsedMilliseconds + " " + ex.ToString());
                throw;
            }
            finally
            {
                Log("[HTTP_TIMING] method=" + request.Method + " url=" + SafeUrl(url) + " elapsedMs=" + diagnosticClock.ElapsedMilliseconds + " requestBytes=" + request.ContentLength);
                UnregisterActiveRequest(request);
            }
        }

        HttpResult UploadBytesToUrl(string url, byte[] bytes, string contentType, byte[] contentMd5, Dictionary<string, string> cookies)
        {
            CheckCancelled();
            ConfigureNetwork();
            TraceRequest("PUT", url, null);
            var diagnosticClock = Stopwatch.StartNew();
            var request = (HttpWebRequest)WebRequest.Create(url);
            RegisterActiveRequest(request);
            request.Method = "PUT";
            ApplyUploadHeaders(request, cookies);
            request.Timeout = 60 * 60 * 1000;
            request.ReadWriteTimeout = 60 * 60 * 1000;
            request.ContentLength = bytes == null ? 0 : bytes.Length;
            if (!string.IsNullOrEmpty(contentType)) request.ContentType = contentType;
            if (contentMd5 != null) request.Headers["Content-MD5"] = Convert.ToBase64String(contentMd5);

            try
            {
                if (bytes != null && bytes.Length > 0)
                {
                    using (var stream = request.GetRequestStream())
                    {
                        const int chunkSize = 1024 * 1024;
                        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
                        {
                            CheckCancelled();
                            var count = Math.Min(chunkSize, bytes.Length - offset);
                            stream.Write(bytes, offset, count);
                        }
                    }
                }
                CheckCancelled();
                var result = ReadResponse(request);
                TraceResponse("PUT", url, result);
                return result;
            }
            catch (Exception ex)
            {
                Log("[HTTP_EXCEPTION] method=" + request.Method + " url=" + SafeUrl(url) + " elapsedMs=" + diagnosticClock.ElapsedMilliseconds + " " + ex.ToString());
                throw;
            }
            finally
            {
                Log("[HTTP_TIMING] method=" + request.Method + " url=" + SafeUrl(url) + " elapsedMs=" + diagnosticClock.ElapsedMilliseconds + " requestBytes=" + request.ContentLength);
                UnregisterActiveRequest(request);
            }
        }

        void ApplyUploadHeaders(HttpWebRequest request, Dictionary<string, string> cookies)
        {
            var isVrchatHost = request.RequestUri != null &&
                request.RequestUri.Host.EndsWith("vrchat.cloud", StringComparison.OrdinalIgnoreCase);
            if (isVrchatHost)
            {
                request.UserAgent = ApiUserAgent;
                request.Accept = "application/json";
                request.CookieContainer = BuildCookieContainer(cookies);
                request.Headers["X-MacAddress"] = GetOrCreateDeviceId();
                request.Headers["X-SDK-Version"] = SdkVersion;
                request.Headers["X-Platform"] = Platform;
                request.Headers["X-Unity-Version"] = SafeUnityVersion(_activeUnityVersion);
            }
            else
            {
                request.UserAgent = UploadUserAgent;
            }
        }

        T WithRetry<T>(string label, Func<T> action)
        {
            Exception last = null;
            for (var attempt = 1; attempt <= MaxUploadRetryAttempts; attempt++)
            {
                CheckCancelled();
                try { return action(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt >= MaxUploadRetryAttempts) break;
                    Log("[RETRY] " + label + " 失败, 重试 " + attempt + "/" + MaxUploadRetryAttempts + ": " + ex.Message);
                    // Quadratic backoff plus a deterministic per-part stagger
                    // prevents all failed parallel parts reconnecting together.
                    var baseDelay = Math.Min(30000, 1500 * attempt * attempt);
                    var stagger = (label.GetHashCode() & 0x7fffffff) % 1500;
                    SleepCancelable(baseDelay + stagger);
                }
            }
            throw new InvalidOperationException(label + " 重试后仍失败: " + (last == null ? "未知" : last.Message), last);
        }

        static void ThrowIfTransientUploadResponse(HttpResult response, string label)
        {
            if (response == null)
                throw new IOException(label + " 未返回 HTTP 响应。");

            var status = response.StatusCode;
            if (status == 408 || status == 425 || status == 429 || (status >= 500 && status <= 599))
                throw new ApiException(status, response.Text, label);
        }

        void CheckCancelled()
        {
            if (_cancelRequested)
                throw new OperationCanceledException("上传任务已停止。");
        }

        void SleepCancelable(int milliseconds)
        {
            var remaining = milliseconds;
            while (remaining > 0)
            {
                CheckCancelled();
                var step = Math.Min(remaining, 200);
                Thread.Sleep(step);
                remaining -= step;
            }
            CheckCancelled();
        }

        void RegisterActiveRequest(HttpWebRequest request)
        {
            if (request == null) return;
            var abortNow = false;
            lock (_cancelLock)
            {
                if (_cancelRequested) abortNow = true;
                else _activeRequests.Add(request);
            }
            if (abortNow)
            {
                try { request.Abort(); } catch { }
                CheckCancelled();
            }
        }

        void UnregisterActiveRequest(HttpWebRequest request)
        {
            if (request == null) return;
            lock (_cancelLock)
                _activeRequests.Remove(request);
        }

        void RegisterActiveProcess(Process process)
        {
            if (process == null) return;
            var killNow = false;
            lock (_cancelLock)
            {
                if (_cancelRequested) killNow = true;
                else _activeProcesses.Add(process);
            }
            if (killNow)
            {
                SafeKill(process);
                CheckCancelled();
            }
        }

        static void ConfigureNetwork()
        {
            try
            {
                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 96);
                ServicePointManager.CheckCertificateRevocationList = false;
            }
            catch { }
        }

        HttpResult ReadResponse(HttpWebRequest request)
        {
            try
            {
                CheckCancelled();
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    CheckCancelled();
                    return new HttpResult { StatusCode = (int)response.StatusCode, Text = ReadResponseText(response), Headers = response.Headers };
                }
            }
            catch (WebException ex)
            {
                CheckCancelled();
                var response = ex.Response as HttpWebResponse;
                if (response == null) throw;
                return new HttpResult { StatusCode = (int)response.StatusCode, Text = ReadResponseText(response), Headers = response.Headers };
            }
        }

        static string ReadResponseText(HttpWebResponse response)
        {
            using (var stream = response.GetResponseStream())
            {
                if (stream == null) return "";
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        static bool IsSuccess(HttpResult result) { return result.StatusCode >= 200 && result.StatusCode < 300; }

        static void EnsureSuccess(HttpResult result, string label)
        {
            if (!IsSuccess(result))
                throw new ApiException(result.StatusCode, result.Text, label);
        }

        // =====================================================================
        //  Cookie
        // =====================================================================
        static CookieContainer BuildCookieContainer(Dictionary<string, string> cookies)
        {
            var container = new CookieContainer();
            if (cookies != null)
            {
                var domain = new Uri(ApiBaseUrl).Host;
                lock (cookies)
                {
                    foreach (var kv in cookies)
                    {
                        if (string.IsNullOrEmpty(kv.Value)) continue;
                        try { container.Add(new Cookie(kv.Key, kv.Value, "/", domain)); }
                        catch { }
                    }
                }
            }
            return container;
        }

        static bool AddResponseCookies(WebHeaderCollection headers, Dictionary<string, string> cookies)
        {
            var changed = false;
            if (headers == null || cookies == null) return false;
            var setCookies = headers.GetValues("Set-Cookie");
            if (setCookies == null) return false;
            foreach (var sc in setCookies)
            {
                if (string.IsNullOrEmpty(sc)) continue;
                var firstSegment = sc.Split(';')[0];
                var eq = firstSegment.IndexOf('=');
                if (eq <= 0) continue;
                var name = firstSegment.Substring(0, eq).Trim();
                var value = firstSegment.Substring(eq + 1).Trim();
                if (string.Equals(name, "auth", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "twoFactorAuth", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(value)) continue;
                    string previous;
                    if (!cookies.TryGetValue(name, out previous) ||
                        !string.Equals(previous, value, StringComparison.Ordinal))
                    {
                        cookies[name] = value;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        void PersistAuthCookies(Dictionary<string, string> cookies)
        {
            try
            {
                string auth;
                string twoFactorAuth;
                lock (cookies)
                {
                    cookies.TryGetValue("auth", out auth);
                    cookies.TryGetValue("twoFactorAuth", out twoFactorAuth);
                }
                if (string.IsNullOrEmpty(auth)) return;

                lock (_authCacheLock)
                {
                    var cache = LoadAuthCache();
                    if (cache == null) return;
                    cache.Auth = auth;
                    if (!string.IsNullOrEmpty(twoFactorAuth))
                        cache.TwoFactorAuth = twoFactorAuth;
                    cache.SavedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    SaveAuthCache(cache);
                }
            }
            catch { }
        }

        // =====================================================================
        //  认证缓存 (DPAPI)
        // =====================================================================
        string AuthCachePath() { return Path.Combine(_authDir, "vrc_auth.json"); }

        string LegacyAuthCachePath() { return Path.Combine(_runtimeDir, "vrc_auth.json"); }

        void SaveAuthCache(AuthCache cache)
        {
            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            data["Username"] = cache == null ? "" : (cache.Username ?? "");
            data["DisplayName"] = cache == null ? "" : (cache.DisplayName ?? "");
            data["UserId"] = cache == null ? "" : (cache.UserId ?? "");
            data["Auth"] = cache == null ? "" : (cache.Auth ?? "");
            data["TwoFactorAuth"] = cache == null ? "" : (cache.TwoFactorAuth ?? "");
            data["DeviceId"] = cache == null ? "" : (cache.DeviceId ?? "");
            data["SavedAtUtc"] = cache == null ? "" : (cache.SavedAtUtc ?? "");
            var json = NewJson().Serialize(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(_authDir);
            File.WriteAllText(AuthCachePath(), "dpapi:" + Convert.ToBase64String(enc), Encoding.UTF8);
        }

        AuthCache LoadAuthCache()
        {
            var cache = LoadAuthCacheFile(AuthCachePath());
            if (cache != null) return cache;

            if (_isolatedAuthSession)
                return null;

            cache = LoadAuthCacheFile(LegacyAuthCachePath());
            if (cache != null)
            {
                try { SaveAuthCache(cache); } catch { }
                return cache;
            }

            return null;
        }

        AuthCache LoadAuthCacheFile(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var content = File.ReadAllText(path, Encoding.UTF8);
                if (!content.StartsWith("dpapi:")) return null;
                var enc = Convert.FromBase64String(content.Substring("dpapi:".Length));
                var bytes = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(bytes);
                var data = ParseJsonObject(json);
                var cache = new AuthCache();
                cache.Username = GetJsonString(data, "Username");
                cache.DisplayName = GetJsonString(data, "DisplayName");
                cache.UserId = GetJsonString(data, "UserId");
                cache.Auth = GetJsonString(data, "Auth");
                cache.TwoFactorAuth = GetJsonString(data, "TwoFactorAuth");
                cache.DeviceId = GetJsonString(data, "DeviceId");
                cache.SavedAtUtc = GetJsonString(data, "SavedAtUtc");
                if (string.IsNullOrEmpty(cache.Auth)) return null;
                return cache;
            }
            catch { return null; }
        }

        static Dictionary<string, string> BuildCookiesFromCache(AuthCache cache)
        {
            var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (cache != null)
            {
                if (!string.IsNullOrEmpty(cache.Auth)) cookies["auth"] = cache.Auth;
                if (!string.IsNullOrEmpty(cache.TwoFactorAuth)) cookies["twoFactorAuth"] = cache.TwoFactorAuth;
            }
            return cookies;
        }

        string GetOrCreateDeviceId()
        {
            var path = Path.Combine(_authDir, "device_id.txt");
            try
            {
                Directory.CreateDirectory(_authDir);
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(existing)) return existing;
                }
                var legacyPath = Path.Combine(_runtimeDir, "device_id.txt");
                if (File.Exists(legacyPath))
                {
                    var legacy = File.ReadAllText(legacyPath, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(legacy))
                    {
                        File.WriteAllText(path, legacy, Encoding.UTF8);
                        return legacy;
                    }
                }
                var id = Guid.NewGuid().ToString("N");
                File.WriteAllText(path, id, Encoding.UTF8);
                return id;
            }
            catch { return Guid.NewGuid().ToString("N"); }
        }

        // =====================================================================
        //  2FA 检测
        // =====================================================================
        static string GetRequiredTwoFactorType(Dictionary<string, object> json)
        {
            var values = json != null && json.ContainsKey("requiresTwoFactorAuth") ? json["requiresTwoFactorAuth"] as IList : null;
            if (values != null && values.Count > 0)
            {
                foreach (var item in values)
                {
                    var value = (item == null ? "" : Convert.ToString(item)).ToLowerInvariant();
                    if (value.IndexOf("email", StringComparison.OrdinalIgnoreCase) >= 0) return "email";
                    if (value.IndexOf("totp", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("otp", StringComparison.OrdinalIgnoreCase) >= 0) return "totp";
                }
                return "totp";
            }
            return GetRequiredTwoFactorTypeFromText(NewJson().Serialize(json));
        }

        static bool LooksLikeTwoFactorRequired(string text) { return !string.IsNullOrEmpty(GetRequiredTwoFactorTypeFromText(text)); }

        static string GetRequiredTwoFactorTypeFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var lower = text.ToLowerInvariant();
            if (lower.IndexOf("requirestwofactorauth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("twofactorauth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("2fa", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (lower.IndexOf("email", StringComparison.OrdinalIgnoreCase) >= 0) return "email";
                return "totp";
            }
            return "";
        }

        // =====================================================================
        //  JSON / MIME 辅助
        // =====================================================================
        static string GetMimeType(string extension)
        {
            extension = (extension == null ? "" : extension).ToLowerInvariant();
            if (extension == ".vrcw") return "application/x-world";
            if (extension == ".vrca") return "application/x-avatar";
            if (extension == ".vrcp") return "application/x-prop";
            if (extension == ".jpg") return "image/jpg";
            if (extension == ".jpeg") return "image/jpg";
            if (extension == ".png") return "image/png";
            if (extension == ".sig") return "application/x-rsync-signature";
            if (extension == ".delta") return "application/x-rsync-delta";
            return "application/octet-stream";
        }

        static JavaScriptSerializer NewJson()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 128;
            return s;
        }

        static Dictionary<string, object> ParseJsonObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var parsed = NewJson().DeserializeObject(text) as Dictionary<string, object>;
            return parsed ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        static IList ParseJsonList(string text)
        {
            if (string.IsNullOrEmpty(text)) return new object[0];
            return NewJson().DeserializeObject(text) as IList ?? new object[0];
        }

        static Dictionary<string, object> AsDictionary(object value)
        {
            var dict = value as Dictionary<string, object>;
            return dict ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        static object GetJsonValue(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            object value;
            return dict.TryGetValue(key, out value) ? value : null;
        }

        static string GetJsonString(Dictionary<string, object> dict, string key)
        {
            var value = GetJsonValue(dict, key);
            return value == null ? "" : Convert.ToString(value);
        }

        static int GetJsonInt(Dictionary<string, object> dict, string key, int fallback)
        {
            var value = GetJsonValue(dict, key);
            if (value == null) return fallback;
            try { return Convert.ToInt32(value); }
            catch { return fallback; }
        }

        static long GetJsonLong(Dictionary<string, object> dict, string key, long fallback)
        {
            var value = GetJsonValue(dict, key);
            if (value == null) return fallback;
            try { return Convert.ToInt64(value); }
            catch { return fallback; }
        }

        static string FirstNonEmpty(string first, string second)
        {
            return string.IsNullOrEmpty(first) ? (second ?? "") : first;
        }

        static string GetJsonBoolString(Dictionary<string, object> dict, string key)
        {
            var value = GetJsonValue(dict, key);
            if (value == null) return "";
            try { return Convert.ToBoolean(value).ToString().ToLowerInvariant(); }
            catch { return Convert.ToString(value); }
        }

        static IList GetJsonList(Dictionary<string, object> dict, string key) { return GetJsonValue(dict, key) as IList; }

        FileInspect InspectFile(string fileId, Dictionary<string, string> cookies)
        {
            var result = new FileInspect();
            result.Id = fileId;

            var response = SendApi("GET", "file/" + Uri.EscapeDataString(fileId), cookies, null, null);
            EnsureSuccess(response, "file/" + fileId);
            var file = ParseJsonObject(response.Text);
            result.Name = GetJsonString(file, "name");
            result.Extension = GetJsonString(file, "extension");
            var latest = GetLatestVersionEntry(file);
            result.Version = GetJsonInt(latest, "version", -1);
            var fileDescriptor = AsDictionary(GetJsonValue(latest, "file"));
            var signatureDescriptor = AsDictionary(GetJsonValue(latest, "signature"));
            result.FileStatus = GetJsonString(fileDescriptor, "status");
            result.SignatureStatus = GetJsonString(signatureDescriptor, "status");
            result.FileSizeInBytes = GetJsonInt(fileDescriptor, "sizeInBytes", -1);
            result.SignatureSizeInBytes = GetJsonInt(signatureDescriptor, "sizeInBytes", -1);
            result.FileUrlPresent = string.IsNullOrEmpty(GetJsonString(fileDescriptor, "url")) ? "false" : "true";
            result.SignatureUrlPresent = string.IsNullOrEmpty(GetJsonString(signatureDescriptor, "url")) ? "false" : "true";
            if (string.Equals(result.Extension, ".vrca", StringComparison.OrdinalIgnoreCase))
                InspectFileSecurity(result, cookies);
            return result;
        }

        void InspectFileSecurity(FileInspect result, Dictionary<string, string> cookies)
        {
            result.SecurityAnalysisStatusCode = -1;
            result.SecurityAnalysis = "not_checked";
            result.SecurityAnalysisMessage = "";

            if (result == null || string.IsNullOrEmpty(result.Id) || result.Version <= 0)
                return;

            var endpoint = "analysis/" + Uri.EscapeDataString(result.Id) + "/" +
                           result.Version.ToString(CultureInfo.InvariantCulture) + "/security";
            try
            {
                var response = SendApi("GET", endpoint, cookies, null, null);
                result.SecurityAnalysisStatusCode = response.StatusCode;
                if (IsSuccess(response))
                {
                    result.SecurityAnalysis = "available";
                    result.SecurityAnalysisMessage = CompactText(response.Text, 180);
                }
                else
                {
                    result.SecurityAnalysis = response.StatusCode == 403 ? "forbidden" : "failed";
                    result.SecurityAnalysisMessage = CompactText(response.Text, 180);
                }
            }
            catch (Exception ex)
            {
                result.SecurityAnalysis = "error";
                result.SecurityAnalysisMessage = ex.GetType().Name + ": " + ex.Message;
            }
        }

        static string CompactText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
                text = text.Replace("  ", " ");
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
        }

        static string ExtractFileId(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            var idx = url.IndexOf("file_", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            var end = idx;
            while (end < url.Length)
            {
                var c = url[end];
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) break;
                end++;
            }
            return url.Substring(idx, end - idx);
        }

        static Dictionary<string, object> GetLatestVersionEntry(Dictionary<string, object> file)
        {
            var versions = GetJsonList(file, "versions");
            if (versions == null || versions.Count == 0)
                throw new InvalidOperationException("file 响应未包含 versions。");
            var latest = AsDictionary(versions[0]);
            var latestNumber = GetJsonInt(latest, "version", 0);
            for (var i = 1; i < versions.Count; i++)
            {
                var current = AsDictionary(versions[i]);
                var currentNumber = GetJsonInt(current, "version", i);
                if (currentNumber >= latestNumber)
                {
                    latest = current;
                    latestNumber = currentNumber;
                }
            }
            return latest;
        }

        static int GetLatestVersionNumber(Dictionary<string, object> file)
        {
            var latest = GetLatestVersionEntry(file);
            var versions = GetJsonList(file, "versions");
            return GetJsonInt(latest, "version", Math.Max(0, (versions == null ? 0 : versions.Count) - 1));
        }

        byte[] GetFileMD5(string filePath)
        {
            using (var md5 = MD5.Create())
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                               FileShare.Read, UploadBufferBytes, FileOptions.SequentialScan))
            {
                var buffer = new byte[UploadBufferBytes];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    CheckCancelled();
                    md5.TransformBlock(buffer, 0, read, buffer, 0);
                }
                md5.TransformFinalBlock(new byte[0], 0, 0);
                CheckCancelled();
                return md5.Hash;
            }
        }

        int ReadExact(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                CheckCancelled();
                var read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) break;
                offset += read;
            }
            CheckCancelled();
            return offset;
        }

        // =====================================================================
        //  Trace
        // =====================================================================
        void TraceRequest(string method, string url, object body)
        {
            Log("[API_TRACE] request method=" + method + " url=" + SafeUrl(url) +
                " sdk=" + SdkVersion + " unity=" + SafeUnityVersion(_activeUnityVersion));
            if (body != null)
            {
                var json = NewJson().Serialize(body);
                if (url.IndexOf("/auth/twofactorauth/", StringComparison.OrdinalIgnoreCase) >= 0)
                    json = "{\"code\":\"<redacted>\"}";
                Log("[API_TRACE] request-json=" + json);
            }
        }

        void TraceResponse(string method, string url, HttpResult result)
        {
            Log("[API_TRACE] response method=" + method + " url=" + SafeUrl(url) + " status=" + result.StatusCode);
            if (result.Headers != null)
                foreach (var key in new[] { "Retry-After", "X-Request-Id", "X-Correlation-Id", "CF-Ray", "Content-Type", "ETag" })
                    if (!string.IsNullOrEmpty(result.Headers[key])) Log("[API_TRACE] response-header " + key + "=" + result.Headers[key]);
            if (result.StatusCode >= 400 && !string.IsNullOrEmpty(result.Text))
                Log("[API_ERROR_BODY] " + LimitDiagnosticBody(ProtectDiagnosticText(result.Text), 32768));
        }

        static string SafeUrl(string url) { return ProtectDiagnosticUrl(url); }

        static string JsonShape(object body)
        {
            var dict = body as Dictionary<string, object>;
            if (dict == null) return "{}";
            var sb = new StringBuilder();
            sb.Append("{");
            var first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append(kv.Key).Append(":").Append(TypeName(kv.Value));
            }
            sb.Append("}");
            return sb.ToString();
        }

        static string TypeName(object value)
        {
            if (value == null) return "null";
            if (value is string) return "String";
            if (value is bool) return "Boolean";
            if (value is int || value is long) return "Int";
            if (value is double || value is float) return "Number";
            if (value is IList) return "Array";
            if (value is IDictionary) return "Object";
            return value.GetType().Name;
        }

        // =====================================================================
        //  日志 / 工具
        // =====================================================================
        void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var detail = ProtectDiagnosticText(message);
            lock (_logFileLock)
            {
                try
                {
                    if (!string.IsNullOrEmpty(_logFilePath))
                        File.AppendAllText(_logFilePath, FormatDiagnosticEntry(detail), Encoding.UTF8);
                }
                catch { }
                try { _log(detail); } catch { }
            }
        }

        static string ToPublicLogMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;

            if (StartsWithTag(message, "[API_TRACE]")) return null;
            if (StartsWithTag(message, "[preflight]")) return null;
            if (StartsWithTag(message, "[preflight:err]")) return null;
            if (StartsWithTag(message, "[world-preflight]")) return null;
            if (StartsWithTag(message, "[world-preflight:err]")) return null;
            if (StartsWithTag(message, "[hotswap:err]")) return null;

            if (StartsWithTag(message, "[RUN]")) return "[RUN] 任务开始。";
            if (StartsWithTag(message, "[LOG]")) return "[LOG] 本地日志已创建。";
            if (message.IndexOf("[OK] 登录成功", StringComparison.OrdinalIgnoreCase) >= 0) return "[OK] 登录成功。";
            if (StartsWithTag(message, "[DOWNLOAD]")) return "[DOWNLOAD] 文件处理完成。";
            if (StartsWithTag(message, "[RETRY]")) return "[RETRY] 网络波动，正在自动重试。";

            if (StartsWithTag(message, "[hotswap] still working"))
                return "[hotswap] still working... elapsed=" + ExtractLogValue(message, "elapsed=", "00:00:00");
            if (StartsWithTag(message, "[hotswap]"))
                return "[UPLOAD] 本地处理进行中。";

            // 世界上传相关: 一律转为不含 id/技术细节的干净文案(优先于下面的 avatar 映射)
            if (message.IndexOf("生成新 world id", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 创建上传任务...";
            if (message.IndexOf("WORLD_UPLOAD_OK", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 上传记录已完成。";
            if (StartsWithTag(message, "[SIGN]"))
                return "[UPLOAD] 本地处理资源...";
            if (StartsWithTag(message, "[CONFIRM]"))
                return "[UPLOAD] 授权确认中...";
            if (message.IndexOf("上传世界 bundle", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 上传模型文件...";
            if (message.IndexOf("创建世界记录", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 更新上传信息...";

            if (message.IndexOf("使用 bundle UnityVersion", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 读取资源信息...";
            if (message.IndexOf("创建 avatar 记录", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 创建上传任务...";
            if (message.IndexOf("预留 avatar id:", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 已创建上传任务。";
            if (message.IndexOf("改写 bundle", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 本地处理资源...";
            if (message.IndexOf("改写完成", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 本地处理完成。";
            if (message.IndexOf("上传 avatar bundle", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 上传模型文件...";
            if (message.IndexOf("上传封面图片", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 上传封面图片...";
            if (message.IndexOf("更新 avatar 元数据", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 更新上传信息...";
            if (message.IndexOf("DIRECT_UPLOAD_OK", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[UPLOAD] 上传记录已完成。";

            if (message.IndexOf("[SECURITY] status=", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[SECURITY] 服务器安全分析完成。";
            if (message.IndexOf("[SECURITY]", StringComparison.OrdinalIgnoreCase) >= 0)
                return message.IndexOf("等待", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "[SECURITY] 等待服务器安全分析..."
                    : "[SECURITY] 正在进行服务器安全分析...";

            if (message.IndexOf("[FILE] 创建文件记录", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[FILE] 准备上传文件...";
            if (message.IndexOf("[FILE] 上传文件数据", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[FILE] 上传文件数据...";
            if (message.IndexOf("[FILE] 上传签名数据", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[FILE] 上传校验数据...";
            if (message.IndexOf("[FILE] 等待处理", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[FILE] 等待服务器处理...";
            if (message.IndexOf("[FILE]", StringComparison.OrdinalIgnoreCase) >= 0 &&
                message.IndexOf("part ", StringComparison.OrdinalIgnoreCase) >= 0)
                return message.IndexOf("signature", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "[FILE] 上传校验数据 part " + ExtractPartPair(message) + "..."
                    : "[FILE] 上传数据 part " + ExtractPartPair(message) + "...";
            if (message.IndexOf("[FILE]", StringComparison.OrdinalIgnoreCase) >= 0 &&
                message.IndexOf("分块大小", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[FILE] 准备分块上传...";
            if (message.IndexOf("[FILE]", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (message.IndexOf("上传完成", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 message.IndexOf("分块上传完成", StringComparison.OrdinalIgnoreCase) >= 0))
                return "[FILE] 文件上传完成。";

            return RedactPublicText(message);
        }

        static bool StartsWithTag(string message, string tag)
        {
            return message != null && message.StartsWith(tag, StringComparison.OrdinalIgnoreCase);
        }

        static string ExtractLogValue(string text, string key, string fallback)
        {
            var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return fallback;
            index += key.Length;
            var end = index;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
                end++;
            var value = text.Substring(index, end - index).Trim();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        static string ExtractPartPair(string text)
        {
            var marker = " part ";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return "1/1";
            index += marker.Length;
            var end = index;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '/'))
                end++;
            var value = text.Substring(index, end - index).Trim();
            return string.IsNullOrEmpty(value) ? "1/1" : value;
        }

        static string RedactPublicText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var result = text;
            result = RedactToken(result, "avtr_");
            result = RedactToken(result, "wrld_");
            result = RedactToken(result, "file_");
            result = RedactToken(result, "usr_");
            result = RedactWindowsPaths(result);
            result = RedactUrlDetails(result);
            return result;
        }

        static string RedactToken(string text, string prefix)
        {
            var result = text;
            var start = 0;
            while (true)
            {
                var index = result.IndexOf(prefix, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var end = index + prefix.Length;
                while (end < result.Length && IsTokenChar(result[end]))
                    end++;
                result = result.Substring(0, index) + prefix + "***" + result.Substring(end);
                start = index + prefix.Length + 3;
            }
            return result;
        }

        static bool IsTokenChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '-' || c == '_';
        }

        static string RedactWindowsPaths(string text)
        {
            var result = text;
            for (var i = 0; i + 2 < result.Length; i++)
            {
                if (!char.IsLetter(result[i]) || result[i + 1] != ':' || (result[i + 2] != '\\' && result[i + 2] != '/'))
                    continue;

                var end = i + 3;
                while (end < result.Length && !char.IsWhiteSpace(result[end]) && result[end] != ')' && result[end] != '"' && result[end] != '\'')
                    end++;
                result = result.Substring(0, i) + "<local-path>" + result.Substring(end);
                i += "<local-path>".Length;
            }
            return result;
        }

        static string RedactUrlDetails(string text)
        {
            var result = text;
            result = ReplaceAfter(result, "url=", "<url>");
            result = ReplaceAfter(result, "endpoint=", "<endpoint>");
            return result;
        }

        static string ReplaceAfter(string text, string key, string replacement)
        {
            var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return text;
            index += key.Length;
            var end = index;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
                end++;
            return text.Substring(0, index) + replacement + text.Substring(end);
        }

        static bool IsDebugLogEnabled(string baseDir) { return true; }

        static bool HasDebugLogMarker(string dir)
        {
            try
            {
                return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, DebugLogMarkerFile));
            }
            catch { return false; }
        }

        static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        static void TryDeleteWithRetry(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch
                {
                    Thread.Sleep(200 * (attempt + 1));
                }
            }
        }

        static void CleanupHotswapSidecars(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath)) return;
            try
            {
                var fullPath = Path.GetFullPath(outputPath);
                var directory = Path.GetDirectoryName(fullPath);
                var fileName = Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory))
                    return;
                var prefix = fullPath + ".";
                foreach (var path in Directory.GetFiles(directory, fileName + ".*", SearchOption.TopDirectoryOnly))
                {
                    var candidate = Path.GetFullPath(path);
                    if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        TryDeleteWithRetry(candidate);
                }
            }
            catch { }
        }

        static void CleanupOldUploadArtifactsOnce()
        {
            var now = DateTime.UtcNow;
            var lastTicks = Interlocked.Read(ref _lastTempCleanupUtcTicks);
            if (lastTicks > 0)
            {
                var last = new DateTime(lastTicks, DateTimeKind.Utc);
                if (now - last < TimeSpan.FromMinutes(15))
                    return;
            }
            Interlocked.Exchange(ref _lastTempCleanupUtcTicks, now.Ticks);
            CleanupOldUploadArtifacts(TimeSpan.FromHours(GetTempCleanupAgeHours()));
        }

        void CleanupRuntimeCacheOnce(bool force)
        {
            var now = DateTime.UtcNow;
            var lastTicks = Interlocked.Read(ref _lastRuntimeCleanupUtcTicks);
            if (!force && lastTicks > 0)
            {
                var last = new DateTime(lastTicks, DateTimeKind.Utc);
                if (now - last < TimeSpan.FromHours(1))
                    return;
            }
            Interlocked.Exchange(ref _lastRuntimeCleanupUtcTicks, now.Ticks);
            CleanupRuntimeCache(TimeSpan.FromDays(GetRuntimeCleanupDays()));
        }

        static int GetTempCleanupAgeHours()
        {
            var raw = Environment.GetEnvironmentVariable(TempCleanupAgeHoursEnv);
            int hours;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw.Trim(), out hours))
            {
                if (hours < 1) return 1;
                if (hours > 168) return 168;
                return hours;
            }
            return 2;
        }

        static int GetRuntimeCleanupDays()
        {
            var raw = Environment.GetEnvironmentVariable(RuntimeCleanupDaysEnv);
            int days;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw.Trim(), out days))
            {
                if (days < 1) return 1;
                if (days > 30) return 30;
                return days;
            }
            return 3;
        }

        static void CleanupOldUploadArtifacts(TimeSpan minAge)
        {
            try
            {
                var tempDir = Path.GetTempPath();
                if (string.IsNullOrEmpty(tempDir) || !Directory.Exists(tempDir)) return;
                var cutoffUtc = DateTime.UtcNow - minAge;
                var files = Directory.GetFiles(tempDir, "vrca*.*", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    try
                    {
                        if (!IsOwnedTempUploadArtifact(file)) continue;
                        if (IsActiveTempArtifact(file)) continue;
                        if (File.GetLastWriteTimeUtc(file) > cutoffUtc) continue;
                        TryDeleteWithRetry(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        void CleanupRuntimeCache(TimeSpan minAge)
        {
            try
            {
                if (string.IsNullOrEmpty(_runtimeDir) || !Directory.Exists(_runtimeDir)) return;
                var cutoffUtc = DateTime.UtcNow - minAge;
                CleanupOldFilesInDirectory(Path.Combine(_runtimeDir, "logs"), "*.*", cutoffUtc, _logFilePath, SearchOption.TopDirectoryOnly);
                CleanupOldFilesInDirectory(Path.Combine(_runtimeDir, "preflight-reports"), "*.*", cutoffUtc, "", SearchOption.TopDirectoryOnly);
                CleanupOldFilesInDirectory(Path.Combine(_runtimeDir, "debug"), "*.*", cutoffUtc, "", SearchOption.AllDirectories);
                CleanupOldFilesInDirectory(Path.Combine(_runtimeDir, "recovery-tests"), "*.*", cutoffUtc, "", SearchOption.AllDirectories);
                CleanupOldFilesInDirectory(Path.Combine(_runtimeDir, "prepared-worlds"), "*.*", cutoffUtc, "", SearchOption.TopDirectoryOnly);
            }
            catch { }
        }

        void EnforcePreparedWorldCacheQuota(string keepPath)
        {
            try
            {
                var now = DateTime.UtcNow;
                var lastTicks = Interlocked.Read(ref _lastPreparedCacheQuotaUtcTicks);
                if (string.IsNullOrEmpty(keepPath) && lastTicks > 0 &&
                    now - new DateTime(lastTicks, DateTimeKind.Utc) < TimeSpan.FromHours(1))
                    return;
                Interlocked.Exchange(ref _lastPreparedCacheQuotaUtcTicks, now.Ticks);

                var dir = Path.Combine(_runtimeDir, "prepared-worlds");
                if (!Directory.Exists(dir)) return;

                var maxBytes = (long)GetEnvInt(PreparedCacheMaxGbEnv, 4) * 1024L * 1024L * 1024L;
                var files = Directory.GetFiles(dir, "*.vrcw", SearchOption.TopDirectoryOnly);
                Array.Sort(files, delegate(string left, string right)
                {
                    return File.GetLastWriteTimeUtc(left).CompareTo(File.GetLastWriteTimeUtc(right));
                });

                long total = 0;
                foreach (var file in files)
                {
                    try { total += new FileInfo(file).Length; } catch { }
                }

                var keepFull = FullPathOrEmpty(keepPath);
                foreach (var file in files)
                {
                    if (total <= maxBytes) break;
                    var full = FullPathOrEmpty(file);
                    if (!string.IsNullOrEmpty(keepFull) &&
                        string.Equals(full, keepFull, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (IsActiveTempArtifact(full)) continue;

                    long length = 0;
                    try { length = new FileInfo(file).Length; } catch { }
                    TryDeleteWithRetry(file);
                    if (!File.Exists(file)) total = Math.Max(0, total - length);
                }
            }
            catch { }
        }

        static void CleanupOldFilesInDirectory(string dir, string pattern, DateTime cutoffUtc, string skipPath, SearchOption searchOption)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var skipFull = FullPathOrEmpty(skipPath);
                var files = Directory.GetFiles(dir, pattern, searchOption);
                foreach (var file in files)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(skipFull) &&
                            string.Equals(FullPathOrEmpty(file), skipFull, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (File.GetLastWriteTimeUtc(file) > cutoffUtc) continue;
                        TryDeleteWithRetry(file);
                    }
                    catch { }
                }
                DeleteEmptyDirectories(dir);
            }
            catch { }
        }

        static void DeleteEmptyDirectories(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var dirs = Directory.GetDirectories(dir, "*", SearchOption.AllDirectories);
                Array.Sort(dirs);
                Array.Reverse(dirs);
                foreach (var child in dirs)
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(child).Length == 0)
                            Directory.Delete(child);
                    }
                    catch { }
                }
            }
            catch { }
        }

        static void RegisterActiveTempArtifact(string path)
        {
            var full = FullPathOrEmpty(path);
            if (string.IsNullOrEmpty(full)) return;
            lock (_activeTempArtifactsLock)
                _activeTempArtifacts.Add(full);
        }

        static void UnregisterActiveTempArtifact(string path)
        {
            var full = FullPathOrEmpty(path);
            if (string.IsNullOrEmpty(full)) return;
            lock (_activeTempArtifactsLock)
                _activeTempArtifacts.Remove(full);
        }

        static bool IsActiveTempArtifact(string path)
        {
            var full = FullPathOrEmpty(path);
            if (string.IsNullOrEmpty(full)) return false;
            lock (_activeTempArtifactsLock)
                return _activeTempArtifacts.Contains(full);
        }

        static string FullPathOrEmpty(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? "" : Path.GetFullPath(path);
            }
            catch
            {
                return "";
            }
        }

        static bool IsOwnedTempUploadArtifact(string path)
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("vrca_sig_", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
                return LooksLikeGuidN(name.Substring("vrca_sig_".Length, name.Length - "vrca_sig_".Length - ".sig".Length));
            if (name.StartsWith("vrca_restore_", StringComparison.OrdinalIgnoreCase))
            {
                var token = StripOwnedTempExtension(name.Substring("vrca_restore_".Length));
                return LooksLikeGuidN(token);
            }
            if (name.StartsWith("vrca_", StringComparison.OrdinalIgnoreCase))
            {
                var remainder = name.Substring("vrca_".Length);
                if (remainder.Length >= 32 && LooksLikeGuidN(remainder.Substring(0, 32)) &&
                    IsOwnedHotswapSuffix(remainder.Substring(32)))
                    return true;
                var token = StripOwnedTempExtension(remainder);
                return LooksLikeGuidN(token);
            }
            return false;
        }

        static bool IsOwnedHotswapSuffix(string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return false;
            return suffix.Equals(".vrca", StringComparison.OrdinalIgnoreCase) ||
                   suffix.Equals(".vrcw", StringComparison.OrdinalIgnoreCase) ||
                   suffix.StartsWith(".vrca.raw", StringComparison.OrdinalIgnoreCase) ||
                   suffix.StartsWith(".vrcw.raw", StringComparison.OrdinalIgnoreCase) ||
                   suffix.StartsWith(".vrca.source-unpacked", StringComparison.OrdinalIgnoreCase) ||
                   suffix.StartsWith(".vrcw.source-unpacked", StringComparison.OrdinalIgnoreCase) ||
                   suffix.StartsWith(".vrca.lzma-", StringComparison.OrdinalIgnoreCase) ||
                   suffix.StartsWith(".vrcw.lzma-", StringComparison.OrdinalIgnoreCase) ||
                   suffix.StartsWith(".vrca.packing-", StringComparison.OrdinalIgnoreCase) ||
                   suffix.StartsWith(".vrcw.packing-", StringComparison.OrdinalIgnoreCase);
        }

        static string StripOwnedTempExtension(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.EndsWith(".vrca.raw", StringComparison.OrdinalIgnoreCase))
                return value.Substring(0, value.Length - ".vrca.raw".Length);
            if (value.EndsWith(".vrcw.raw", StringComparison.OrdinalIgnoreCase))
                return value.Substring(0, value.Length - ".vrcw.raw".Length);
            if (value.EndsWith(".vrca", StringComparison.OrdinalIgnoreCase))
                return value.Substring(0, value.Length - ".vrca".Length);
            if (value.EndsWith(".vrcw", StringComparison.OrdinalIgnoreCase))
                return value.Substring(0, value.Length - ".vrcw".Length);
            if (value.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
                return value.Substring(0, value.Length - ".raw".Length);
            return value;
        }

        static bool LooksLikeGuidN(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32) return false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var hex = (c >= '0' && c <= '9') ||
                          (c >= 'a' && c <= 'f') ||
                          (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        static string Quote(string s) { return "\"" + (s == null ? "" : s.Replace("\"", "\\\"")) + "\""; }

        // =====================================================================
        //  公开类型
        // =====================================================================
        public class LoginResult
        {
            public bool Success;
            public bool Needs2FA;
            public string TwoFactorType;
            public string DisplayName;
            public string UserId;
            public string Error;
        }

        public class UploadRequest
        {
            public string VrcaPath;
            public string ImagePath;
            public string Name;
            public string Description;
            public string ReleaseStatus;
        }

        public class UploadResult
        {
            public string AvatarId;
            public string BundleFileId;
            public bool ServerSecurityChecked;
            public int SecurityStatusCode;
            public string SecurityAnalysis;
            public string SecurityMessage;
            public string BundleFileStatus;
            public string SignatureStatus;
        }

        public class VrcaFileEntry
        {
            public string Id;
            public string Name;
            public string Extension;
            public int Version;
            public string CreatedAt;
            public string UpdatedAt;
            public string Url;
            public long SizeBytes;
            public string AvatarId;
            public string AvatarName;
            public string Description;
            public string ReleaseStatus;
            public string ImageUrl;
            public string ThumbnailImageUrl;
        }

        sealed class AvatarDownloadMetadata
        {
            public string AvatarId;
            public string AvatarName;
            public string Description;
            public string ReleaseStatus;
            public string ImageUrl;
            public string ThumbnailImageUrl;
        }

        public class PreflightResult
        {
            public int ErrorCount;
            public int RepairableCount;
            public int BuildRequiredCount;
            public int WarningCount;
            public int InfoCount;
            public string ReportPath;
            public List<PreflightItem> Items;
        }

        public class PreflightItem
        {
            public string Level;
            public string Code;
            public string Message;
        }

        public class AvatarInspectResult
        {
            public string Id;
            public string Name;
            public string AuthorId;
            public string ReleaseStatus;
            public string ImageUrl;
            public string UnityPackageUrl;
            public string ActiveAssetReviewId;
            public string PendingUpload;
            public int Version;
            public List<UnityPackageInspect> UnityPackages;
            public List<FileInspect> FileStatuses;
        }

        public class UnityPackageInspect
        {
            public string Id;
            public string AssetUrl;
            public string UnityVersion;
            public string Platform;
            public string Variant;
            public int AssetVersion;
            public string FileId;
        }

        public class FileInspect
        {
            public string Id;
            public string Name;
            public string Extension;
            public int Version;
            public string FileStatus;
            public string SignatureStatus;
            public int FileSizeInBytes;
            public int SignatureSizeInBytes;
            public string FileUrlPresent;
            public string SignatureUrlPresent;
            public int SecurityAnalysisStatusCode;
            public string SecurityAnalysis;
            public string SecurityAnalysisMessage;
        }

        public class NotLoggedInException : Exception
        {
            public NotLoggedInException() : base("未登录或登录已过期，请先登录。") { }
        }

        public class ServerSecurityException : Exception
        {
            public UploadResult Result { get; private set; }

            public ServerSecurityException(UploadResult result)
                : base(BuildMessage(result))
            {
                Result = result;
            }

            static string BuildMessage(UploadResult result)
            {
                if (result == null)
                    return "服务器安全分析失败。";
                return "服务器安全分析失败(File forbidden)，VRChat 客户端不会下载这个 Avatar。\n" +
                       "Avatar ID: " + (result.AvatarId ?? "") + "\n" +
                       "File ID: " + (result.BundleFileId ?? "") + "\n" +
                       "状态: HTTP " + result.SecurityStatusCode + " / " + (result.SecurityAnalysis ?? "") + "\n" +
                       "说明: API 上传和文件完成不等于游戏内可用；当前 bundle 没有通过 VRChat 服务器安全分析，需要继续对比官方 SDK 构建产物。";
            }
        }

        class HttpResult
        {
            public int StatusCode;
            public string Text;
            public WebHeaderCollection Headers;
        }

        public class ApiException : Exception
        {
            public int StatusCode;
            public string Body;
            public ApiException(int statusCode, string body, string label)
                : base("API " + label + " 失败: HTTP " + statusCode) { StatusCode = statusCode; Body = body; }
        }

        public class AuthCache
        {
            public string Username { get; set; }
            public string DisplayName { get; set; }
            public string UserId { get; set; }
            public string Auth { get; set; }
            public string TwoFactorAuth { get; set; }
            public string DeviceId { get; set; }
            public string SavedAtUtc { get; set; }
        }

        public class WorldUploadRequest
        {
            public string VrcwPath;
            public string ImagePath;
            public string ExistingImageUrl;
            public string WorldSignature;
            public string Name;
            public string Description;
            public string ReleaseStatus;
            public string Platform;
            public string WorldId; // 保留字段(当前流程始终生成新 wrld_id)
            public int? HotswapCompression;
        }

        public class WorldUploadResult
        {
            public string WorldId;
            public string BundleFileId;
            public bool CreatedNew;
            public int SecurityStatusCode;
            public string SecurityAnalysis;
            public string ImageUrl;
            public string Platform;
        }

        public class WorldRecordUpdateRequest
        {
            public string WorldId;
            public string BundleFileId;
            public string ImageFileId;
            public string Name;
            public string Description;
            public string ReleaseStatus;
            public string UnityVersion;
            public string WorldSignature;
        }

        public class WorldPreflightResult
        {
            public int ErrorCount;
            public int BuildRequiredCount;
            public int WarningCount;
            public string ReportPath;
            public string BundleHeaderLine;
            public string WorldId;
            public int TargetPlatform;
        }
    }
}
