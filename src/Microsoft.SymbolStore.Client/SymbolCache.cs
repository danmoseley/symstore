using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.SymbolStore.Client
{
    public abstract class SymbolStoreFileCacheBase : SymbolStore
    {
        public static string DefaultSymbolCacheLocation
        {
            get
            {
                return Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "Symbols");
            }
        }

        public SymbolStoreFileCacheBase(SymbolStore upstreamStore) : this(DefaultSymbolCacheLocation, upstreamStore)
        {
            UpstreamStore = upstreamStore;
        }
        public SymbolStoreFileCacheBase(string path, SymbolStore upstreamStore) : base(path)
        {
            UpstreamStore = upstreamStore;
        }

        public SymbolStore UpstreamStore { get; private set; }

        public virtual async Task AddFile(string key, Stream contents)
        {
            string tempFile = Path.GetTempFileName();
            string finalPath = GetCacheLookupPath(key);
            using (Stream output = File.OpenWrite(tempFile))
            {
                await contents.CopyToAsync(output);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
            File.Move(tempFile, finalPath);
        }

        public override async Task<SearchResult> FindFile(string key, CancellationToken cancelToken = default(CancellationToken), CacheValidityPolicy cachePolicy = null)
        {
            string path = GetCacheLookupPath(key);
            DateTimeOffset queryTime = DateTimeOffset.Now;
            if (File.Exists(path))
            {
                CacheFileInfo cacheInfo = LookupCacheInfo(key);
                return CreateFileExistsResult(path, cacheInfo.FileIdentity, queryTime, ConvertToDiagnostics(cacheInfo));
            }
            
            SearchResult result = await UpstreamStore.FindFile(key, cancelToken, cachePolicy);
            if (result.Outcome != SearchOutcome.Success)
            {
                return CreateFileNotFoundResult(path, path, queryTime, result.Diagnostics);
            }

            CacheFileInfo cacheFileInfo = CreateCacheInfo(key, result);
            using (Stream stream = await result.GetStreamAsync())
            {
                await AddCacheFileInfo(key, cacheFileInfo);
                await AddFile(key, stream);
            }

            return CreateFileExistsResult(path, path, queryTime, ConvertToDiagnostics(cacheFileInfo));
        }

        protected abstract string GetCacheLookupPath(string key);
        protected abstract CacheFileInfo LookupCacheInfo(string key);
        protected abstract CacheFileInfo CreateCacheInfo(string key, SearchResult result);
        protected abstract Task AddCacheFileInfo(string key, CacheFileInfo cacheFileInfo);

        protected CacheFileInfo ConvertToCacheInfo(SearchResult result)
        {
            List<CacheFileUpstreamQueryInfo> queryInfos = new List<CacheFileUpstreamQueryInfo>();
            ConvertToQueryInfoList(result.Diagnostics, queryInfos);
            return new CacheFileInfo(result.FileIdentity, queryInfos.ToArray());
        }

        private void ConvertToQueryInfoList(SearchDiagnostics diagnostics, List<CacheFileUpstreamQueryInfo> queryInfos)
        {
            queryInfos.Add(new CacheFileUpstreamQueryInfo(diagnostics.SymbolStoreName,
                                                          diagnostics.FilePath,
                                                          diagnostics.QueryTime));
            if (diagnostics.UpstreamStoreDiagnostics != null)
            {
                ConvertToQueryInfoList(diagnostics.UpstreamStoreDiagnostics, queryInfos);
            }
        }

        private SearchDiagnostics ConvertToDiagnostics(CacheFileInfo cacheInfo)
        {
            SearchDiagnostics cur = null;
            foreach(CacheFileUpstreamQueryInfo upstreamQuery in cacheInfo.UpstreamQueries.Reverse())
            {
                cur = new SearchDiagnostics(SearchOutcome.Success,
                                            upstreamQuery.FilePath,
                                            upstreamQuery.LastQueryTime,
                                            upstreamQuery.StoreName,
                                            cur);
            }
            return cur;
        }

        protected SearchResult CreateFileExistsResult(string filePath, 
                                                      string fileIdentity,
                                                      DateTimeOffset queryTime,
                                                      SearchDiagnostics upstreamDiagnostics = null)
        {
            return CreateResult(() => Task.Factory.StartNew(() => (Stream)File.OpenRead(filePath)),
                                SearchOutcome.Success,
                                fileIdentity,
                                filePath,
                                queryTime,
                                upstreamDiagnostics); 
                                
        }

        protected SearchResult CreateFileNotFoundResult(string filePath,
                                                        string fileIdentity,
                                                        DateTimeOffset queryTime,
                                                        SearchDiagnostics upstreamDiagnostics = null)
        {
            return CreateResult(() => Task.FromResult<Stream>(null),
                                SearchOutcome.NotFound,
                                fileIdentity,
                                filePath,
                                queryTime,
                                upstreamDiagnostics);
        }
    }

    public class CacheFileUpstreamQueryInfo
    {
        public CacheFileUpstreamQueryInfo(string storeName, string filePath, DateTimeOffset lastQueryTime)
        {
            StoreName = storeName;
            FilePath = filePath;
            LastQueryTime = lastQueryTime;
        }
        public string StoreName { get; private set; }
        public string FilePath { get; private set; }
        public DateTimeOffset LastQueryTime { get; private set; }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Store: " + StoreName);
            sb.AppendLine("File Path: " + FilePath);
            sb.AppendLine("Last Query Time: " + LastQueryTime);
            return sb.ToString();
        }

        public static bool TryParse(string input, out CacheFileUpstreamQueryInfo queryInfo)
        {
            queryInfo = null;
            string[] lines = input.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != 3) return false;
            if (!lines[0].StartsWith("Store: ")) return false;
            if (!lines[1].StartsWith("File Path: ")) return false;
            if (!lines[2].StartsWith("Last Query Time: ")) return false;

            string upstreamStoreName = lines[0].Substring("Store: ".Length).TrimEnd();
            string filePath = lines[1].Substring("File Path: ".Length).TrimEnd();
            string lastQueryTimeText = lines[2].Substring("Last Query Time: ".Length).TrimEnd();
            DateTimeOffset lastQueryTime;
            if (!DateTimeOffset.TryParse(lastQueryTimeText, out lastQueryTime)) return false;
            queryInfo = new CacheFileUpstreamQueryInfo(upstreamStoreName, filePath, lastQueryTime);
            return true;
        }
    }

    public class CacheFileInfo
    {
        public CacheFileInfo(string fileIdentity, params CacheFileUpstreamQueryInfo[] upstreamQueries)
        {
            FileIdentity = fileIdentity;
            UpstreamQueries = upstreamQueries;
        }

        public string FileIdentity { get; private set; }
        public IEnumerable<CacheFileUpstreamQueryInfo> UpstreamQueries { get; private set; }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("File Identity: " + FileIdentity);
            foreach(CacheFileUpstreamQueryInfo query in UpstreamQueries)
            {
                sb.AppendLine(query.ToString());
            }
            return sb.ToString();
        }

        public static bool TryParse(string input, out CacheFileInfo cacheFileInfo)
        {
            cacheFileInfo = null;
            string[] lines = input.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 4) return false;
            if (!lines[0].StartsWith("File Identity: ")) return false;
            string fileIdentity = lines[0].Substring("File Identity: ".Length).TrimEnd();
            List<CacheFileUpstreamQueryInfo> queries = new List<CacheFileUpstreamQueryInfo>();
            for(int i = 1; i+3 < lines.Length; i+=4)
            {
                string queryInfoText = lines[i] + '\n' + lines[i + 1] + '\n' + lines[i + 2];
                CacheFileUpstreamQueryInfo queryInfo;
                if (!CacheFileUpstreamQueryInfo.TryParse(queryInfoText, out queryInfo)) return false;
                queries.Add(queryInfo);
            }

            cacheFileInfo = new CacheFileInfo(fileIdentity, queries.ToArray());
            return true;
        }
    }

    public class LegacySymbolStoreFileCache : SymbolStoreFileCacheBase
    {
        public LegacySymbolStoreFileCache(SymbolStore upstreamStore) : base(upstreamStore) { }
        public LegacySymbolStoreFileCache(string path, SymbolStore upstreamStore) : base(path, upstreamStore) { }

        public override string GetFileIdentity(string key)
        {
            //The legacy cache doesn't preserve the origin of files, the only identity is the location in the cache
            return GetCacheLookupPath(key);
        }

        protected override string GetCacheLookupPath(string key)
        {
            //TODO: make sure the key doesn't do sneaky things like include absolute paths or parent directory references
            string relativePath = key.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(Name, relativePath);
        }

        protected override Task AddCacheFileInfo(string key, CacheFileInfo cacheFileInfo)
        {
            return Task.CompletedTask;
        }

        protected override CacheFileInfo CreateCacheInfo(string key, SearchResult result)
        {
            return new CacheFileInfo(GetFileIdentity(key));
        }

        protected override CacheFileInfo LookupCacheInfo(string key)
        {
            return new CacheFileInfo(GetFileIdentity(key));
        }
    }

    public class SymbolStoreFileCache : SymbolStoreFileCacheBase
    {
        public SymbolStoreFileCache(SymbolStore upstreamStore) : this(DefaultSymbolCacheLocation, upstreamStore) { }

        public SymbolStoreFileCache(string path, SymbolStore upstreamStore) : base(path, upstreamStore) { }

        public override string GetFileIdentity(string key)
        {
            string identity = UpstreamStore.GetFileIdentity(key);
            if(identity == null)
            {
                return GetCacheLookupPath(key);
            }
            else
            {
                return identity;
            }
        }

        protected override Task AddCacheFileInfo(string key, CacheFileInfo cacheFileInfo)
        {
            string cacheInfoPath = GetDiagnosticsPathForKey(key);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheInfoPath));
            File.WriteAllText(GetDiagnosticsPathForKey(key), cacheFileInfo.ToString());
            return Task.CompletedTask;
        }

        protected override CacheFileInfo CreateCacheInfo(string key, SearchResult result)
        {
            return ConvertToCacheInfo(result);
        }

        protected override CacheFileInfo LookupCacheInfo(string key)
        {
            string text = File.ReadAllText(GetDiagnosticsPathForKey(key));
            CacheFileInfo cacheFileInfo = null;
            CacheFileInfo.TryParse(text, out cacheFileInfo);
            return cacheFileInfo;
        }

        private string ComputeFileIdentityDirName(string fileIdentity)
        {
            if(fileIdentity == null)
            {
                return null;
            }
            byte[] hashedBytes = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(fileIdentity)).Take(8).ToArray();
            return string.Concat(hashedBytes.Select(b => b.ToString("x2")));
        }

        private string GetDiagnosticsPathForKey(string key)
        {
            string contentPath = GetCacheLookupPath(key);
            return contentPath + ".cache_info";
        }

        protected override string GetCacheLookupPath(string key)
        {
            string identity = UpstreamStore.GetFileIdentity(key);
            return GetCacheLookupPath(key, ComputeFileIdentityDirName(identity));
        }

        private string GetCacheLookupPath(string key, string identityDirName)
        {
            //TODO: make sure the key doesn't do sneaky things like include absolute paths or parent directory references
            string relativePathKey = key.Replace('/', Path.DirectorySeparatorChar);
            if (identityDirName != null)
            {
                string keyDir = Path.Combine(Path.GetDirectoryName(relativePathKey), identityDirName);
                string keyFile = Path.GetFileName(relativePathKey);
                return Path.Combine(Name, keyDir, keyFile);
            }
            else
            {
                return Path.Combine(Name, relativePathKey);
            }
        }
    }
}
