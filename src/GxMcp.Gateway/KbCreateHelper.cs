using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Gateway-side helper to create brand new GeneXus Knowledge Bases using native MSBuild tasks.
    /// Operates outside of an active worker because a worker requires an existing KB.
    /// </summary>
    public static class KbCreateHelper
    {
        public sealed class CreateOptions
        {
            public string Path { get; set; } = string.Empty;
            public string? Name { get; set; }
            public string? Alias { get; set; }
            public string? DbServer { get; set; }
            public string? DbName { get; set; }
            public string? DbUser { get; set; }
            public string? DbPassword { get; set; }
            public string? Template { get; set; }
            public string? SdkPath { get; set; }
            public string? Major { get; set; }
            public bool OpenAfterCreate { get; set; } = true;
            public bool Persist { get; set; } = false;
            public bool DryRun { get; set; } = false;
        }

        public static async Task<JObject> CreateKbAsync(
            CreateOptions options,
            Configuration? activeConfig,
            string sessionId,
            bool sessionContextEnabled,
            IWorkerSupervisor? workerPool,
            Action<string, string, string>? setSessionSelectedKb,
            Action<string>? triggerIndexBootstrap)
        {
            if (options == null)
            {
                return Error("InvalidArguments", "Create options cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(options.Path))
            {
                return Error("PathRequired", "The 'path' parameter is required for creating a Knowledge Base.",
                    hint: "Specify an absolute directory path where the Knowledge Base will be created.");
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(options.Path.Trim());
            }
            catch (Exception ex)
            {
                return Error("InvalidPath", $"Invalid path '{options.Path}': {ex.Message}");
            }

            // Guard against overwriting an existing KB
            if (Configuration.IsPlausibleKbPath(fullPath))
            {
                return Error("KbAlreadyExists", $"A GeneXus Knowledge Base already exists at '{fullPath}'.",
                    hint: "Choose a new directory, or use genexus_kb action=open to open the existing Knowledge Base.");
            }

            if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                return Error("DirectoryNotEmpty",
                    $"Target directory '{fullPath}' already exists and is not empty.",
                    hint: "Specify an empty directory or a non-existent path to create a new Knowledge Base.");
            }

            // Derive name and alias
            string name = string.IsNullOrWhiteSpace(options.Name)
                ? Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : options.Name.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                return Error("InvalidName", "Could not determine a valid Knowledge Base name from the path.");
            }

            string alias = string.IsNullOrWhiteSpace(options.Alias)
                ? name.ToLowerInvariant()
                : options.Alias.Trim().ToLowerInvariant();

            // Resolve SDK path
            string? sdkPath = ResolveSdkPath(options.SdkPath, options.Major, activeConfig);
            if (string.IsNullOrWhiteSpace(sdkPath) || !Directory.Exists(sdkPath))
            {
                return Error("SdkNotFound",
                    $"Could not locate a valid GeneXus SDK installation. Tried: '{sdkPath}'.",
                    hint: "Specify 'sdkPath' (e.g. 'C:\\Program Files (x86)\\GeneXus\\GeneXus18') or 'major' ('16', '17', '18'), or configure GeneXus.InstallationPath in config.json.");
            }

            string tasksDll = Path.Combine(sdkPath, "Genexus.MsBuild.Tasks.dll");
            string targetsFile = Path.Combine(sdkPath, "Genexus.Tasks.targets");
            if (!File.Exists(tasksDll) || !File.Exists(targetsFile))
            {
                return Error("SdkIncomplete",
                    $"GeneXus SDK at '{sdkPath}' is missing required MSBuild assets ({Path.GetFileName(tasksDll)} or {Path.GetFileName(targetsFile)}).");
            }

            // Resolve template
            string? templatePath = ResolveTemplatePath(sdkPath, options.Template);
            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            {
                return Error("TemplateNotFound",
                    $"GeneXus KB template was not found: '{options.Template ?? "default csharp/netcore.kbtemplate"}'.",
                    hint: $"Verify that '{templatePath}' exists in the GeneXus Templates directory.");
            }

            // Resolve DB parameters
            string dbServer = string.IsNullOrWhiteSpace(options.DbServer)
                ? @"(LocalDB)\MSSQLLocalDB"
                : options.DbServer.Trim();

            string dbName = string.IsNullOrWhiteSpace(options.DbName)
                ? $"gx_kb_{name}"
                : options.DbName.Trim();

            // Resolve MSBuild.exe (GeneXus tasks are 32-bit x86 net4x)
            string? msbuildPath = LocateNetFrameworkMsBuild();
            if (string.IsNullOrWhiteSpace(msbuildPath) || !File.Exists(msbuildPath))
            {
                return Error("MsBuildNotFound",
                    "Could not locate 32-bit .NET Framework MSBuild.exe (v4.0.30319) required by GeneXus SDK tasks.",
                    hint: "Verify that .NET Framework 4.8 is installed on this Windows machine.");
            }

            // Handle DryRun
            if (options.DryRun)
            {
                return new JObject
                {
                    ["status"] = "Plan",
                    ["dryRun"] = true,
                    ["alias"] = alias,
                    ["name"] = name,
                    ["dbServer"] = dbServer,
                    ["dbName"] = dbName,
                    ["template"] = templatePath,
                    ["sdkPath"] = sdkPath,
                    ["plan"] = new JObject
                    {
                        ["path"] = fullPath,
                        ["name"] = name,
                        ["alias"] = alias,
                        ["dbServer"] = dbServer,
                        ["dbName"] = dbName,
                        ["dbUser"] = options.DbUser,
                        ["template"] = templatePath,
                        ["sdkPath"] = sdkPath,
                        ["msbuildPath"] = msbuildPath,
                        ["openAfterCreate"] = options.OpenAfterCreate,
                        ["persist"] = options.Persist
                    }
                };
            }

            // Preflight: If using LocalDB, ensure instance is started
            if (dbServer.IndexOf("LocalDB", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                TryStartLocalDbInstance("MSSQLLocalDB");
            }

            // Create target directory if needed
            try
            {
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
            }
            catch (Exception ex)
            {
                return Error("DirectoryCreationFailed", $"Failed to create target directory '{fullPath}': {ex.Message}");
            }

            // Generate temporary MSBuild project file
            string tempProj = Path.Combine(Path.GetTempPath(), $"gxmcp_create_kb_{Guid.NewGuid():N}.proj");
            try
            {
                string projXml = $@"<Project DefaultTargets=""Create"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Import Project=""{targetsFile}"" />
  <UsingTask AssemblyFile=""{tasksDll}"" TaskName=""Genexus.MsBuild.Tasks.CreateKnowledgeBase"" />
  <Target Name=""Create"">
    <CreateKnowledgeBase
        Directory=""$(KBDirectory)""
        Template=""$(KBTemplate)""
        ServerInstance=""$(KBDbServer)""
        DBName=""$(KBDbName)""
        UserId=""$(KBDbUser)""
        Password=""$(KBDbPassword)""
        IntegratedSecurity=""$(KBIntegratedSecurity)"" />
  </Target>
</Project>";
                File.WriteAllText(tempProj, projXml);

                bool integrated = string.IsNullOrWhiteSpace(options.DbUser);
                string arguments = $"/nologo /v:minimal \"{tempProj}\" \"/p:KBDirectory={fullPath}\" \"/p:KBTemplate={templatePath}\" \"/p:KBDbServer={dbServer}\" \"/p:KBDbName={dbName}\" \"/p:KBIntegratedSecurity={integrated}\"";
                if (!integrated)
                {
                    arguments += $" \"/p:KBDbUser={options.DbUser}\" \"/p:KBDbPassword={options.DbPassword}\"";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = msbuildPath,
                    Arguments = arguments,
                    WorkingDirectory = fullPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                psi.EnvironmentVariables["GX_PATH"] = sdkPath;
                psi.EnvironmentVariables["GX_PROGRAM_DIR"] = sdkPath;

                var sw = Stopwatch.StartNew();
                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                bool finished = proc.WaitForExit(300000); // 5 minutes ceiling
                sw.Stop();

                if (!finished)
                {
                    try { proc.Kill(); } catch { }
                    return Error("KbCreationTimeout",
                        $"GeneXus KB creation timed out after 300 seconds at '{fullPath}'.",
                        hint: "Check if SQL Server / LocalDB is responsive.");
                }

                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                string allOutput = (stdout + "\n" + stderr).Trim();

                if (proc.ExitCode != 0 || !Configuration.IsPlausibleKbPath(fullPath))
                {
                    return new JObject
                    {
                        ["status"] = "Error",
                        ["code"] = "KbCreationFailed",
                        ["message"] = $"GeneXus KB creation exited with code {proc.ExitCode}.",
                        ["path"] = fullPath,
                        ["durationMs"] = sw.ElapsedMilliseconds,
                        ["output"] = allOutput,
                        ["hint"] = "Check database permissions, LocalDB instance state, and ensure no conflicting database exists."
                    };
                }

                // Optional persistence to config.json
                bool persisted = false;
                if (options.Persist && !string.IsNullOrWhiteSpace(Configuration.CurrentConfigPath))
                {
                    try
                    {
                        persisted = TryPersistKbEntry(Configuration.CurrentConfigPath!, alias, fullPath, activeConfig);
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[KbCreateHelper] Failed to persist KB entry: {ex.Message}");
                    }
                }

                // Optional open & session selection
                int? workerPid = null;
                bool opened = false;
                bool selected = false;
                if (options.OpenAfterCreate && workerPool != null)
                {
                    try
                    {
                        var handle = new KbHandle(alias, fullPath);
                        workerPool.RegisterKnown(handle);
                        var worker = await workerPool.AcquireAsync(handle, CancellationToken.None);
                        workerPid = worker?.Pid;
                        opened = true;

                        if (sessionContextEnabled && setSessionSelectedKb != null)
                        {
                            setSessionSelectedKb(sessionId, alias, fullPath);
                            selected = true;
                        }

                        triggerIndexBootstrap?.Invoke(alias);
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[KbCreateHelper] Created KB succeeded, but auto-open failed: {ex.Message}");
                    }
                }

                return new JObject
                {
                    ["status"] = "Success",
                    ["created"] = true,
                    ["path"] = fullPath,
                    ["name"] = name,
                    ["alias"] = alias,
                    ["dbServer"] = dbServer,
                    ["dbName"] = dbName,
                    ["template"] = templatePath,
                    ["sdkPath"] = sdkPath,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["opened"] = opened,
                    ["selected"] = selected,
                    ["workerPid"] = workerPid,
                    ["persisted"] = persisted
                };
            }
            finally
            {
                try
                {
                    if (File.Exists(tempProj)) File.Delete(tempProj);
                }
                catch { }
            }
        }

        private static string? ResolveSdkPath(string? explicitPath, string? explicitMajor, Configuration? activeConfig)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath.Trim()))
            {
                return Path.GetFullPath(explicitPath.Trim());
            }

            if (!string.IsNullOrWhiteSpace(explicitMajor))
            {
                var diag = GeneXusVersionCatalog.ToDiagnosticObject();
                if (diag["entries"] is JArray entries)
                {
                    foreach (var token in entries)
                    {
                        if (token is JObject entryObj &&
                            string.Equals(entryObj["major"]?.ToString(), explicitMajor.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            string? defaultPath = entryObj["defaultInstallPath"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(defaultPath) && Directory.Exists(defaultPath))
                            {
                                return Path.GetFullPath(defaultPath);
                            }
                        }
                    }
                }
            }

            string? configPath = activeConfig?.GeneXus?.InstallationPath;
            if (!string.IsNullOrWhiteSpace(configPath) && Directory.Exists(configPath))
            {
                return Path.GetFullPath(configPath);
            }

            string? environmentPath = Environment.GetEnvironmentVariable("GXMCP_SDK_PATH");
            if (!string.IsNullOrWhiteSpace(environmentPath) && Directory.Exists(environmentPath.Trim()))
            {
                return Path.GetFullPath(environmentPath.Trim());
            }

            string primaryPath = GeneXusVersionCatalog.PrimaryInstallPath;
            if (Directory.Exists(primaryPath))
            {
                return Path.GetFullPath(primaryPath);
            }

            return null;
        }

        private static string? ResolveTemplatePath(string sdkPath, string? template)
        {
            string templatesDir = Path.Combine(sdkPath, "Templates");
            if (string.IsNullOrWhiteSpace(template))
            {
                string defaultNetCore = Path.Combine(templatesDir, "netcore.kbtemplate");
                if (File.Exists(defaultNetCore)) return defaultNetCore;
                string defaultCSharp = Path.Combine(templatesDir, "csharp.kbtemplate");
                if (File.Exists(defaultCSharp)) return defaultCSharp;
                string defaultNet = Path.Combine(templatesDir, "net.kbtemplate");
                if (File.Exists(defaultNet)) return defaultNet;

                if (Directory.Exists(templatesDir))
                {
                    var any = Directory.GetFiles(templatesDir, "*.kbtemplate").FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(any)) return any;
                }

                return defaultNetCore;
            }

            string trimmed = template.Trim();
            if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            string direct = Path.Combine(templatesDir, trimmed);
            if (File.Exists(direct)) return direct;

            if (!trimmed.EndsWith(".kbtemplate", StringComparison.OrdinalIgnoreCase))
            {
                string withExt = Path.Combine(templatesDir, trimmed + ".kbtemplate");
                if (File.Exists(withExt)) return withExt;
            }

            return Path.Combine(templatesDir, trimmed);
        }

        private static string? LocateNetFrameworkMsBuild()
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string frameworkPath = Path.Combine(winDir, @"Microsoft.NET\Framework\v4.0.30319\MSBuild.exe");
            if (File.Exists(frameworkPath)) return frameworkPath;

            string hardcoded = @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe";
            if (File.Exists(hardcoded)) return hardcoded;

            return null;
        }

        private static void TryStartLocalDbInstance(string instanceName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sqllocaldb",
                    Arguments = $"start {instanceName}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch
            {
                // Non-critical preflight: sqllocaldb might not be on PATH
            }
        }

        private static bool TryPersistKbEntry(string configPath, string alias, string path, Configuration? activeConfig)
        {
            if (!File.Exists(configPath)) return false;

            string content = File.ReadAllText(configPath);
            var root = JObject.Parse(content);
            if (root["Environment"] is not JObject envObj)
            {
                envObj = new JObject();
                root["Environment"] = envObj;
            }

            var kbsToken = envObj["KBs"];
            bool modified = false;
            if (kbsToken is JObject kbsObj)
            {
                kbsObj[alias] = path;
                modified = true;
            }
            else if (kbsToken is JArray kbsArr)
            {
                var existing = kbsArr.OfType<JObject>()
                    .FirstOrDefault(x => string.Equals(x["alias"]?.ToString(), alias, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing["path"] = path;
                }
                else
                {
                    kbsArr.Add(new JObject { ["alias"] = alias, ["path"] = path });
                }
                modified = true;
            }
            else
            {
                envObj["KBs"] = new JObject { [alias] = path };
                modified = true;
            }

            if (modified)
            {
                AtomicJsonFileWriter.Write(configPath, root.ToString(Formatting.Indented));
                if (activeConfig?.Environment?.KBs != null)
                {
                    var declared = activeConfig.Environment.KBs.FirstOrDefault(
                        k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                    if (declared != null) declared.Path = path;
                    else activeConfig.Environment.KBs.Add(new KbEntry { Alias = alias, Path = path });
                }
                return true;
            }

            return false;
        }

        private static JObject Error(string code, string message, string? hint = null)
        {
            var err = new JObject
            {
                ["status"] = "Error",
                ["code"] = code,
                ["message"] = message
            };
            if (!string.IsNullOrEmpty(hint))
            {
                err["hint"] = hint;
            }
            return err;
        }
    }
}
