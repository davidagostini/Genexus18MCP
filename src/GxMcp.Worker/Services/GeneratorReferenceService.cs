using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Artech.Genexus.Common.Entities;
using Artech.Genexus.Common.ModelParts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Typed access to the .NET generator references that GeneXus emits as
    /// GxExternalReference. The SDK stores those references as /r: entries in
    /// CSHARP_COMPILER_FLAGS on the selected GxGenerator.
    /// </summary>
    public sealed class GeneratorReferenceService
    {
        private const string CompilerFlagsProperty = "CSHARP_COMPILER_FLAGS";
        private static readonly Regex ReferenceToken = new Regex(
            @"(?ix)(?<!\S)/(?:(?:r)|(?:reference)):(?:""(?<path>[^""]+)""|(?<path>\S+))",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly IGeneratorConfigurationStore _store;

        public GeneratorReferenceService(KbService kbService)
            : this(new SdkGeneratorConfigurationStore(kbService))
        {
        }

        internal GeneratorReferenceService(IGeneratorConfigurationStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public string Run(JObject args)
        {
            args = args ?? new JObject();
            string action = (args["action"]?.ToString() ?? "list").Trim().ToLowerInvariant();
            string environment = args["environment"]?.ToString()?.Trim();
            string generator = args["generator"]?.ToString()?.Trim();
            string assembly = NormalizeAssemblyName(args["assembly"]?.ToString());
            string baseVersion = args["baseVersion"]?.ToString()?.Trim();
            bool rollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            bool forcedDryRun = args["dryRun"]?.ToObject<bool?>() == true;

            if (string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(generator))
                return Error("GeneratorSelectionRequired", "environment and generator are required.");

            bool add = action == "add" || action == "dry_run_add";
            bool remove = action == "remove" || action == "dry_run_remove";
            bool dryRun = forcedDryRun || action == "dry_run_add" || action == "dry_run_remove";
            if (action != "list" && !add && !remove)
                return Error("UnsupportedGeneratorReferenceAction", "Use list, dry_run_add, add, dry_run_remove, or remove.");
            if ((add || remove) && string.IsNullOrWhiteSpace(assembly))
                return Error("AssemblyRequired", "assembly is required for add/remove actions.");

            try
            {
                GeneratorConfigurationSnapshot before = _store.Read(environment, generator, reload: false);
                List<string> beforeReferences = ParseReferences(before.CompilerFlags);

                if (action == "list")
                    return Success("GeneratorReferencesListed", before, before, beforeReferences, beforeReferences,
                        persisted: false, verified: true, idempotent: true, dryRun: true,
                        assembly: null, assemblyInfo: null, rollbackPerformed: false,
                        stateRestoredExactly: false, unrelatedChanges: new JArray());

                JObject assemblyInfo = null;
                if (add || !string.IsNullOrWhiteSpace(args["assemblyPath"]?.ToString()))
                {
                    string resolvedPath = ResolveAssemblyPath(
                        args["assemblyPath"]?.ToString(), assembly, before.KbLocation, before.TargetPath);
                    assemblyInfo = InspectAssembly(resolvedPath, assembly);
                }

                bool present = beforeReferences.Any(x => SameAssembly(x, assembly));
                string requestedFlags = add
                    ? (present ? before.CompilerFlags : AddReferenceToken(before.CompilerFlags, assembly))
                    : (present ? RemoveReferenceTokens(before.CompilerFlags, assembly) : before.CompilerFlags);
                List<string> requestedReferences = ParseReferences(requestedFlags);
                bool idempotent = add ? present : !present;

                if (dryRun)
                    return Success(add ? "GeneratorReferenceAddPreview" : "GeneratorReferenceRemovePreview",
                        before, before, beforeReferences, requestedReferences,
                        persisted: false, verified: true, idempotent: idempotent, dryRun: true,
                        assembly: assembly, assemblyInfo: assemblyInfo, rollbackPerformed: false,
                        stateRestoredExactly: false, unrelatedChanges: new JArray());

                // A no-op is safe and useful even without a token: it cannot overwrite
                // concurrent state and gives add/remove their idempotent contract.
                if (idempotent)
                    return Success(add ? "GeneratorReferenceAlreadyPresent" : "GeneratorReferenceAlreadyAbsent",
                        before, before, beforeReferences, beforeReferences,
                        persisted: true, verified: true, idempotent: true, dryRun: false,
                        assembly: assembly, assemblyInfo: assemblyInfo, rollbackPerformed: false,
                        stateRestoredExactly: false, unrelatedChanges: new JArray());

                if (string.IsNullOrWhiteSpace(baseVersion))
                    return Error("BaseVersionRequired",
                        "baseVersion is required for a generator reference mutation. Run list or dry_run_* first.",
                        before.VersionToken);

                GeneratorMutationResult mutation = _store.Apply(
                    environment, generator, baseVersion, requestedFlags, rollbackOnFailure);

                if (mutation.VersionConflict)
                    return Error("VersionConflict",
                        "The generator configuration changed after baseVersion was issued; no change was written.",
                        mutation.Before?.VersionToken ?? before.VersionToken);

                GeneratorConfigurationSnapshot effectiveBefore = mutation.Before ?? before;
                GeneratorConfigurationSnapshot effectiveAfter = mutation.After ?? effectiveBefore;
                List<string> afterReferences = ParseReferences(effectiveAfter.CompilerFlags);
                JArray unrelated = FindUnrelatedChanges(effectiveBefore, effectiveAfter, CompilerFlagsProperty);
                bool referenceVerified = add
                    ? afterReferences.Count(x => SameAssembly(x, assembly)) == 1
                    : afterReferences.All(x => !SameAssembly(x, assembly));
                bool verified = mutation.Verified && referenceVerified && unrelated.Count == 0;

                if (!verified)
                {
                    return McpResponse.Err(
                        code: "GeneratorReferenceNotPersisted",
                        message: mutation.Error ?? "The generator reference did not match the requested persisted state.",
                        hint: mutation.StateRestoredExactly
                            ? "The complete prior generator configuration was restored exactly; retry from a fresh list token."
                            : "Do not retry with the stale token. Inspect the generator configuration and resolve concurrent changes first.",
                        extra: new JObject
                        {
                            ["persisted"] = false,
                            ["verified"] = false,
                            ["before"] = new JArray(ParseReferences(effectiveBefore.CompilerFlags)),
                            ["after"] = new JArray(afterReferences),
                            ["versionBefore"] = effectiveBefore.VersionToken,
                            ["versionAfter"] = effectiveAfter.VersionToken,
                            ["partialPersistenceDetected"] = mutation.Committed && !mutation.StateRestoredExactly,
                            ["rollbackPerformed"] = mutation.RollbackPerformed,
                            ["stateRestoredExactly"] = mutation.StateRestoredExactly,
                            ["unrelatedChanges"] = unrelated,
                            ["implicitLifecycleActions"] = new JArray()
                        });
                }

                return Success(add ? "GeneratorReferenceAdded" : "GeneratorReferenceRemoved",
                    effectiveBefore, effectiveAfter,
                    ParseReferences(effectiveBefore.CompilerFlags), afterReferences,
                    persisted: true, verified: true, idempotent: false, dryRun: false,
                    assembly: assembly, assemblyInfo: assemblyInfo,
                    rollbackPerformed: mutation.RollbackPerformed,
                    stateRestoredExactly: mutation.StateRestoredExactly,
                    unrelatedChanges: unrelated);
            }
            catch (GeneratorReferenceStoreException ex)
            {
                return Error(ex.Code, ex.Message, ex.CurrentVersion);
            }
            catch (FileNotFoundException ex)
            {
                return Error("AssemblyNotFound", ex.Message);
            }
            catch (BadImageFormatException ex)
            {
                return Error("AssemblyNotManaged", ex.Message);
            }
            catch (Exception ex)
            {
                return Error("GeneratorReferenceFailed", ex.Message);
            }
        }

        private static string Success(
            string code,
            GeneratorConfigurationSnapshot beforeState,
            GeneratorConfigurationSnapshot afterState,
            IList<string> before,
            IList<string> after,
            bool persisted,
            bool verified,
            bool idempotent,
            bool dryRun,
            string assembly,
            JObject assemblyInfo,
            bool rollbackPerformed,
            bool stateRestoredExactly,
            JArray unrelatedChanges)
        {
            var result = new JObject
            {
                ["environment"] = beforeState.EnvironmentName,
                ["generator"] = beforeState.GeneratorName,
                ["persisted"] = persisted,
                ["verified"] = verified,
                ["dryRun"] = dryRun,
                ["idempotent"] = idempotent,
                ["before"] = new JArray(before),
                ["after"] = new JArray(after),
                ["baseVersion"] = beforeState.VersionToken,
                ["versionBefore"] = beforeState.VersionToken,
                ["versionAfter"] = afterState.VersionToken,
                ["rollbackPerformed"] = rollbackPerformed,
                ["stateRestoredExactly"] = stateRestoredExactly,
                ["partialPersistenceDetected"] = false,
                ["unrelatedChanges"] = unrelatedChanges ?? new JArray(),
                ["implicitLifecycleActions"] = new JArray()
            };
            if (!string.IsNullOrWhiteSpace(assembly))
            {
                if (code.IndexOf("Removed", StringComparison.OrdinalIgnoreCase) >= 0)
                    result["removed"] = assembly;
                else if (code.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         code.IndexOf("Present", StringComparison.OrdinalIgnoreCase) >= 0)
                    result["added"] = assembly;
                else
                    result["assembly"] = assembly;
            }
            if (assemblyInfo != null) result["assemblyInfo"] = assemblyInfo;
            return McpResponse.Ok(code: code, result: result);
        }

        private static string Error(string code, string message, string currentVersion = null)
        {
            var extra = new JObject
            {
                ["persisted"] = false,
                ["verified"] = false,
                ["implicitLifecycleActions"] = new JArray()
            };
            if (!string.IsNullOrWhiteSpace(currentVersion)) extra["currentVersion"] = currentVersion;
            return McpResponse.Err(code, message, extra: extra);
        }

        internal static List<string> ParseReferences(string compilerFlags)
        {
            var result = new List<string>();
            foreach (Match match in ReferenceToken.Matches(compilerFlags ?? string.Empty))
            {
                string path = match.Groups["path"].Value.Trim();
                string name = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
            return result;
        }

        internal static string AddReferenceToken(string compilerFlags, string assembly)
        {
            string raw = compilerFlags ?? string.Empty;
            if (ParseReferences(raw).Any(x => SameAssembly(x, assembly))) return raw;
            string token = assembly.IndexOf(' ') >= 0 ? "/r:\"" + assembly + "\"" : "/r:" + assembly;
            if (raw.Length == 0) return token;
            return char.IsWhiteSpace(raw[raw.Length - 1]) ? raw + token : raw + " " + token;
        }

        internal static string RemoveReferenceTokens(string compilerFlags, string assembly)
        {
            string value = compilerFlags ?? string.Empty;
            var matches = ReferenceToken.Matches(value).Cast<Match>()
                .Where(m => SameAssembly(Path.GetFileName(m.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar)), assembly))
                .OrderByDescending(m => m.Index)
                .ToList();
            foreach (Match match in matches)
            {
                int start = match.Index;
                int length = match.Length;
                if (start + length == value.Length)
                {
                    while (start > 0 && char.IsWhiteSpace(value[start - 1]))
                    {
                        start--;
                        length++;
                    }
                }
                else
                {
                    while (start + length < value.Length && char.IsWhiteSpace(value[start + length]))
                        length++;
                }
                value = value.Remove(start, length);
            }
            return value;
        }

        private static bool SameAssembly(string left, string right) =>
            string.Equals(NormalizeAssemblyName(left), NormalizeAssemblyName(right), StringComparison.OrdinalIgnoreCase);

        private static string NormalizeAssemblyName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string name = Path.GetFileName(value.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
            return name;
        }

        private static string ResolveAssemblyPath(string supplied, string assembly, string kbLocation, string targetPath)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(supplied))
            {
                string candidate = supplied.Trim().Trim('"');
                candidates.Add(Path.IsPathRooted(candidate)
                    ? Path.GetFullPath(candidate)
                    : Path.GetFullPath(Path.Combine(kbLocation ?? string.Empty, candidate)));
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(kbLocation)) candidates.Add(Path.Combine(kbLocation, assembly));
                if (!string.IsNullOrWhiteSpace(targetPath))
                {
                    candidates.Add(Path.Combine(targetPath, assembly));
                    candidates.Add(Path.Combine(targetPath, "web", "bin", assembly));
                    candidates.Add(Path.Combine(targetPath, "bin", assembly));
                }
            }

            string found = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
            if (found == null)
                throw new FileNotFoundException("Assembly was not found at the supplied or standard model paths: " + assembly);
            if (!SameAssembly(Path.GetFileName(found), assembly))
                throw new GeneratorReferenceStoreException("AssemblyNameMismatch",
                    "assemblyPath points to '" + Path.GetFileName(found) + "', not '" + assembly + "'.");
            return found;
        }

        private static JObject InspectAssembly(string path, string assembly)
        {
            AssemblyName name = AssemblyName.GetAssemblyName(path);
            if (!string.Equals(name.Name, Path.GetFileNameWithoutExtension(assembly), StringComparison.OrdinalIgnoreCase))
                throw new GeneratorReferenceStoreException("AssemblyIdentityMismatch",
                    "The managed assembly identity '" + name.Name + "' does not match '" + assembly + "'.");
            FileVersionInfo file = FileVersionInfo.GetVersionInfo(path);
            return new JObject
            {
                ["assembly"] = assembly,
                ["path"] = Path.GetFullPath(path),
                ["identity"] = name.FullName,
                ["assemblyVersion"] = name.Version?.ToString(),
                ["fileVersion"] = file.FileVersion,
                ["processorArchitecture"] = name.ProcessorArchitecture.ToString(),
                ["managed"] = true,
                ["compatibleWithDotNetGenerator"] = true
            };
        }

        private static JArray FindUnrelatedChanges(
            GeneratorConfigurationSnapshot before,
            GeneratorConfigurationSnapshot after,
            string allowedProperty)
        {
            var changes = new JArray();
            var identities = before.Generators.Keys.Union(after.Generators.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
            foreach (string identity in identities)
            {
                if (!before.Generators.TryGetValue(identity, out GeneratorState left) ||
                    !after.Generators.TryGetValue(identity, out GeneratorState right))
                {
                    changes.Add(identity + ":generator-collection");
                    continue;
                }
                var names = left.Properties.Keys.Union(right.Properties.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (string name in names)
                {
                    bool isAllowed = identity == before.TargetIdentity &&
                                     string.Equals(name, allowedProperty, StringComparison.OrdinalIgnoreCase);
                    left.Properties.TryGetValue(name, out string oldValue);
                    right.Properties.TryGetValue(name, out string newValue);
                    if (!isAllowed && !string.Equals(oldValue, newValue, StringComparison.Ordinal))
                        changes.Add(identity + ":" + name);
                }
            }
            return changes;
        }

        internal interface IGeneratorConfigurationStore
        {
            GeneratorConfigurationSnapshot Read(string environment, string generator, bool reload);
            GeneratorMutationResult Apply(string environment, string generator, string baseVersion,
                string compilerFlags, bool rollbackOnFailure);
        }

        internal sealed class GeneratorMutationResult
        {
            public GeneratorConfigurationSnapshot Before { get; set; }
            public GeneratorConfigurationSnapshot After { get; set; }
            public bool Committed { get; set; }
            public bool Verified { get; set; }
            public bool VersionConflict { get; set; }
            public bool RollbackPerformed { get; set; }
            public bool StateRestoredExactly { get; set; }
            public string Error { get; set; }
        }

        internal sealed class GeneratorConfigurationSnapshot
        {
            public string EnvironmentName { get; set; }
            public string GeneratorName { get; set; }
            public string TargetIdentity { get; set; }
            public string CompilerFlags { get; set; }
            public string VersionToken { get; set; }
            public string KbLocation { get; set; }
            public string TargetPath { get; set; }
            public Dictionary<string, GeneratorState> Generators { get; } =
                new Dictionary<string, GeneratorState>(StringComparer.Ordinal);
        }

        internal sealed class GeneratorState
        {
            public string Identity { get; set; }
            public string PropertiesXml { get; set; }
            public Dictionary<string, string> Properties { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed class SdkGeneratorConfigurationStore : IGeneratorConfigurationStore
        {
            private readonly KbService _kbService;
            private readonly object _sync = new object();

            public SdkGeneratorConfigurationStore(KbService kbService)
            {
                _kbService = kbService ?? throw new ArgumentNullException(nameof(kbService));
            }

            public GeneratorConfigurationSnapshot Read(string environment, string generator, bool reload)
            {
                lock (_sync) return Capture(environment, generator, reload, out _, out _);
            }

            public GeneratorMutationResult Apply(string environment, string generator, string baseVersion,
                string compilerFlags, bool rollbackOnFailure)
            {
                lock (_sync)
                {
                    dynamic kb = _kbService.GetKB();
                    if (kb == null) throw new GeneratorReferenceStoreException("KbNotOpen", "No Knowledge Base is open.");
                    var result = new GeneratorMutationResult();
                    bool committed = false;
                    try
                    {
                        using (var transaction = kb.BeginTransaction())
                        {
                            bool transactionCommitted = false;
                            try
                            {
                                GeneratorConfigurationSnapshot before = Capture(environment, generator, reload: false,
                                    out GeneratorsPart part, out GxGenerator selected);
                                result.Before = before;
                                if (!string.Equals(before.VersionToken, baseVersion, StringComparison.Ordinal))
                                {
                                    result.VersionConflict = true;
                                    transaction.Rollback();
                                    return result;
                                }

                                selected.Properties.SetPropertyValue(CompilerFlagsProperty, compilerFlags ?? string.Empty);
                                part.Dirty = true;
                                part.OnSavingEnviromentChange();
                                part.Save();
                                transaction.Commit();
                                transactionCommitted = true;
                                committed = true;
                                result.Committed = true;
                            }
                            finally
                            {
                                if (!transactionCommitted)
                                {
                                    try { transaction.Rollback(); } catch { }
                                }
                            }
                        }

                        GeneratorConfigurationSnapshot after = Capture(environment, generator, reload: true, out _, out _);
                        result.After = after;
                        result.Verified = string.Equals(after.CompilerFlags, compilerFlags ?? string.Empty, StringComparison.Ordinal)
                            && FindUnrelatedChanges(result.Before, after, CompilerFlagsProperty).Count == 0;
                        if (result.Verified) return result;

                        result.Error = "The GeneratorsPart reread diverged from the requested configuration.";
                        if (rollbackOnFailure)
                            Restore(environment, generator, result.Before, after.VersionToken, result);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        result.Committed = committed;
                        result.Error = ex.Message;
                        try
                        {
                            GeneratorConfigurationSnapshot current = Capture(environment, generator, reload: true, out _, out _);
                            result.After = current;
                            if (result.Before != null && string.Equals(current.VersionToken, result.Before.VersionToken, StringComparison.Ordinal))
                            {
                                result.RollbackPerformed = !committed;
                                result.StateRestoredExactly = true;
                            }
                            else if (rollbackOnFailure && result.Before != null &&
                                     (committed || string.Equals(current.CompilerFlags, compilerFlags ?? string.Empty, StringComparison.Ordinal)))
                            {
                                Restore(environment, generator, result.Before, current.VersionToken, result);
                            }
                        }
                        catch (Exception rollbackEx)
                        {
                            result.Error += " Rollback verification failed: " + rollbackEx.Message;
                        }
                        return result;
                    }
                }
            }

            private void Restore(string environment, string generator, GeneratorConfigurationSnapshot before,
                string expectedCurrentVersion, GeneratorMutationResult result)
            {
                dynamic kb = _kbService.GetKB();
                using (var transaction = kb.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        GeneratorConfigurationSnapshot current = Capture(environment, generator, reload: false,
                            out GeneratorsPart part, out _);
                        if (!string.Equals(current.VersionToken, expectedCurrentVersion, StringComparison.Ordinal))
                            throw new GeneratorReferenceStoreException("ConcurrentChangeDuringRollback",
                                "The generator configuration changed again before rollback; the newer state was not overwritten.",
                                current.VersionToken);

                        var live = part.Generators.ToDictionary(Identity, StringComparer.Ordinal);
                        if (live.Count != before.Generators.Count || before.Generators.Keys.Any(k => !live.ContainsKey(k)))
                            throw new GeneratorReferenceStoreException("GeneratorCollectionChanged",
                                "The generator collection changed and cannot be restored safely.", current.VersionToken);

                        foreach (KeyValuePair<string, GeneratorState> item in before.Generators)
                        {
                            live[item.Key].Properties.Reset();
                            live[item.Key].Properties.DeserializeFromXml(item.Value.PropertiesXml);
                        }
                        part.Dirty = true;
                        part.OnSavingEnviromentChange();
                        part.Save();
                        transaction.Commit();
                        committed = true;
                        result.RollbackPerformed = true;
                    }
                    finally
                    {
                        if (!committed)
                        {
                            try { transaction.Rollback(); } catch { }
                        }
                    }
                }

                GeneratorConfigurationSnapshot restored = Capture(environment, generator, reload: true, out _, out _);
                result.After = restored;
                result.StateRestoredExactly = string.Equals(restored.VersionToken, before.VersionToken, StringComparison.Ordinal);
                result.Verified = false;
                if (!result.StateRestoredExactly)
                    result.Error = (result.Error ?? "Verification failed.") + " The prior generator snapshot was not restored exactly.";
            }

            private GeneratorConfigurationSnapshot Capture(string requestedEnvironment, string requestedGenerator,
                bool reload, out GeneratorsPart part, out GxGenerator selected)
            {
                dynamic kb = _kbService.GetKB();
                if (kb == null) throw new GeneratorReferenceStoreException("KbNotOpen", "No Knowledge Base is open.");
                if (reload) kb.ReloadModels();

                dynamic designModel = kb.DesignModel;
                dynamic environment = designModel?.Environment;
                dynamic targetModel = environment?.TargetModel;
                if (targetModel == null)
                    throw new GeneratorReferenceStoreException("EnvironmentUnavailable", "The active target Environment is unavailable.");

                string environmentName = environment.TargetName?.ToString() ?? targetModel.Name?.ToString();
                string targetModelName = targetModel.Name?.ToString();
                if (!MatchesSelection(requestedEnvironment, environmentName, targetModelName))
                    throw new GeneratorReferenceStoreException("EnvironmentNotActive",
                        "The requested Environment is not the active Environment. This tool does not switch Environments implicitly.");

                part = ((Artech.Architecture.Common.Objects.KBModel)targetModel).Parts.Get<GeneratorsPart>();
                if (part == null)
                    throw new GeneratorReferenceStoreException("GeneratorsPartUnavailable", "The active Environment has no native GeneratorsPart.");

                selected = part.Generators.FirstOrDefault(g => GeneratorMatches(g, requestedGenerator));
                if (selected == null)
                    throw new GeneratorReferenceStoreException("GeneratorNotFound", "The requested generator was not found in the active Environment.");
                if (!selected.Properties.ContainsPropertyDefinition(CompilerFlagsProperty))
                    throw new GeneratorReferenceStoreException("GeneratorReferencesUnsupported",
                        "The selected generator does not expose the native C# compiler flags property.");

                var snapshot = new GeneratorConfigurationSnapshot
                {
                    EnvironmentName = environmentName ?? targetModelName,
                    GeneratorName = DisplayName(selected),
                    TargetIdentity = Identity(selected),
                    CompilerFlags = selected.Properties.GetPropertyValueString(CompilerFlagsProperty) ?? string.Empty,
                    KbLocation = kb.Location?.ToString(),
                    TargetPath = targetModel.TargetPath?.ToString()
                };

                foreach (GxGenerator item in part.Generators.OrderBy(Identity, StringComparer.Ordinal))
                {
                    var state = new GeneratorState
                    {
                        Identity = Identity(item),
                        PropertiesXml = item.Properties.SerializeToXml() ?? string.Empty
                    };
                    foreach (var property in item.Properties.SerializedProperties())
                        state.Properties[property.Name] = property.Value ?? string.Empty;
                    if (state.Identity == snapshot.TargetIdentity)
                        state.Properties[CompilerFlagsProperty] = snapshot.CompilerFlags;
                    snapshot.Generators[state.Identity] = state;
                }
                snapshot.VersionToken = Version(snapshot);
                return snapshot;
            }

            private static bool GeneratorMatches(GxGenerator generator, string requested)
            {
                string normalized = NormalizeSelection(requested);
                return new[]
                    {
                        generator.ToString(),
                        generator.Description,
                        generator.Category?.Name,
                        DisplayName(generator),
                        (generator.Category?.Name ?? string.Empty) + " (" + (generator.Description ?? string.Empty) + ")",
                        (generator.Category?.Name ?? string.Empty) + " " + generator.GeneratorType
                    }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Any(x => NormalizeSelection(x) == normalized);
            }

            private static bool MatchesSelection(string requested, params string[] candidates)
            {
                string normalized = NormalizeSelection(requested);
                return candidates.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Any(x => NormalizeSelection(x) == normalized);
            }

            private static string NormalizeSelection(string value) =>
                new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

            private static string DisplayName(GxGenerator generator)
            {
                string text = generator.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
                return (generator.Category?.Name ?? "Generator") + " (" + generator.GeneratorType + ")";
            }

            private static string Identity(GxGenerator generator) =>
                generator.CategoryGuid.ToString("D") + ":" + generator.Generator.ToString();

            private static string Version(GeneratorConfigurationSnapshot snapshot)
            {
                var raw = new StringBuilder()
                    .Append(snapshot.EnvironmentName).Append('\n')
                    .Append(snapshot.TargetIdentity).Append('\n')
                    .Append(snapshot.CompilerFlags ?? string.Empty).Append('\n');
                foreach (KeyValuePair<string, GeneratorState> item in snapshot.Generators.OrderBy(x => x.Key, StringComparer.Ordinal))
                    raw.Append(item.Key).Append('\n').Append(item.Value.PropertiesXml).Append('\n');
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToString()));
                    return "sha256:" + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
        }

        internal sealed class GeneratorReferenceStoreException : Exception
        {
            public string Code { get; }
            public string CurrentVersion { get; }

            public GeneratorReferenceStoreException(string code, string message, string currentVersion = null)
                : base(message)
            {
                Code = code;
                CurrentVersion = currentVersion;
            }
        }
    }
}
