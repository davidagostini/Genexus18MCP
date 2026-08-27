using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Helpers
{
    public sealed class StagedObjectMutation
    {
        public string Target { get; set; }
        public string Part { get; set; }
        public string OriginalContent { get; set; }
        public string ModifiedContent { get; set; }
        public bool Applied { get; set; }
    }

    /// <summary>
    /// Deep Unit-of-Work Manager for Multi-Object Refactorings.
    /// Provides atomic multi-object transaction semantics, in-memory dry-run diffing,
    /// and automated LIFO compensation/rollback across all touched call-sites on intermediate failures.
    /// </summary>
    public sealed class MultiObjectUnitOfWork
    {
        private readonly List<StagedObjectMutation> _staged = new List<StagedObjectMutation>();
        private readonly WriteService _writeService;
        private readonly ObjectService _objectService;

        public MultiObjectUnitOfWork(WriteService writeService, ObjectService objectService)
        {
            _writeService = writeService;
            _objectService = objectService;
        }

        public void Stage(string target, string part, string originalContent, string modifiedContent)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            string effectivePart = part ?? "Source";

            var existing = _staged.FirstOrDefault(s =>
                string.Equals(s.Target, target, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Part, effectivePart, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.ModifiedContent = modifiedContent;
                if (existing.OriginalContent == null) existing.OriginalContent = originalContent;
            }
            else
            {
                _staged.Add(new StagedObjectMutation
                {
                    Target = target,
                    Part = effectivePart,
                    OriginalContent = originalContent,
                    ModifiedContent = modifiedContent,
                    Applied = false
                });
            }
        }

        public JObject BuildPreviewPlan()
        {
            var plan = new JObject
            {
                ["totalObjects"] = _staged.Count,
                ["mutations"] = new JArray(_staged.Select(m => new JObject
                {
                    ["target"] = m.Target,
                    ["part"] = m.Part,
                    ["changed"] = !string.Equals(m.OriginalContent, m.ModifiedContent, StringComparison.Ordinal)
                }))
            };
            return plan;
        }

        public bool Commit(out List<string> errors)
        {
            errors = new List<string>();
            if (_staged.Count == 0) return true;

            if (_writeService == null)
            {
                errors.Add("WriteService is not available for committing unit of work.");
                return false;
            }

            var appliedList = new List<StagedObjectMutation>();

            foreach (var mutation in _staged)
            {
                try
                {
                    var writeArgs = new JObject
                    {
                        ["part"] = mutation.Part,
                        ["content"] = mutation.ModifiedContent,
                        ["autoCommit"] = false
                    };

                    string resJson = _writeService.WriteObject(mutation.Target, writeArgs);
                    var resObj = JObject.Parse(resJson);
                    string status = resObj["status"]?.ToString();

                    if (!string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"Failed writing {mutation.Target}: {resObj["message"] ?? resObj["details"]}");
                        break;
                    }

                    mutation.Applied = true;
                    appliedList.Add(mutation);
                }
                catch (Exception ex)
                {
                    errors.Add($"Exception writing {mutation.Target}: {ex.Message}");
                    break;
                }
            }

            if (errors.Count > 0)
            {
                // Rollback previously applied mutations in LIFO (reverse) order
                appliedList.Reverse();
                foreach (var applied in appliedList)
                {
                    try
                    {
                        if (applied.OriginalContent != null)
                        {
                            var rollbackArgs = new JObject
                            {
                                ["part"] = applied.Part,
                                ["content"] = applied.OriginalContent,
                                ["autoCommit"] = false
                            };
                            _writeService.WriteObject(applied.Target, rollbackArgs);
                        }
                    }
                    catch
                    {
                        // best effort compensation
                    }
                }
                return false;
            }

            return true;
        }
    }
}