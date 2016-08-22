using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.SymbolStore.Client
{
    public class CacheValidityPolicy
    {
        public TimeSpan UnreachableStatusValidityPeriod { get; set; }
        public TimeSpan FileResultValidityPeriod { get; set; }
    }

    public enum SearchOutcome
    {
        Success,
        NotFound,
        Unreachable
    }

    public class SearchResult
    {
        public SearchResult(Func<Task<Stream>> getStream, string fileIdentity, SearchDiagnostics diagnostics)
        {
            _getStream = getStream;
            FileIdentity = fileIdentity;
            Diagnostics = diagnostics;
        }

        Func<Task<Stream>> _getStream;

        public Task<Stream> GetStreamAsync() { return _getStream(); }
        public string FileIdentity { get; }
        public SearchOutcome Outcome { get { return Diagnostics.Outcome; } }
        public SearchDiagnostics Diagnostics { get; }
    }

    public class SearchDiagnostics
    {
        public SearchDiagnostics(SearchOutcome outcome,
                                 string filePath,
                                 DateTimeOffset queryTime,
                                 string symbolStoreName,
                                 SearchDiagnostics upstreamStoreDiagnostics)
        {
            Outcome = outcome;
            FilePath = filePath;
            QueryTime = queryTime;
            SymbolStoreName = symbolStoreName;
            UpstreamStoreDiagnostics = upstreamStoreDiagnostics;
        }
        public SearchOutcome Outcome { get; }
        public string FilePath { get; }
        public DateTimeOffset QueryTime { get; }
        public string SymbolStoreName { get; }
        public SearchDiagnostics UpstreamStoreDiagnostics { get; }
    }

    public abstract class SymbolStore
    {
        public SymbolStore(string name)
        {
            Name = name;
            DefaultCacheValidityPolicy = new CacheValidityPolicy()
            {
                UnreachableStatusValidityPeriod = TimeSpan.FromMinutes(5),
                FileResultValidityPeriod = TimeSpan.MaxValue
            };
        }

        public string Name { get; private set; }
        public CacheValidityPolicy DefaultCacheValidityPolicy { get; set; }

        public abstract string GetFileIdentity(string key);
        public abstract Task<SearchResult> FindFile(string key, CancellationToken cancelToken = default(CancellationToken), CacheValidityPolicy cachePolicy = null);

        protected SearchResult CreateResult(Func<Task<Stream>> getStream, SearchOutcome outcome, string fileIdentity, string path, DateTimeOffset queryTime, SearchDiagnostics upstreamDiagnostics = null)
        {
            return new SearchResult(getStream, fileIdentity, CreateDiagnostics(outcome, path, queryTime, upstreamDiagnostics));
        }
        protected SearchDiagnostics CreateDiagnostics(SearchOutcome outcome, string path, DateTimeOffset queryTime, SearchDiagnostics upstreamDiagnostics = null)
        {
            return new SearchDiagnostics(outcome, path, queryTime, Name, upstreamDiagnostics);
        }
    }

    public static class SymbolStoreCommonFileFormatExtensions
    {
        public static Task<SearchResult> FindPEFileAsync(this SymbolStore store, string filename, int buildTimeStamp, int imageSize, 
            CancellationToken cancelToken = default(CancellationToken), CacheValidityPolicy cachePolicy = null)
        {
            string key = StoreQueryBuilder.GetPEFileIndexPath(filename, buildTimeStamp, imageSize);
            return store.FindFile(key, cancelToken, cachePolicy);
        }

        public static Task<SearchResult> FindPdbAsync(this SymbolStore store, string pdbName, Guid guid, int age,
            CancellationToken cancelToken = default(CancellationToken), CacheValidityPolicy cachePolicy = null)
        {
            string key = StoreQueryBuilder.GetWindowsPdbQueryString(pdbName, guid, age);
            return store.FindFile(key, cancelToken, cachePolicy);
        }
    }

    public class UnionSymbolStore : SymbolStore
    {
        public UnionSymbolStore(params SymbolStore[] upstreamStores) : base("Union")
        {
            UpstreamStores = upstreamStores;
        }

        public IEnumerable<SymbolStore> UpstreamStores { get; private set; }

        public override string GetFileIdentity(string key)
        {
            //We can't predict which store is going to have the file when we search
            //so we have no useful identity
            return null;
        }

        public override async Task<SearchResult> FindFile(string key, CancellationToken cancelToken = default(CancellationToken), CacheValidityPolicy cachePolicy = null)
        {
            CancellationTokenSource cancelSource = new CancellationTokenSource();
            cancelToken.Register(() => cancelSource.Cancel());
            IEnumerable<Task<SearchResult>> searches = UpstreamStores.Select(store => store.FindFile(key, cancelSource.Token, cachePolicy));

            // when any search completes succesfully, trigger the cancelation of all others
            searches = searches.Select(t => t.ContinueWith(prevTask =>
            {
                if (prevTask.Result != null)
                {
                    cancelSource.Cancel();
                }
                return prevTask.Result;
            }));

            await Task.WhenAll(searches);
            return searches.Select(s => s.Result).Where(r => r != null).FirstOrDefault();
        }
    }

    public class SsqpSymbolServerClient : SymbolStore
    {
        public SsqpSymbolServerClient(string url) : base(url) { }

        public DateTimeOffset? LastUnreachableTime { get; private set; }

        public override string GetFileIdentity(string key)
        {
            return Name + "/" + key;
        }

        public override async Task<SearchResult> FindFile(string key, CancellationToken cancelToken, CacheValidityPolicy cachePolicy)
        {
            DateTimeOffset queryTime = DateTimeOffset.Now;
            string path = GetFileIdentity(key);
            if (LastUnreachableTime.HasValue && queryTime - LastUnreachableTime.Value < cachePolicy.UnreachableStatusValidityPeriod)
            {
                return CreateResult(() => Task.FromResult<Stream>(null), SearchOutcome.Unreachable, path, path, queryTime);
            }

            HttpClient client = new HttpClient();
            HttpResponseMessage response = await client.GetAsync(path, cancelToken);
            if (response.IsSuccessStatusCode)
            {
                return CreateResult(() => response.Content.ReadAsStreamAsync(), SearchOutcome.Success, path, path, queryTime);
            }
            else if(response.StatusCode != HttpStatusCode.NotFound)
            {
                LastUnreachableTime = queryTime;
                return CreateResult(() => Task.FromResult<Stream>(null), SearchOutcome.Unreachable, path, path, queryTime);
            }

            SearchResult result = await MakeAdditionalRequests(key, cancelToken, queryTime);
            return result ?? CreateResult(() => Task.FromResult<Stream>(null), SearchOutcome.NotFound, path, path, queryTime);
        }

        protected virtual Task<SearchResult> MakeAdditionalRequests(string key, CancellationToken cancelToken, DateTimeOffset queryTime)
        {
            return null;
        }
    }

    
    public class MicrosoftSymbolServerClient : SsqpSymbolServerClient
    {
        public MicrosoftSymbolServerClient(string name) : base(name) { }

        protected override async Task<SearchResult> MakeAdditionalRequests(string key, CancellationToken cancelToken, DateTimeOffset queryTime)
        {
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent","Microsoft-Symbol-Server/6.13.0009.1140");
            CancellationTokenSource cancelSource = new CancellationTokenSource();
            cancelToken.Register(() => cancelSource.Cancel());
            IEnumerable<Task<SearchResult>> searches = new Task<SearchResult>[]
            {
                FindViaFilePtr(client, key, cancelSource.Token, queryTime),
                FindViaCompressedPath(client, key, cancelSource.Token, queryTime)
            };

            // when any search completes succesfully, trigger the cancelation of all others
            searches = searches.Select(t => t.ContinueWith(prevTask => 
            {
                if (prevTask.Result != null)
                {
                    cancelSource.Cancel();
                }
                return prevTask.Result;
            }));  

            await Task.WhenAll(searches);
            return searches.Select(s => s.Result).Where(r => r != null).FirstOrDefault();
        }

        private async Task<SearchResult> FindViaCompressedPath(HttpClient client, string key, CancellationToken cancelToken, DateTimeOffset queryTime)
        {
            string compressedPath = Name + "/" + key.Substring(0, key.Length - 1) + "_";      
            HttpResponseMessage response = await client.GetAsync(compressedPath, cancelToken);
            if(response.IsSuccessStatusCode)
            {
                return CreateResult(async () =>
                {
                    using (Stream compressedStream = await response.Content.ReadAsStreamAsync())
                    {
                        return await new CabConverter(compressedStream).ConvertAsync();
                    }
                }
                , SearchOutcome.Success, GetFileIdentity(key), compressedPath, queryTime);
            }

            return null;
        }

        private async Task<SearchResult> FindViaFilePtr(HttpClient client, string key, CancellationToken cancelToken, DateTimeOffset queryTime)
        {
            int lastSlash = key.LastIndexOf('/');
            string filePtrPath = Name + "/" + key.Substring(0, lastSlash + 1) + "file.ptr";
            HttpResponseMessage response = await client.GetAsync(filePtrPath, cancelToken);
            if (response.IsSuccessStatusCode)
            {
                string fileData = await response.Content.ReadAsStringAsync();
                PtrFile ptrFile;
                if(PtrFile.TryParse(fileData, out ptrFile))
                {
                    try
                    {
                        string redirectPath = ptrFile.Path;
                        if (File.Exists(redirectPath))
                        {
                            return CreateResult(() => Task.Factory.StartNew(() => (Stream)File.OpenRead(redirectPath)),
                                SearchOutcome.Success, GetFileIdentity(key), redirectPath, queryTime);
                        }
                    }
                    catch (IOException) { }
                }
            }

            return null;
        }

        private class PtrFile
        {
            public static bool TryParse(string fileContents, out PtrFile file)
            {
                file = new PtrFile();
                if (fileContents.StartsWith("MSG: "))
                {
                    file.Message = fileContents.Substring(5);
                }
                else if (fileContents.StartsWith("PATH:"))
                {
                    file.Path = fileContents.Substring(5);
                }
                else
                {
                    file = null;
                    return false;
                }
                return true;
            }

            public string Message { get; private set; }
            public string Path { get; private set; }
        }
    }
}
