using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class BuildEngineTests
    {
        [Fact]
        public void ReorganizationAnalyzer_ClassifiesNonDestructiveStatements()
        {
            var analyzer = new ReorganizationAnalyzer();
            string sql = @"
                CREATE TABLE Customer (CustomerId INT NOT NULL, CustomerName VARCHAR(100));
                CREATE INDEX IX_Customer_Name ON Customer (CustomerName);
            ";

            var plan = analyzer.AnalyzeSqlScript(sql);

            Assert.Equal(2, plan.Statements.Count);
            Assert.Equal("create_table", plan.Statements[0].Kind);
            Assert.False(plan.Statements[0].IsDestructive);
            Assert.Equal("create_index", plan.Statements[1].Kind);
            Assert.False(plan.Statements[1].IsDestructive);
            Assert.Equal(0, plan.DestructiveCount);
            Assert.Contains("Customer", plan.AffectedTables);
        }

        [Fact]
        public void ReorganizationAnalyzer_FlagsDestructiveStatements()
        {
            var analyzer = new ReorganizationAnalyzer();
            string sql = @"
                ALTER TABLE Invoice DROP COLUMN DeprecatedTax;
                DROP TABLE ObsoleteLog;
            ";

            var plan = analyzer.AnalyzeSqlScript(sql);

            Assert.Equal(2, plan.Statements.Count);
            Assert.Equal("drop_column", plan.Statements[0].Kind);
            Assert.True(plan.Statements[0].IsDestructive);

            Assert.Equal("drop_table", plan.Statements[1].Kind);
            Assert.True(plan.Statements[1].IsDestructive);

            Assert.Equal(2, plan.DestructiveCount);
            Assert.Contains("Invoice", plan.AffectedTables);
            Assert.Contains("ObsoleteLog", plan.AffectedTables);
        }

        private class MockProcessExecutor : IProcessExecutor
        {
            public ProcessExecutionResult ExpectedResult { get; set; } = new ProcessExecutionResult { ExitCode = 0 };
            public int KilledCount { get; set; }

            public ProcessExecutionResult Execute(ProcessExecutionSpec spec, Action<string> onOutputLine = null)
            {
                onOutputLine?.Invoke("Build started");
                onOutputLine?.Invoke("Build succeeded. 0 Warning(s), 0 Error(s)");
                return ExpectedResult;
            }

            public int KillProcessTree(int rootPid)
            {
                KilledCount++;
                return 1;
            }
        }

        [Fact]
        public void MockProcessExecutor_EnablesTestableBuildSeam()
        {
            var mock = new MockProcessExecutor();
            var output = new List<string>();

            var res = mock.Execute(new ProcessExecutionSpec { ExecutablePath = "msbuild.exe" }, line => output.Add(line));

            Assert.Equal(0, res.ExitCode);
            Assert.Equal(2, output.Count);
            Assert.Contains("Build succeeded", output[1]);

            int killed = mock.KillProcessTree(1234);
            Assert.Equal(1, killed);
            Assert.Equal(1, mock.KilledCount);
        }
    }
}
