using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_module over the official IModuleManagerService. The service exposes
    /// installed modules, package/publish/restore, and configured module servers while
    /// keeping the SDK as the only KB/configuration authority.
    /// </summary>
    public class ModuleService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;
        private readonly IndexCacheService _index;

        public ModuleService(KbService kb, ObjectService objects, IndexCacheService index = null)
        {
            _kb = kb;
            _objects = objects;
            _index = index;
        }

        public string Run(JObject args)
        {
            string action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) action = "list";
            action = action.Trim().ToLowerInvariant();

            var validActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "install", "install_builtin", "update", "list", "package", "publish",
                "restore", "list_modules_servers", "add_modules_server", "search_modules_in_servers"
            };
            if (!validActions.Contains(action))
            {
                return McpResponse.Err(
                    code: "BadAction",
                    message: "Unknown action '" + action + "'.",
                    hint: "Use install, install_builtin, update, list, package, publish, restore, add_modules_server or search_modules_in_servers.");
            }

            if (action == "install" || action == "install_builtin") return InstallVerified(args);
            if (args?["dryRun"]?.Value<bool>() == true && action != "list" && action != "list_modules_servers" && action != "search_modules_in_servers")
                return McpResponse.Err("ModuleDryRunUnsupported", "This action has no safe preview; nothing was dispatched.");

            KnowledgeBase kb;
            try { kb = _kb?.GetKB() as KnowledgeBase; }
            catch { kb = null; }
            if (kb == null)
                return McpResponse.Err(code: "NoKbOpen", message: "No KB is open in this worker session.", hint: "Open a KB first (genexus_kb action=open).");

            if (action == "list") return ListModules(kb);

            IModuleManagerService svc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IModuleManagerService>();
            if (svc == null)
            {
                return McpResponse.Err(
                    code: "ModuleManagerServiceUnavailable",
                    message: "The GeneXus SDK's IModuleManagerService is not registered in this worker session.",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");
            }

            try
            {
                switch (action)
                {
                    case "update": return Update(svc, kb.DesignModel, args);
                    case "package": return Package(svc, kb.DesignModel, args);
                    case "publish": return Publish(svc, kb.DesignModel, args);
                    case "restore": return Restore(svc, kb.DesignModel, args);
                    case "list_modules_servers": return ListServers(svc);
                    case "add_modules_server": return AddServer(svc, args);
                    case "search_modules_in_servers": return SearchServers(svc, args);
                    default: return McpResponse.Err(code: "BadAction", message: "Unsupported Module Manager action.");
                }
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ModuleOperationFailed", message: ex.Message, hint: "Check the worker log for the SDK exception.");
            }
        }

        private string InstallVerified(JObject args)
        {
            bool dispatched = false;
            if (_kb == null) return McpResponse.Err("NoKbOpen", "No KB is open in this worker session.");
            if (!Monitor.TryEnter(_kb.KbLock)) return McpResponse.Err("ModuleOperationBusy", "An incompatible KB operation is in progress.", retryable: false);
            try
            {
                var kb = _kb.GetKB() as KnowledgeBase;
                if (kb?.DesignModel == null) return McpResponse.Err("NoKbOpen", "No design model is open in this worker session.");
                var guard = WriteDestinationGuard.CheckCommand(_kb, "Module", "Run", args);
                if (guard != null) return guard;
                bool dryRun = args?["dryRun"]?.Value<bool>() == true;
                bool builtin = string.Equals(args?["action"]?.ToString(), "install_builtin", StringComparison.OrdinalIgnoreCase);
                var manager = dryRun && !builtin ? null : Helpers.SdkServiceResolver.Resolve<IModuleManagerService>();
                if ((!dryRun || builtin) && manager == null) return McpResponse.Err("ModuleManagerServiceUnavailable", "The native module manager service is unavailable.");
                string requestedVersion = args?["version"]?.ToString();
                if (builtin)
                {
                    string name = args?["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(args?["opcFile"]?.ToString()))
                        throw new ModuleInstallPlanException("BadArgs", "install_builtin requires a built-in name and does not accept opcFile.");
                    if (!manager.IsBuiltInModule(name)) throw new ModuleInstallPlanException("ModuleBuiltinNotRegistered", "The selected SDK does not register this built-in module.");
                    string builtinVersion = manager.GetBuiltinModuleVersion(name);
                    if (string.IsNullOrWhiteSpace(builtinVersion)) throw new ModuleInstallPlanException("ModuleBuiltinVersionUnavailable", "The SDK did not report the built-in version.");
                    if (!string.IsNullOrWhiteSpace(requestedVersion) && requestedVersion != builtinVersion)
                        throw new ModuleInstallPlanException("ModuleBuiltinVersionConflict", "The requested version differs from the SDK registered built-in version.");
                    requestedVersion = builtinVersion;
                }
                // Cache staging is shared by SDK sessions. Never wait behind another
                // installation or silently restart one after a timeout.
                using (var gate = new Mutex(false, @"Local\GxMcp.ModuleInstall"))
                {
                    bool entered;
                    try { entered = gate.WaitOne(0); }
                    catch (AbandonedMutexException) { gate.ReleaseMutex(); return McpResponse.Err("ModuleReconciliationRequired", "An earlier installation ended without a verified receipt. Inspect the module inventory before recovery.", retryable: false, reconciliationRequired: true); }
                    if (!entered) return McpResponse.Err("ModuleOperationBusy", "Another module installation is in progress.", retryable: false);
                    try
                    {
                        var sdkPath = Path.GetDirectoryName(typeof(KnowledgeBase).Assembly.Location);
                        var packages = ModuleInstallPackage.Plan(args?["opcFile"]?.ToString(), args?["name"]?.ToString(), requestedVersion, sdkPath);
                        var backend = new ModuleInstallSdkBackend(_kb, kb, manager, _index, packages);
                        dispatched = !dryRun;
                        var receipt = ModuleInstallFlow.Run(packages, dryRun, backend);
                        receipt["sdkCache"] = backend.CacheDiagnostics;
                        string code = receipt["code"]?.ToString();
                        bool success = receipt["status"]?.ToString() == "ok";
                        receipt.Remove("status");
                        receipt.Remove("code");
                        receipt["verificationScope"] = "module identity, version, dependency metadata and object inventory";
                        return success ? McpResponse.Ok(code: code, result: receipt)
                            : McpResponse.Err(code, receipt["diagnostic"]?["message"]?.ToString() ?? "The installation was not verified; inspect the reported inventory and diagnostic.",
                                extra: receipt, retryable: false,
                                reconciliationRequired: receipt["noMutation"]?.Value<bool>() != true);
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
            catch (ModuleInstallPlanException ex)
            {
                return McpResponse.Err(ex.Code, ex.Message, extra: new JObject { ["persisted"] = false, ["implicitLifecycleOperations"] = new JArray() });
            }
            catch (Exception ex)
            {
                return McpResponse.Err("ModulePreflightFailed", "The module installation preflight failed.",
                    errorExtra: new JObject { ["exceptionType"] = ex.GetType().Name, ["parameter"] = (ex as ArgumentException)?.ParamName },
                    extra: new JObject { ["persisted"] = dispatched ? JValue.CreateNull() : new JValue(false),
                        ["persistedStateKnown"] = !dispatched, ["implicitLifecycleOperations"] = new JArray() }, retryable: false,
                    reconciliationRequired: dispatched);
            }
            finally { Monitor.Exit(_kb.KbLock); }
        }

        private string Update(IModuleManagerService svc, KBModel model, JObject args)
        {
            string name = args?["name"]?.ToString();
            string version = args?["version"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return McpResponse.Err(code: "BadArgs", message: "action=update requires name.", hint: "Pass name=<installed module> and version=<target version>.");
            Module module = ResolveModule(name);
            if (module == null)
                return McpResponse.Err(code: "ModuleNotFound", message: "Module '" + name + "' not found in this KB.", hint: "Run action=list to inspect installed modules.");
            bool ok = svc.Update(model, module, version);
            return OperationResult(ok ? "ModuleUpdated" : "ModuleUpdateDeclined", ok, new JObject { ["name"] = name, ["version"] = version });
        }

        private string Package(IModuleManagerService svc, KBModel model, JObject args)
        {
            string name = args?["name"]?.ToString();
            string outputDirectory = args?["outputPath"]?.ToString() ?? args?["outputDirectory"]?.ToString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(outputDirectory))
                return McpResponse.Err(code: "BadArgs", message: "action=package requires name and outputPath.", hint: "outputPath is the destination directory for the generated .opc file.");
            if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                return McpResponse.Err(code: "ConfirmRequired", message: "action=package writes a module package and requires confirm=true.", hint: "Review the destination and repeat with confirm=true.");

            Module module = ResolveModule(name);
            if (module == null)
                return McpResponse.Err(code: "ModuleNotFound", message: "Module '" + name + "' not found in this KB.");

            bool rebuild = args?["rebuild"]?.ToObject<bool?>() ?? false;
            string opcFile;
            bool ok = svc.Package(module, new List<KBModel> { model }, rebuild, outputDirectory, out opcFile);
            return OperationResult(ok ? "ModulePackaged" : "ModulePackageDeclined", ok, new JObject
            {
                ["name"] = name,
                ["outputDirectory"] = outputDirectory,
                ["opcFile"] = opcFile,
                ["rebuild"] = rebuild
            });
        }

        private string Publish(IModuleManagerService svc, KBModel model, JObject args)
        {
            string serverId = args?["server"]?.ToString();
            string opcFile = args?["opcFile"]?.ToString();
            string name = args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(serverId))
                return McpResponse.Err(code: "BadArgs", message: "action=publish requires server.", hint: "Pass the configured module server id.");
            if (string.IsNullOrWhiteSpace(opcFile) && string.IsNullOrWhiteSpace(name))
                return McpResponse.Err(code: "BadArgs", message: "action=publish requires opcFile or name.", hint: "Publish a generated .opc file or an installed Module by name.");
            if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                return McpResponse.Err(code: "ConfirmRequired", message: "action=publish requires confirm=true.", hint: "Publishing uploads a module to an external server.");

            bool ok;
            if (!string.IsNullOrWhiteSpace(opcFile)) ok = svc.Publish(opcFile, serverId);
            else
            {
                Module module = ResolveModule(name);
                if (module == null) return McpResponse.Err(code: "ModuleNotFound", message: "Module '" + name + "' not found in this KB.");
                ok = svc.Publish(module, serverId);
            }
            return OperationResult(ok ? "ModulePublished" : "ModulePublishDeclined", ok, new JObject
            {
                ["name"] = name,
                ["opcFile"] = opcFile,
                ["server"] = serverId
            });
        }

        private string Restore(IModuleManagerService svc, KBModel model, JObject args)
        {
            string name = args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return McpResponse.Err(code: "BadArgs", message: "action=restore requires name.", hint: "Pass the installed module name.");
            if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                return McpResponse.Err(code: "ConfirmRequired", message: "action=restore requires confirm=true.", hint: "Restore writes module files into the KB.");

            Module module = ResolveModule(name);
            if (module == null)
                return McpResponse.Err(code: "ModuleNotFound", message: "Module '" + name + "' not found in this KB.");

            string serverId = args?["server"]?.ToString();
            IModuleManagerServer server = string.IsNullOrWhiteSpace(serverId) ? null : FindServer(svc, serverId);
            if (!string.IsNullOrWhiteSpace(serverId) && server == null)
                return McpResponse.Err(code: "ModuleServerNotFound", message: "Module server '" + serverId + "' is not configured.");
            bool ok = server == null ? svc.Restore(model, module) : svc.Restore(model, server, module);
            return OperationResult(ok ? "ModuleRestored" : "ModuleRestoreDeclined", ok, new JObject { ["name"] = name, ["server"] = serverId });
        }

        private static string AddServer(IModuleManagerService svc, JObject args)
        {
            string source = args?["source"]?.ToString();
            string serverId = args?["server"]?.ToString() ?? args?["name"]?.ToString();
            string typeValue = args?["serverType"]?.ToString() ?? "ModuleServer";
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(serverId))
                return McpResponse.Err(code: "BadArgs", message: "action=add_modules_server requires server/name and source.", hint: "Pass server=<id>, source=<URL or directory>, and serverType when needed.");
            if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                return McpResponse.Err(code: "ConfirmRequired", message: "action=add_modules_server requires confirm=true.", hint: "This changes the SDK Module Manager server configuration.");
            if (!Enum.TryParse(typeValue, true, out ServerType serverType))
                return McpResponse.Err(code: "BadArgs", message: "Unknown serverType '" + typeValue + "'.", hint: "Use Directory, Nexus, NexusNuGet or ModuleServer.");

            bool preserve = args?["preserveConfiguration"]?.ToObject<bool?>() ?? true;
            IModuleManagerServer server = svc.AddServer(serverType, serverId, source, preserve);
            if (server == null) return McpResponse.Err(code: "ModuleServerAddDeclined", message: "The SDK declined the module server.");
            return McpResponse.Ok(code: "ModuleServerAdded", result: ServerToJson(server));
        }

        private static string SearchServers(IModuleManagerService svc, JObject args)
        {
            string requested = args?["server"]?.ToString();
            string filter = args?["filter"]?.ToString();
            if (string.IsNullOrWhiteSpace(requested))
                return McpResponse.Err(code: "ModuleServerRequired", message: "action=search_modules_in_servers requires server.", hint: "Run action=list_modules_servers first, then pass one configured server name to bound the SDK network operation.");
            IEnumerable<IModuleManagerServer> servers = svc.ListServers() ?? Enumerable.Empty<IModuleManagerServer>();
            if (!string.IsNullOrWhiteSpace(requested))
                servers = servers.Where(server => string.Equals(server?.Name, requested, StringComparison.OrdinalIgnoreCase));

            var results = new JArray();
            foreach (IModuleManagerServer server in servers)
            {
                if (server == null) continue;
                IEnumerable<ModulePackage> packages = string.IsNullOrWhiteSpace(filter) ? server.List() : server.List(filter);
                var modules = new JArray();
                foreach (ModulePackage package in packages ?? Enumerable.Empty<ModulePackage>()) modules.Add(PackageToJson(package));
                results.Add(new JObject
                {
                    ["server"] = server.Name,
                    ["modules"] = modules,
                    ["count"] = modules.Count
                });
            }
            return McpResponse.Ok(code: "ModuleServerSearchCompleted", result: new JObject
            {
                ["filter"] = filter,
                ["servers"] = results,
                ["serverCount"] = results.Count
            });
        }

        private static string ListServers(IModuleManagerService svc)
        {
            try
            {
                IEnumerable<IModuleManagerServer> servers = svc.ListServers() ?? Enumerable.Empty<IModuleManagerServer>();
                var result = new JArray();
                foreach (IModuleManagerServer server in servers)
                    if (server != null) result.Add(ServerToJson(server));
                return McpResponse.Ok(code: "ModuleServerListRetrieved", result: new JObject
                {
                    ["servers"] = result,
                    ["count"] = result.Count
                });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ModuleServerListFailed", message: ex.Message, hint: "Check the worker log for the SDK exception.");
            }
        }

        private Module ResolveModule(string name)
        {
            try { return _objects?.FindObject(name, "Module") as Module; }
            catch { return null; }
        }

        private static IModuleManagerServer FindServer(IModuleManagerService svc, string name)
        {
            try
            {
                return (svc.ListServers() ?? Enumerable.Empty<IModuleManagerServer>())
                    .FirstOrDefault(server => string.Equals(server?.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        private static string ListModules(dynamic kb)
        {
            try
            {
                var modules = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                foreach (KBObject obj in kb.DesignModel.Objects.GetAll())
                {
                    if (obj == null || !string.Equals(obj.TypeDescriptor?.Name, "Module", StringComparison.OrdinalIgnoreCase)) continue;
                    string guid = SafeString(() => obj.Guid == Guid.Empty ? string.Empty : obj.Guid.ToString());
                    string entityKey = SafeString(() => obj.Key?.ToString());
                    string path = BuildModulePath(obj);
                    string parent = path;
                    int parentSeparator = path.LastIndexOf('/');
                    if (parentSeparator >= 0) parent = path.Substring(0, parentSeparator);
                    else parent = string.Empty;
                    string identity = BuildStableModuleKey(guid, entityKey, path, obj.Name);
                    if (modules.ContainsKey(identity)) continue;
                    modules[identity] = new JObject
                    {
                        ["name"] = obj.Name,
                        ["version"] = obj.GetPropertyValue<string>("ModuleVersion"),
                        ["description"] = SafeString(() => obj.Description),
                        ["guid"] = guid,
                        ["entityKey"] = entityKey,
                        ["parent"] = parent,
                        ["path"] = path,
                        ["qualifiedName"] = path
                    };
                }
                var ordered = new JArray();
                foreach (JObject module in modules.Values
                    .OrderBy(item => item["path"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item["name"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item["guid"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    ordered.Add(module);
                return McpResponse.Ok(code: "ModuleListRetrieved", result: new JObject
                {
                    ["count"] = ordered.Count,
                    ["modules"] = ordered,
                    ["source"] = "sdk:DesignModel.Objects"
                });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ModuleListFailed", message: ex.Message, hint: "Check the worker log for the SDK exception.");
            }
        }

        internal static string BuildStableModuleKey(string guid, string entityKey, string path, string name)
        {
            if (!string.IsNullOrWhiteSpace(guid)) return "guid:" + guid.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(entityKey)) return "entity:" + entityKey.Trim().ToLowerInvariant();
            return "path:" + (path ?? string.Empty).Trim().ToLowerInvariant() + ":" + (name ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string BuildModulePath(KBObject module)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            KBObject current = module;
            for (int depth = 0; current != null && depth < 64; depth++)
            {
                string name = SafeString(() => current.Name);
                if (string.IsNullOrWhiteSpace(name)) break;
                string key = SafeString(() => current.Guid == Guid.Empty ? name : current.Guid.ToString());
                if (!seen.Add(key)) break;
                names.Add(name);
                try { current = current.Parent; }
                catch { break; }
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private static string OperationResult(string code, bool success, JObject details)
        {
            details = details ?? new JObject();
            details["success"] = success;
            details["source"] = "sdk:IModuleManagerService";
            return McpResponse.Ok(code: code, result: details);
        }

        private static JObject ServerToJson(IModuleManagerServer server)
        {
            var result = new JObject
            {
                ["name"] = server?.Name,
                ["canDelete"] = server?.CanDelete ?? false,
                ["canUpdate"] = server?.CanUpdate ?? false,
                ["needsProfile"] = server?.NeedsProfile ?? false
            };
            if (server is IModuleManagerConfigurableServer configurable)
            {
                result["serverType"] = configurable.ServerType.ToString();
                result["sourceConfigured"] = !string.IsNullOrWhiteSpace(configurable.Source);
                result["preserveConfiguration"] = configurable.PreserveConfiguration;
            }
            return result;
        }

        private static JObject PackageToJson(ModulePackage package)
        {
            return new JObject
            {
                ["id"] = package?.ID,
                ["name"] = package?.Name,
                ["version"] = package?.Version,
                ["description"] = package?.Description,
                ["owner"] = package?.Owner,
                ["author"] = package?.Author,
                ["hasDatabase"] = package?.HasDatabase ?? false,
                ["serverUrl"] = package?.ServerUrl,
                ["tags"] = package?.Tags
            };
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); } catch { return null; }
        }
    }
}
