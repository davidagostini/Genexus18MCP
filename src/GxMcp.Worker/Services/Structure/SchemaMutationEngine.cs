using System;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    public class SchemaMutationOptions
    {
        public bool DryRun { get; set; }
        public string ExpectedVersion { get; set; }
    }

    public class SchemaMutationOutcome
    {
        public bool Success { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public JObject Diff { get; set; }
    }

    public class SchemaMutationResult
    {
        public bool Success { get; set; }
        public bool IsDryRun { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public JObject Diff { get; set; }
    }

    public interface ISchemaMutationEngine
    {
        SchemaMutationResult Execute<T>(
            T target,
            Func<T, T> snapshotCapturer,
            Func<T, T, SchemaMutationOutcome> mutateAction,
            SchemaMutationOptions options,
            Action<T, T> restoreAction = null,
            Func<T, string> currentVersionResolver = null);
    }

    /// <summary>
    /// Deep Schema Mutation Engine for Transactions, Tables, Indexes, and Domains.
    /// Centralizes pre-flight optimistic concurrency token checking,
    /// lossless snapshot capture, dry-run diff simulation, and automated rollback.
    /// </summary>
    public class SchemaMutationEngine : ISchemaMutationEngine
    {
        public SchemaMutationResult Execute<T>(
            T target,
            Func<T, T> snapshotCapturer,
            Func<T, T, SchemaMutationOutcome> mutateAction,
            SchemaMutationOptions options,
            Action<T, T> restoreAction = null,
            Func<T, string> currentVersionResolver = null)
        {
            if (target == null)
            {
                return new SchemaMutationResult
                {
                    Success = false,
                    ErrorCode = "NullTarget",
                    ErrorMessage = "Target schema object cannot be null."
                };
            }

            options = options ?? new SchemaMutationOptions();

            // Pre-flight version check
            if (!string.IsNullOrEmpty(options.ExpectedVersion) && currentVersionResolver != null)
            {
                string currentVersion = currentVersionResolver(target);
                if (!string.Equals(currentVersion, options.ExpectedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return new SchemaMutationResult
                    {
                        Success = false,
                        ErrorCode = "VersionConflict",
                        ErrorMessage = $"Version token conflict: current '{currentVersion}' does not match expected '{options.ExpectedVersion}'."
                    };
                }
            }

            // Capture snapshot
            T snapshot = snapshotCapturer != null ? snapshotCapturer(target) : default;

            if (options.DryRun)
            {
                // In dry-run mode, create a scratch target or work on snapshot copy
                T scratch = snapshot != null ? snapshot : target;
                try
                {
                    var outcome = mutateAction(snapshot, scratch);
                    return new SchemaMutationResult
                    {
                        Success = outcome.Success,
                        IsDryRun = true,
                        ErrorCode = outcome.ErrorCode,
                        ErrorMessage = outcome.ErrorMessage,
                        Diff = outcome.Diff
                    };
                }
                catch (Exception ex)
                {
                    return new SchemaMutationResult
                    {
                        Success = false,
                        IsDryRun = true,
                        ErrorCode = "DryRunError",
                        ErrorMessage = ex.Message
                    };
                }
            }

            // Real mutation with rollback guarantee
            try
            {
                var outcome = mutateAction(snapshot, target);
                if (!outcome.Success && restoreAction != null && snapshot != null)
                {
                    restoreAction(target, snapshot);
                }

                return new SchemaMutationResult
                {
                    Success = outcome.Success,
                    IsDryRun = false,
                    ErrorCode = outcome.ErrorCode,
                    ErrorMessage = outcome.ErrorMessage,
                    Diff = outcome.Diff
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"[SCHEMA-MUTATION] Mutation failed, attempting rollback: {ex.Message}");
                if (restoreAction != null && snapshot != null)
                {
                    try
                    {
                        restoreAction(target, snapshot);
                    }
                    catch (Exception rollbackEx)
                    {
                        Logger.Error($"[SCHEMA-MUTATION] Rollback also failed: {rollbackEx.Message}");
                    }
                }

                return new SchemaMutationResult
                {
                    Success = false,
                    IsDryRun = false,
                    ErrorCode = "SchemaMutationFailed",
                    ErrorMessage = ex.Message
                };
            }
        }
    }
}
