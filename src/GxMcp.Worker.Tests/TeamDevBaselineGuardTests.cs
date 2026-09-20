using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Regression guard for the Team Development pending list.
    //
    // KBModel.LastCommitDate is the "everything up to this instant is already
    // committed" baseline that ITeamDevClientService.GetLocalChanges(model)
    // measures against — the same list surfaced by genexus_gxserver
    // action=pending and by the IDE's Team Dev > Commit tab. Stamping it to
    // UtcNow after a write tells Team Development that nothing is pending, and
    // because it lives on the MODEL it wipes the entry for every object in the
    // KB, including objects the worker never touched.
    //
    // Measured 2026-09-08 against v2.56.0: the IDE marked one object
    // (pending count = 1); a single genexus_edit on a DIFFERENT object dropped
    // the count to 0. Introduced by 4f7cc9c "IDE concurrency detection, sync
    // flushing, and revision stamping (#128)", which needs only
    // LastObjectsVersionDate to make the IDE reload the worker's writes.
    public class TeamDevBaselineGuardTests
    {
        private sealed class SourceLine
        {
            public string FileName { get; set; }
            public int Number { get; set; }
            public string Text { get; set; }
        }

        private static string ServicesDirectory
        {
            get
            {
                return Path.GetFullPath(Path.Combine(
                    TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services"));
            }
        }

        private static SourceLine[] CodeLines()
        {
            Assert.True(Directory.Exists(ServicesDirectory),
                "WriteService source directory must exist at: " + ServicesDirectory);

            return Directory.GetFiles(ServicesDirectory, "WriteService*.cs")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .SelectMany(ReadCodeLines)
                .ToArray();
        }

        private static IEnumerable<SourceLine> ReadCodeLines(string path)
        {
            bool inBlockComment = false;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                yield return new SourceLine
                {
                    FileName = Path.GetFileName(path),
                    Number = i + 1,
                    Text = RemoveComments(lines[i], ref inBlockComment)
                };
            }
        }

        private static string RemoveComments(string line, ref bool inBlockComment)
        {
            var code = new StringBuilder();
            bool inString = false;
            bool escaped = false;
            char delimiter = '\0';

            for (int i = 0; i < line.Length; i++)
            {
                char current = line[i];
                char next = i + 1 < line.Length ? line[i + 1] : '\0';

                if (inBlockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }

                if (!inString && current == '/' && next == '/') break;
                if (!inString && current == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                code.Append(current);
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == delimiter)
                    {
                        inString = false;
                    }
                }
                else if (current == '"' || current == '\'')
                {
                    inString = true;
                    delimiter = current;
                }
            }

            return code.ToString();
        }

        private static string MethodBody(string source, string methodName)
        {
            Match declaration = Regex.Match(
                source,
                @"(?m)^\s*(?:private|internal|public|protected)\b[^\r\n]*\b" +
                    Regex.Escape(methodName) + @"\s*\(");
            Assert.True(declaration.Success, methodName + " must exist in WriteService.cs");

            int openBrace = source.IndexOf('{', declaration.Index + declaration.Length);
            Assert.True(openBrace >= 0, methodName + " must have a body");

            int depth = 0;
            for (int i = openBrace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                if (source[i] != '}') continue;
                depth--;
                if (depth == 0)
                    return source.Substring(openBrace + 1, i - openBrace - 1);
            }

            Assert.Fail(methodName + " has an unterminated body");
            return string.Empty;
        }

        private static string CodeOnly(string source)
        {
            bool inBlockComment = false;
            return string.Join(
                Environment.NewLine,
                source.Replace("\r\n", "\n").Split('\n')
                    .Select(line => RemoveComments(line, ref inBlockComment)));
        }

        [Fact]
        public void WriteServicePartials_NeverStamp_ModelLastCommitDate()
        {
            var offenders = CodeLines()
                .Where(x => Regex.IsMatch(x.Text, @"\bLastCommitDate\s*="))
                .Select(x => x.FileName + ":" + x.Number + ": " + x.Text.Trim())
                .ToArray();

            Assert.True(offenders.Length == 0,
                "Assigning KBModel.LastCommitDate moves the Team Development commit baseline and " +
                "empties the pending-commit list for every object in the KB. Stamp " +
                "LastObjectsVersionDate instead — that is what makes the IDE notice the worker's " +
                "writes. Offending lines:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void WriteService_Stamps_ModelLastObjectsVersionDate_InEachRevisionPath()
        {
            string path = Path.Combine(ServicesDirectory, "WriteService.cs");
            Assert.True(File.Exists(path), "WriteService.cs must exist at: " + path);
            string source = File.ReadAllText(path);

            Assert.Matches(
                @"\bLastObjectsVersionDate\s*=",
                CodeOnly(MethodBody(source, "FlushSync")));
            Assert.Matches(
                @"\bLastObjectsVersionDate\s*=",
                CodeOnly(MethodBody(source, "StampObjectRevisionDates")));
        }
    }
}
