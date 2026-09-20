using BenchmarkDotNet.Attributes;
using GxMcp.Gateway;

namespace GxMcp.Benchmarks
{
    [MemoryDiagnoser]
    public class DidYouMeanBenchmark
    {
        private static readonly string[] _toolCandidates = new[]
        {
            "genexus_query", "genexus_read", "genexus_edit", "genexus_inspect",
            "genexus_analyze", "genexus_lifecycle", "genexus_sql", "genexus_kb",
            "genexus_create", "genexus_refactor", "genexus_variable", "genexus_format",
            "genexus_properties", "genexus_asset", "genexus_history", "genexus_structure",
            "genexus_doc", "genexus_whoami", "genexus_gam", "genexus_wwp"
        };

        [Benchmark]
        public string? SuggestCloseMatch()
        {
            return DidYouMean.Suggest("genexus_querry", _toolCandidates, 2);
        }

        [Benchmark]
        public string? SuggestNoMatch()
        {
            return DidYouMean.Suggest("completely_unknown_action_name", _toolCandidates, 2);
        }

        [Benchmark]
        public int LevenshteinDistance()
        {
            return DidYouMean.Levenshtein("genexus_querry", "genexus_query");
        }
    }
}
