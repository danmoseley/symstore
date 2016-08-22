// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.SymbolStore.Client
{
    // Note that as an implementation detail, all async methods are equivalent in SymbolStore.Client,
    // so testing the non-async version of methods you end up hitting both code paths.
    public partial class Tests
    {
        const string PEFileName = "clr.dll";
        const int PEFileSize = 0x00965000;
        const int PEFileTimestamp = 0x4ba21eeb;

        const string PDBFileName = "clr.pdb";
        static Guid PDBGuid = new Guid("0a821b8a-573e-4289-9202-851df6a539f1");
        const int PDBAge = 2;

        [Fact]
        public async Task TestKnownPdbDownload()
        {
            // We should always be able to find 4.0 RTM on the public symbol server.
            SymbolStore server = CreateMicrosoftSymbolServer();
            SearchResult result = await server.FindPdbAsync(PDBFileName, PDBGuid, PDBAge);
            
            Assert.NotNull(result);
        }

        [Fact]
        public async Task TestKnownPEFileDownload()
        {
            // We should always be able to find 4.0 RTM on the public symbol server.
            SymbolStore server = CreateMicrosoftSymbolServer();
            SearchResult result = await server.FindPEFileAsync(PEFileName, PEFileTimestamp, PEFileSize);
            Assert.NotNull(result);
        }

        private SymbolStore CreateMicrosoftSymbolServer()
        {
            return new MicrosoftSymbolServerClient("http://msdl.microsoft.com/download/symbols");
        }

        [Fact]
        public async Task SymbolCacheCanHandleMultipleFilesPerKey()
        {
            MockSymbolStore storeA = new MockSymbolStore("Mock1");
            storeA.AddContent("a/b/c", new byte[] { 1, 2, 3 });
            MockSymbolStore storeB = new MockSymbolStore("Mock2");
            storeB.AddContent("a/b/c", new byte[] { 4, 5, 6 });

            string tempStorePath = Path.Combine(Path.GetTempPath(), "test_symbol_cache_" + DateTime.Now.ToString("yyyy\\_MM\\_dd\\_hh\\_mm\\_ss\\_ffff"));
            try
            {
                SymbolStoreFileCache cacheA = new SymbolStoreFileCache(tempStorePath, storeA);
                SymbolStoreFileCache cacheB = new SymbolStoreFileCache(tempStorePath, storeB);
                using (Stream a = await (await cacheA.FindFile("a/b/c")).GetStreamAsync())
                {
                    Assert.Equal(1, a.ReadByte());
                }
                using (Stream a = await (await cacheB.FindFile("a/b/c")).GetStreamAsync())
                {
                    Assert.Equal(4, a.ReadByte());
                }
                using (Stream a = await (await cacheA.FindFile("a/b/c")).GetStreamAsync())
                {
                    Assert.Equal(1, a.ReadByte());
                }
            }
            finally
            {
                Directory.Delete(tempStorePath, true);
            }
        }

        [Fact]
        public async Task SymbolClientProvidesDiagnostics()
        {
            // We should always be able to find 4.0 RTM on the public symbol server.
            SymbolStore server = CreateMicrosoftSymbolServer();
            SearchResult result = await server.FindPEFileAsync(PEFileName, PEFileTimestamp, PEFileSize);

            Assert.NotNull(result.Diagnostics);
            //Hopefully this is incredibly generous, if the test runs so slowly that it takes more than an hour
            //something is not working properly. Typically this would be measured in ms or sec.
            Assert.True(DateTimeOffset.Now - result.Diagnostics.QueryTime < TimeSpan.FromHours(1));
            Assert.Equal("http://msdl.microsoft.com/download/symbols", result.Diagnostics.SymbolStoreName);
            Assert.Equal("http://msdl.microsoft.com/download/symbols/clr.dll/4ba21eeb965000/clr.dll", result.Diagnostics.FilePath);
            Assert.Equal(SearchOutcome.Success, result.Diagnostics.Outcome);
        }

        [Fact]
        public async Task SymbolCacheProvidesDiagnostics()
        {
            MockSymbolStore storeA = new MockSymbolStore("Mock1");
            storeA.AddContent("a/b/c", new byte[] { 1, 2, 3 });
            string tempStorePath = Path.Combine(Path.GetTempPath(), "test_symbol_cache_" + DateTime.Now.ToString("yyyy\\_MM\\_dd\\_hh\\_mm\\_ss\\_ffff"));

            try
            {
                SymbolStoreFileCache cache = new SymbolStoreFileCache(tempStorePath, storeA);
                SearchResult result = await cache.FindFile("a/b/c");
                Assert.NotNull(result);

                Assert.NotNull(result.Diagnostics);
                //Hopefully this is incredibly generous, if the test runs so slowly that it takes more than an hour
                //something is not working properly. Typically this would be measured in ms or sec.
                Assert.True(DateTimeOffset.Now - result.Diagnostics.QueryTime < TimeSpan.FromHours(1));
                Assert.Equal(tempStorePath, result.Diagnostics.SymbolStoreName);
                Assert.Equal(tempStorePath + "\\a\\b\\cf2da09ef5f2261e\\c", result.Diagnostics.FilePath);
                Assert.Equal(SearchOutcome.Success, result.Diagnostics.Outcome);
                Assert.NotNull(result.Diagnostics.UpstreamStoreDiagnostics);
                Assert.True(DateTimeOffset.Now - result.Diagnostics.UpstreamStoreDiagnostics.QueryTime < TimeSpan.FromHours(1));
                Assert.Equal("Mock1", result.Diagnostics.UpstreamStoreDiagnostics.SymbolStoreName);
                Assert.Equal("Mock1/a/b/c", result.Diagnostics.UpstreamStoreDiagnostics.FilePath);
                Assert.Equal(SearchOutcome.Success, result.Diagnostics.UpstreamStoreDiagnostics.Outcome);

                SearchResult result2 = await cache.FindFile("a/b/c");
                Assert.NotNull(result2);

                //The cache was queried at two different times, but because of caching upstream was only queried once.
                //The upstream query times should be identical
                Assert.NotEqual(result.Diagnostics.QueryTime, result2.Diagnostics.QueryTime);
                Assert.Equal(result.Diagnostics.UpstreamStoreDiagnostics.QueryTime, result.Diagnostics.UpstreamStoreDiagnostics.QueryTime);
            }
            finally
            {
                Directory.Delete(tempStorePath, true);
            }

        }

        public class MockSymbolStore : SymbolStore
        {
            public MockSymbolStore(string name) : base(name) { }

            Dictionary<string, byte[]> _keyToContent = new Dictionary<string, byte[]>();

            public override string GetFileIdentity(string key)
            {
                return Name + "/" + key;
            }

            public void AddContent(string key, byte[] content)
            {
                _keyToContent.Add(key, content);
            }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
            public override async Task<SearchResult> FindFile(string key, CancellationToken cancelToken = default(CancellationToken), CacheValidityPolicy cachePolicy = null)
#pragma warning restore CS1998
            {
                byte[] content;
                if(_keyToContent.TryGetValue(key, out content))
                {
                    return CreateResult(() => Task.FromResult<Stream>(new MemoryStream(content)),
                                        SearchOutcome.Success,
                                        GetFileIdentity(key),
                                        GetFileIdentity(key),
                                        DateTimeOffset.Now);
                }
                else
                {
                    return CreateResult(() => Task.FromResult<Stream>(null),
                                        SearchOutcome.NotFound,
                                        GetFileIdentity(key),
                                        GetFileIdentity(key),
                                        DateTimeOffset.Now);
                }
            }
        }
    }
}
