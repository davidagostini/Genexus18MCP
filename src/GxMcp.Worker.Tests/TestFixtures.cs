using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (Task 1.3): shared helpers for graph-related tests. Avoids
    // standing up a real KB by populating IndexCacheService via the
    // internal test seam LoadFromEntries.
    public static class TestFixtures
    {
        public static string FindRepoRoot()
        {
            string dir = System.AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir, "Genexus18MCP.sln")))
                    return dir;
                dir = System.IO.Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(dir)) break;
            }
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        }

        public class CallGraphFixture
        {
            public IndexCacheService Index;
        }

        // A simple A -> B -> C call chain.
        // A.Calls = [B]; B.Calls = [C]; C.CalledBy = [B]; B.CalledBy = [A].
        public static CallGraphFixture SmallCallGraph()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry {
                    Name = "A", Type = "Procedure",
                    Calls = new List<string> { "B" },
                    CalledBy = new List<string>(),
                    SourceSnippet = "B()"
                },
                new SearchIndex.IndexEntry {
                    Name = "B", Type = "Procedure",
                    Calls = new List<string> { "C" },
                    CalledBy = new List<string> { "A" },
                    SourceSnippet = "C()"
                },
                new SearchIndex.IndexEntry {
                    Name = "C", Type = "Procedure",
                    Calls = new List<string>(),
                    CalledBy = new List<string> { "B" },
                    SourceSnippet = ""
                }
            };
            var svc = new IndexCacheService();
            svc.LoadFromEntries(entries);
            return new CallGraphFixture { Index = svc };
        }

        // v2.3.8 (Task 2.2): folder/discovery fixture for ListDiscoveryTests.
        // Provides entries that exercise nameFilter / descriptionFilter / pathPrefix
        // independently:
        //   - "ComissaoLiberaPareceres": name contains "Libera" but description does NOT contain "pareceres".
        //   - "PSPContParecer": name lacks "Libera"; description contains "pareceres".
        //   - 2 entries under "Root Module/ClickSign/X" for pathPrefix coverage.
        public static CallGraphFixture IndexWithFolders()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry {
                    Name = "ComissaoLiberaPareceres", Type = "Procedure",
                    Description = "Liberar comissões",
                    ParentFolderPath = "Root Module/Comissao",
                    ParentPath = "Comissao",
                    Path = "Comissao/ComissaoLiberaPareceres"
                },
                new SearchIndex.IndexEntry {
                    Name = "PSPContParecer", Type = "Procedure",
                    Description = "Conta pareceres do processo seletivo",
                    ParentFolderPath = "Root Module/PSP",
                    ParentPath = "PSP",
                    Path = "PSP/PSPContParecer"
                },
                new SearchIndex.IndexEntry {
                    Name = "ClickSignSendDoc", Type = "Procedure",
                    Description = "Send doc to ClickSign",
                    ParentFolderPath = "Root Module/ClickSign/X",
                    ParentPath = "ClickSign/X",
                    Path = "ClickSign/X/ClickSignSendDoc"
                },
                new SearchIndex.IndexEntry {
                    Name = "ClickSignCallback", Type = "Procedure",
                    Description = "Callback handler",
                    ParentFolderPath = "Root Module/ClickSign/X",
                    ParentPath = "ClickSign/X",
                    Path = "ClickSign/X/ClickSignCallback"
                }
            };
            var svc = new IndexCacheService();
            svc.LoadFromEntries(entries);
            return new CallGraphFixture { Index = svc };
        }

        // v2.6.8: fixture for temporal sort/cursor/since tests. Five procedures
        // with distinct LastUpdate timestamps spanning 30 days, plus a sixth
        // with no timestamp (sentinel MinValue) to exercise the skip-on-emit
        // path. Authors are split 4/2 to make by_author aggregate non-trivial.
        public static CallGraphFixture IndexWithLifecycle()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry {
                    Guid = "00000000-0000-0000-0000-000000000001",
                    Name = "RecentMost", Type = "Procedure", Description = "newest",
                    LastUpdate = new System.DateTime(2026, 5, 22, 12, 0, 0, System.DateTimeKind.Utc),
                    CreatedAt = new System.DateTime(2026, 5, 1, 0, 0, 0, System.DateTimeKind.Utc),
                    LastModifiedBy = "alice"
                },
                new SearchIndex.IndexEntry {
                    Guid = "00000000-0000-0000-0000-000000000002",
                    Name = "Recent2", Type = "Procedure", Description = "second-newest",
                    LastUpdate = new System.DateTime(2026, 5, 20, 12, 0, 0, System.DateTimeKind.Utc),
                    LastModifiedBy = "alice"
                },
                new SearchIndex.IndexEntry {
                    Guid = "00000000-0000-0000-0000-000000000003",
                    Name = "Mid", Type = "Procedure", Description = "two weeks back",
                    LastUpdate = new System.DateTime(2026, 5, 10, 12, 0, 0, System.DateTimeKind.Utc),
                    LastModifiedBy = "bob"
                },
                new SearchIndex.IndexEntry {
                    Guid = "00000000-0000-0000-0000-000000000004",
                    Name = "Older", Type = "Procedure", Description = "month back",
                    LastUpdate = new System.DateTime(2026, 4, 22, 12, 0, 0, System.DateTimeKind.Utc),
                    LastModifiedBy = "alice"
                },
                new SearchIndex.IndexEntry {
                    Guid = "00000000-0000-0000-0000-000000000005",
                    Name = "Eldest", Type = "Procedure", Description = "two months back",
                    LastUpdate = new System.DateTime(2026, 3, 22, 12, 0, 0, System.DateTimeKind.Utc),
                    LastModifiedBy = "bob"
                },
                new SearchIndex.IndexEntry {
                    Guid = "00000000-0000-0000-0000-000000000006",
                    Name = "Untouched", Type = "Procedure", Description = "no lifecycle data",
                    // LastUpdate left at default(DateTime) = MinValue
                    LastModifiedBy = null
                }
            };
            var svc = new IndexCacheService();
            svc.LoadFromEntries(entries);
            return new CallGraphFixture { Index = svc };
        }

        // Linear chain N0 -> N1 -> N2 -> ... -> N{depth-1}.
        // Root = "N0". Each entry's Calls points to the next node only.
        public static CallGraphFixture LargeCallChain(int depth)
        {
            var entries = new List<SearchIndex.IndexEntry>();
            for (int i = 0; i < depth; i++)
            {
                var e = new SearchIndex.IndexEntry {
                    Name = "N" + i,
                    Type = "Procedure",
                    Calls = new List<string>(),
                    CalledBy = new List<string>(),
                    SourceSnippet = ""
                };
                if (i < depth - 1) e.Calls.Add("N" + (i + 1));
                if (i > 0) e.CalledBy.Add("N" + (i - 1));
                entries.Add(e);
            }
            var svc = new IndexCacheService();
            svc.LoadFromEntries(entries);
            return new CallGraphFixture { Index = svc };
        }
    }
}
