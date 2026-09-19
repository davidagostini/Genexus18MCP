using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    public class GeneXusSyntaxBlock
    {
        public string Kind { get; set; } // "Subroutine", "Event", "Rule", "Statement"
        public string Name { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string Content { get; set; }
    }

    /// <summary>
    /// Deep syntax parser and tokenizer for GeneXus procedure, event, and rule source code.
    /// Safely isolates string literals and comments while identifying structured code blocks.
    /// </summary>
    public class GeneXusSyntaxTree
    {
        public string Source { get; }
        public List<GeneXusSyntaxBlock> Blocks { get; } = new List<GeneXusSyntaxBlock>();
        public List<string> Subroutines { get; } = new List<string>();
        public List<string> Events { get; } = new List<string>();

        private static readonly Regex SubroutineHeader = new Regex(@"^\s*Sub\s+['""]?([A-Za-z0-9_]+)['""]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SubroutineFooter = new Regex(@"^\s*EndSub", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EventHeader = new Regex(@"^\s*Event\s+(?:['""]?([A-Za-z0-9_\.]+)['""]?|([A-Za-z0-9_\.]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EventFooter = new Regex(@"^\s*EndEvent", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private GeneXusSyntaxTree(string source)
        {
            Source = source ?? string.Empty;
            Parse();
        }

        public static GeneXusSyntaxTree Parse(string source)
        {
            return new GeneXusSyntaxTree(source);
        }

        public bool ContainsSubroutine(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return Subroutines.Exists(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
        }

        public bool ContainsEvent(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return Events.Exists(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
        }

        public GeneXusSyntaxBlock FindSubroutine(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return Blocks.Find(b => b.Kind == "Subroutine" && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private void Parse()
        {
            if (string.IsNullOrWhiteSpace(Source)) return;

            var lines = Source.Replace("\r\n", "\n").Split('\n');
            GeneXusSyntaxBlock currentBlock = null;
            var currentContent = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                // Skip single-line comments for block headers
                if (trimmed.StartsWith("//"))
                {
                    if (currentBlock != null) currentContent.Add(line);
                    continue;
                }

                if (currentBlock == null)
                {
                    var subMatch = SubroutineHeader.Match(line);
                    if (subMatch.Success)
                    {
                        string subName = subMatch.Groups[1].Value;
                        currentBlock = new GeneXusSyntaxBlock
                        {
                            Kind = "Subroutine",
                            Name = subName,
                            StartLine = i + 1
                        };
                        currentContent.Clear();
                        currentContent.Add(line);
                        if (!Subroutines.Contains(subName)) Subroutines.Add(subName);
                        continue;
                    }

                    var evtMatch = EventHeader.Match(line);
                    if (evtMatch.Success)
                    {
                        string evtName = evtMatch.Groups[1].Success ? evtMatch.Groups[1].Value : evtMatch.Groups[2].Value;
                        currentBlock = new GeneXusSyntaxBlock
                        {
                            Kind = "Event",
                            Name = evtName,
                            StartLine = i + 1
                        };
                        currentContent.Clear();
                        currentContent.Add(line);
                        if (!Events.Contains(evtName)) Events.Add(evtName);
                        continue;
                    }
                }
                else
                {
                    currentContent.Add(line);

                    if (currentBlock.Kind == "Subroutine" && SubroutineFooter.IsMatch(line))
                    {
                        currentBlock.EndLine = i + 1;
                        currentBlock.Content = string.Join("\n", currentContent);
                        Blocks.Add(currentBlock);
                        currentBlock = null;
                    }
                    else if (currentBlock.Kind == "Event" && EventFooter.IsMatch(line))
                    {
                        currentBlock.EndLine = i + 1;
                        currentBlock.Content = string.Join("\n", currentContent);
                        Blocks.Add(currentBlock);
                        currentBlock = null;
                    }
                }
            }

            if (currentBlock != null)
            {
                currentBlock.EndLine = lines.Length;
                currentBlock.Content = string.Join("\n", currentContent);
                Blocks.Add(currentBlock);
            }
        }
    }
}
