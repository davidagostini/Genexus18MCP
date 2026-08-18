using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.CustomTypes;
using Artech.Genexus.Common.Entities;
using Artech.Genexus.Common.ModelParts;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using GxAttribute = Artech.Genexus.Common.Objects.Attribute;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Creates and maintains a root-only Transaction + Data View pair over an
    /// existing physical table. No lifecycle action is called by this service.
    /// </summary>
    public sealed class DataViewService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public DataViewService(KbService kbService, ObjectService objectService)
        {
            _kbService = kbService;
            _objectService = objectService;
        }

        private sealed class Mapping
        {
            public string AttributeName;
            public string ColumnName;
            public bool IsKey;
            public GxAttribute Attribute;
        }

        private sealed class Request
        {
            public string Action;
            public string TransactionName;
            public string DataViewName;
            public string DataStoreName;
            public string Schema;
            public string TableName;
            public bool Updatable;
            public bool DryRun;
            public bool RollbackOnFailure;
            public string ExpectedVersion;
            public readonly List<Mapping> Mappings = new List<Mapping>();
            public readonly JArray Errors = new JArray();
        }

        public string Run(JObject args)
        {
            Request request = Parse(args ?? new JObject());
            if (request.Errors.Count > 0)
                return ValidationError(request);

            try
            {
                var kb = _kbService.GetKB();
                if (kb == null)
                    return McpResponse.Err("NoKb", "No KB is open.", target: request.TransactionName);

                var model = kb.DesignModel;
                Transaction transaction = FindTransaction(model, request.TransactionName);
                DataView dataView = FindDataView(model, request.DataViewName);

                if (request.Action == "inspect")
                    return Inspect(request, transaction, dataView);

                if (request.Action == "delete")
                {
                    if (request.DryRun)
                        return DeleteDryRun(request, transaction, dataView);
                    return Delete(request, transaction, dataView);
                }

                bool create = request.Action == "create" || request.Action == "dry_run";
                if (create && (transaction != null || dataView != null))
                    return McpResponse.Err(
                        "AlreadyExists",
                        "The requested Transaction or Data View already exists; nothing was changed.",
                        "Use action=inspect to read the pair, or choose unused names.",
                        target: request.TransactionName,
                        errorExtra: new JObject
                        {
                            ["transactionExists"] = transaction != null,
                            ["dataViewExists"] = dataView != null
                        });
                if (request.Action == "update" && (transaction == null || dataView == null))
                    return McpResponse.Err(
                        "ObjectNotFound",
                        "action=update requires both the Transaction and Data View to exist.",
                        target: request.TransactionName);

                Table sourceTable;
                GxDataStore dataStore;
                string validation = ResolveAndValidatePhysicalModel(request, model, out sourceTable, out dataStore);
                if (validation != null) return validation;

                string beforeVersion = ComputeVersion(request, transaction, dataView, sourceTable);
                string concurrency = ValidateExpectedVersion(request, beforeVersion);
                if (concurrency != null) return concurrency;

                if (request.DryRun || request.Action == "dry_run")
                    return DryRun(request, beforeVersion, sourceTable, dataStore);

                if (request.Action == "create")
                    return Create(request, beforeVersion, sourceTable, dataStore);
                if (request.Action == "update")
                    return Update(request, beforeVersion, transaction, dataView, sourceTable, dataStore);

                return McpResponse.Err("UnsupportedAction", "Unsupported Data View action: " + request.Action,
                    "Use inspect, dry_run, create, update, or delete.", target: request.TransactionName);
            }
            catch (Exception ex)
            {
                return McpResponse.Err("DataViewOperationFailed", ex.Message,
                    "No lifecycle action was run. Inspect the pair and retry with the latest version token.",
                    target: request.TransactionName);
            }
        }

        private static Request Parse(JObject args)
        {
            var r = new Request
            {
                Action = (args["action"]?.ToString() ?? "inspect").Trim().ToLowerInvariant(),
                TransactionName = args["transaction"]?.ToString()?.Trim(),
                DataViewName = args["dataViewName"]?.ToString()?.Trim(),
                DataStoreName = (args["dataStore"]?.ToString() ?? "Default").Trim(),
                Schema = args["schema"]?.ToString()?.Trim(),
                TableName = args["table"]?.ToString()?.Trim(),
                Updatable = args["updatable"]?.ToObject<bool?>() ?? true,
                DryRun = args["dryRun"]?.ToObject<bool?>() ?? false,
                RollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true,
                ExpectedVersion = args["expectedVersion"]?.ToString()?.Trim()
            };

            if (!new[] { "inspect", "dry_run", "create", "update", "delete" }.Contains(r.Action))
                r.Errors.Add(FieldError("action", "action must be inspect | dry_run | create | update | delete."));
            if (string.IsNullOrWhiteSpace(r.TransactionName))
                r.Errors.Add(FieldError("transaction", "transaction is required."));
            else if (!Regex.IsMatch(r.TransactionName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                r.Errors.Add(FieldError("transaction", "transaction must be a valid GeneXus object name."));
            if (string.IsNullOrWhiteSpace(r.DataViewName))
                r.Errors.Add(FieldError("dataViewName", "dataViewName is required."));
            else if (!Regex.IsMatch(r.DataViewName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                r.Errors.Add(FieldError("dataViewName", "dataViewName must be a valid GeneXus object name."));

            bool needsDefinition = r.Action == "dry_run" || r.Action == "create" || r.Action == "update";
            if (needsDefinition)
            {
                if (string.IsNullOrWhiteSpace(r.DataStoreName)) r.Errors.Add(FieldError("dataStore", "dataStore is required."));
                if (string.IsNullOrWhiteSpace(r.Schema)) r.Errors.Add(FieldError("schema", "schema is required."));
                if (string.IsNullOrWhiteSpace(r.TableName)) r.Errors.Add(FieldError("table", "table is required."));
                if (!r.Updatable) r.Errors.Add(FieldError("updatable", "A Transaction-backed Data View must be updatable; pass updatable=true."));

                var mappings = args["attributeMappings"] as JArray;
                if (mappings == null || mappings.Count == 0)
                    r.Errors.Add(FieldError("attributeMappings", "At least one attribute mapping is required."));
                else
                {
                    var attributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < mappings.Count; i++)
                    {
                        var item = mappings[i] as JObject;
                        string attribute = item?["attribute"]?.ToString()?.Trim();
                        string column = item?["column"]?.ToString()?.Trim();
                        bool key = item?["key"]?.ToObject<bool?>() ?? false;
                        if (string.IsNullOrWhiteSpace(attribute)) r.Errors.Add(FieldError("attributeMappings[" + i + "].attribute", "attribute is required."));
                        if (string.IsNullOrWhiteSpace(column)) r.Errors.Add(FieldError("attributeMappings[" + i + "].column", "column is required."));
                        if (!string.IsNullOrEmpty(attribute) && !attributeNames.Add(attribute)) r.Errors.Add(FieldError("attributeMappings[" + i + "].attribute", "attribute names must be unique."));
                        if (!string.IsNullOrEmpty(column) && !columnNames.Add(column)) r.Errors.Add(FieldError("attributeMappings[" + i + "].column", "column names must be unique."));
                        r.Mappings.Add(new Mapping { AttributeName = attribute, ColumnName = column, IsKey = key });
                    }
                    if (!r.Mappings.Any(m => m.IsKey))
                        r.Errors.Add(FieldError("attributeMappings", "At least one mapping must have key=true."));
                    bool sawNonKey = false;
                    for (int i = 0; i < r.Mappings.Count; i++)
                    {
                        if (!r.Mappings[i].IsKey) sawNonKey = true;
                        else if (sawNonKey) r.Errors.Add(FieldError("attributeMappings[" + i + "].key", "Key mappings must precede non-key mappings."));
                    }
                }
            }
            return r;
        }

        private string ResolveAndValidatePhysicalModel(Request request, KBModel model, out Table sourceTable, out GxDataStore dataStore)
        {
            sourceTable = Table.Get(model, request.TableName);
            dataStore = model.Parts.Get<DataStoresPart>()?.GetDataStore(request.DataStoreName);
            var errors = new JArray();
            if (sourceTable == null)
                errors.Add(FieldError("table", "Existing GeneXus table metadata was not found for '" + request.TableName + "'."));
            if (dataStore == null)
                errors.Add(FieldError("dataStore", "Data store '" + request.DataStoreName + "' was not found."));
            else if (dataStore.Dbms == 0)
                errors.Add(FieldError("dataStore", "The selected data store has no DBMS definition."));

            if (sourceTable != null)
            {
                foreach (Mapping mapping in request.Mappings)
                {
                    GxAttribute attribute = model.GetObjects<GxAttribute>().GetByName(mapping.AttributeName).FirstOrDefault();
                    if (attribute == null)
                    {
                        errors.Add(FieldError("attributeMappings", "Global attribute '" + mapping.AttributeName + "' does not exist."));
                        continue;
                    }
                    TableAttribute tableAttribute = sourceTable.TableStructure.GetAttribute(attribute);
                    if (tableAttribute == null)
                    {
                        errors.Add(FieldError("attributeMappings", "Attribute '" + mapping.AttributeName + "' is not part of existing table '" + request.TableName + "'."));
                        continue;
                    }
                    if (attribute.Formula != null)
                        errors.Add(FieldError("attributeMappings", "Formula attribute '" + mapping.AttributeName + "' cannot be mapped as a stored column."));
                    if (mapping.IsKey != tableAttribute.IsKey)
                        errors.Add(FieldError("attributeMappings", "key for '" + mapping.AttributeName + "' does not match the existing table key metadata."));
                    mapping.Attribute = attribute;
                }

                var requestedKeys = new HashSet<int>(request.Mappings.Where(m => m.IsKey && m.Attribute != null).Select(m => m.Attribute.Id));
                var physicalKeys = new HashSet<int>(sourceTable.TableStructure.PrimaryKey.Select(k => k.Id));
                if (!requestedKeys.SetEquals(physicalKeys))
                    errors.Add(FieldError("attributeMappings", "All and only the existing table primary-key attributes must be mapped with key=true."));
            }

            if (errors.Count == 0) return null;
            return McpResponse.Err(
                "DataViewValidationFailed",
                "The Data View definition has validation errors; nothing was saved.",
                "Correct the field errors and run action=dry_run again.",
                target: request.TransactionName,
                errorExtra: new JObject { ["fieldErrors"] = errors });
        }

        private string DryRun(Request request, string beforeVersion, Table sourceTable, GxDataStore dataStore)
        {
            return McpResponse.Ok(request.TransactionName, "DataViewDryRun", new JObject
            {
                ["action"] = request.Action == "update" ? "update" : "create",
                ["persisted"] = false,
                ["mutationDetected"] = false,
                ["transaction"] = request.TransactionName,
                ["dataViewName"] = request.DataViewName,
                ["businessComponent"] = true,
                ["rootOnly"] = true,
                ["updatable"] = true,
                ["physicalTable"] = PhysicalName(request),
                ["logicalSourceTable"] = sourceTable.Name,
                ["dataStore"] = dataStore.Category.Name,
                ["attributeMappings"] = MappingJson(request.Mappings),
                ["version"] = beforeVersion,
                ["newTables"] = new JArray(),
                ["newIndexes"] = new JArray(),
                ["reorgRequired"] = false,
                ["ddl"] = new JArray(),
                ["implicitLifecycleActions"] = new JArray(),
                ["note"] = "The preview only read persisted metadata; KBObject.Create, Save, Specify, Generate, Build, Rebuild, Reorg, Publish, Run, and Test were not called."
            });
        }

        private string Create(Request request, string beforeVersion, Table sourceTable, GxDataStore dataStore)
        {
            var kb = _kbService.GetKB();
            var model = kb.DesignModel;
            bool committed = false;
            string failure = null;
            using (var tx = kb.BeginTransaction())
            {
                try
                {
                    if (FindTransaction(model, request.TransactionName) != null || FindDataView(model, request.DataViewName) != null)
                        throw new InvalidOperationException("Concurrent creation detected: a target object now exists.");
                    string current = ComputeVersion(request, null, null, sourceTable);
                    if (!string.IsNullOrEmpty(request.ExpectedVersion) && !string.Equals(current, request.ExpectedVersion, StringComparison.Ordinal))
                        throw new InvalidOperationException("Concurrent modification detected before the first save.");

                    Transaction trn = Transaction.Create(model);
                    trn.Name = request.TransactionName;
                    trn.Module = model.RootModule;
                    trn.SetPropertyValue("idISBUSINESSCOMPONENT", true);
                    TrySetProperty(trn, "IntegratedSecurityLevel", "SecurityNone");
                    AddRootAttributes(trn, request.Mappings);
                    trn.Save(new KBObjectSavePreferences { ForceSave = true, ForceSaveDefaultParts = true, SkipValidation = false });

                    Table logicalTable = trn.Structure.Root.AssociatedTable;
                    if (logicalTable == null) throw new InvalidOperationException("The SDK did not create the Transaction's logical table metadata.");

                    DataView dv = BuildDataView(model, request, dataStore, logicalTable);
                    dv.Save(new KBObjectSavePreferences { ForceSave = true, ForceSaveDefaultParts = true, SkipValidation = false });
                    VerifyPair(request, trn, dv, logicalTable);

                    tx.Commit();
                    committed = true;
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                    try { tx.Rollback(); } catch (Exception rollbackEx) { failure += " Rollback: " + rollbackEx.Message; }
                }
            }

            Transaction persistedTrn = FindTransaction(model, request.TransactionName);
            DataView persistedDv = FindDataView(model, request.DataViewName);
            if (!committed)
            {
                bool clean = persistedTrn == null && persistedDv == null;
                return McpResponse.Err("DataViewCreateFailed", failure ?? "Atomic create failed.",
                    clean ? "The SDK transaction was rolled back; correct the input and retry." : "Rollback verification found persisted targets; inspect them before retrying.",
                    target: request.TransactionName,
                    errorExtra: new JObject
                    {
                        ["rolledBack"] = clean,
                        ["orphanTransaction"] = persistedTrn != null,
                        ["orphanDataView"] = persistedDv != null,
                        ["implicitLifecycleActions"] = new JArray()
                    });
            }

            JObject verified = VerifyPersisted(request, persistedTrn, persistedDv);
            if (!(verified["confirmed"]?.ToObject<bool>() ?? false))
                return McpResponse.Err("DataViewVerificationFailed", "The atomic save committed, but persisted reread did not match the requested pair.",
                    "Inspect the returned verification details before using the Transaction.", target: request.TransactionName,
                    errorExtra: new JObject { ["verification"] = verified });

            string afterVersion = ComputeVersion(request, persistedTrn, persistedDv, sourceTable);
            TryUpdateIndex(persistedTrn, persistedDv);
            return McpResponse.Ok(request.TransactionName, "DataViewCreated", SuccessPayload(request, beforeVersion, afterVersion, persistedTrn, persistedDv, verified));
        }

        private string Update(Request request, string beforeVersion, Transaction transaction, DataView dataView, Table sourceTable, GxDataStore dataStore)
        {
            JArray existingNames = new JArray(transaction.Structure.Root.Attributes.Select(a => a.Name));
            var requestedNames = request.Mappings.Select(m => m.AttributeName).ToArray();
            if (transaction.Structure.Root.Levels.Count != 0 || !transaction.Structure.Root.Attributes.Select(a => a.Name).SequenceEqual(requestedNames, StringComparer.OrdinalIgnoreCase))
                return McpResponse.Err("DataViewStructureChangeUnsupported",
                    "action=update does not add, remove, reorder, or nest Transaction attributes; nothing was changed.",
                    "Create a new atomic pair for a different root structure. Updating Data View columns/properties is supported when the root structure is unchanged.",
                    target: request.TransactionName,
                    errorExtra: new JObject { ["persistedRootAttributes"] = existingNames, ["requestedRootAttributes"] = new JArray(requestedNames) });

            var kb = _kbService.GetKB();
            var model = kb.DesignModel;
            bool committed = false;
            string failure = null;
            using (var tx = kb.BeginTransaction())
            {
                try
                {
                    string current = ComputeVersion(request, transaction, dataView, sourceTable);
                    if (!string.IsNullOrEmpty(request.ExpectedVersion) && !string.Equals(current, request.ExpectedVersion, StringComparison.Ordinal))
                        throw new InvalidOperationException("Concurrent modification detected before the first save.");
                    transaction.SetPropertyValue("idISBUSINESSCOMPONENT", true);
                    transaction.Save(new KBObjectSavePreferences { ForceSave = true, ForceSaveDefaultParts = true, SkipValidation = false });
                    Table logicalTable = transaction.Structure.Root.AssociatedTable;
                    ConfigureDataView(dataView, request, dataStore, logicalTable);
                    dataView.Save(new KBObjectSavePreferences { ForceSave = true, ForceSaveDefaultParts = true, SkipValidation = false });
                    VerifyPair(request, transaction, dataView, logicalTable);
                    tx.Commit();
                    committed = true;
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                    try { tx.Rollback(); } catch (Exception rollbackEx) { failure += " Rollback: " + rollbackEx.Message; }
                }
            }
            if (!committed)
                return McpResponse.Err("DataViewUpdateFailed", failure ?? "Atomic update failed.",
                    "The SDK transaction was rolled back; reread the pair and retry with its current version.", target: request.TransactionName,
                    errorExtra: new JObject { ["rolledBack"] = true, ["implicitLifecycleActions"] = new JArray() });

            Transaction persistedTrn = FindTransaction(model, request.TransactionName);
            DataView persistedDv = FindDataView(model, request.DataViewName);
            JObject verified = VerifyPersisted(request, persistedTrn, persistedDv);
            string afterVersion = ComputeVersion(request, persistedTrn, persistedDv, sourceTable);
            TryUpdateIndex(persistedTrn, persistedDv);
            return McpResponse.Ok(request.TransactionName, "DataViewUpdated", SuccessPayload(request, beforeVersion, afterVersion, persistedTrn, persistedDv, verified));
        }

        private string Delete(Request request, Transaction transaction, DataView dataView)
        {
            if (transaction == null || dataView == null)
                return McpResponse.Err("ObjectNotFound", "Both the Transaction and Data View are required for atomic delete.", target: request.TransactionName);
            Table logicalTable = transaction.Structure.Root.AssociatedTable;
            if (logicalTable == null || dataView.AssociatedTableKey == null || dataView.AssociatedTableKey.Id != logicalTable.Id)
                return McpResponse.Err("DataViewPairMismatch", "The Data View is not associated with the Transaction's logical table; refusing to delete unrelated objects.", target: request.TransactionName);

            var model = transaction.Model;
            string beforeVersion = ComputeVersion(request, transaction, dataView, logicalTable);
            string concurrency = ValidateExpectedVersion(request, beforeVersion);
            if (concurrency != null) return concurrency;
            bool committed = false;
            string failure = null;
            using (var tx = transaction.KB.BeginTransaction())
            {
                try
                {
                    dataView.Delete();
                    transaction.Delete();
                    tx.Commit();
                    committed = true;
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                    try { tx.Rollback(); } catch (Exception rollbackEx) { failure += " Rollback: " + rollbackEx.Message; }
                }
            }
            Transaction remainingTrn = FindTransaction(model, request.TransactionName);
            DataView remainingDv = FindDataView(model, request.DataViewName);
            if (!committed || remainingTrn != null || remainingDv != null)
                return McpResponse.Err("DataViewDeleteFailed", failure ?? "Persisted reread found an object after delete.",
                    "The SDK transaction was rolled back when deletion failed.", target: request.TransactionName,
                    errorExtra: new JObject { ["rolledBack"] = !committed, ["transactionExists"] = remainingTrn != null, ["dataViewExists"] = remainingDv != null });
            return McpResponse.Ok(request.TransactionName, "DataViewDeleted", new JObject
            {
                ["persisted"] = true,
                ["beforeVersion"] = beforeVersion,
                ["afterVersion"] = null,
                ["transactionRemoved"] = true,
                ["dataViewRemoved"] = true,
                ["globalAttributesRemoved"] = new JArray(),
                ["physicalTablesRemoved"] = new JArray(),
                ["implicitLifecycleActions"] = new JArray()
            });
        }

        private string DeleteDryRun(Request request, Transaction transaction, DataView dataView)
        {
            if (transaction == null || dataView == null)
                return McpResponse.Err("ObjectNotFound", "Both the Transaction and Data View are required for an atomic delete preview.", target: request.TransactionName);
            Table logicalTable = transaction.Structure.Root.AssociatedTable;
            if (logicalTable == null || dataView.AssociatedTableKey == null || dataView.AssociatedTableKey.Id != logicalTable.Id)
                return McpResponse.Err("DataViewPairMismatch", "The Data View is not associated with the Transaction's logical table; refusing to preview deletion of unrelated objects.", target: request.TransactionName);

            string version = ComputeVersion(request, transaction, dataView, logicalTable);
            string concurrency = ValidateExpectedVersion(request, version);
            if (concurrency != null) return concurrency;
            return McpResponse.Ok(request.TransactionName, "DataViewDeleteDryRun", new JObject
            {
                ["action"] = "delete",
                ["persisted"] = false,
                ["mutationDetected"] = false,
                ["version"] = version,
                ["transactionWouldBeRemoved"] = true,
                ["dataViewWouldBeRemoved"] = true,
                ["globalAttributesWouldBeRemoved"] = new JArray(),
                ["physicalTablesWouldBeRemoved"] = new JArray(),
                ["implicitLifecycleActions"] = new JArray()
            });
        }

        private string Inspect(Request request, Transaction transaction, DataView dataView)
        {
            if (transaction == null && dataView == null)
                return McpResponse.Err("ObjectNotFound", "Neither the Transaction nor Data View exists.", target: request.TransactionName);
            Table table = transaction?.Structure?.Root?.AssociatedTable;
            var result = new JObject
            {
                ["transactionExists"] = transaction != null,
                ["dataViewExists"] = dataView != null,
                ["transaction"] = transaction?.Name,
                ["dataViewName"] = dataView?.Name,
                ["businessComponent"] = transaction?.IsBusinessComponent,
                ["rootOnly"] = transaction != null && transaction.Structure.Root.Levels.Count == 0,
                ["rootAttributes"] = transaction == null ? new JArray() : new JArray(transaction.Structure.Root.Attributes.Select(a => new JObject { ["attribute"] = a.Name, ["key"] = a.IsKey })),
                ["associatedLogicalTable"] = table?.Name,
                ["associatedTableVerified"] = table != null && dataView?.AssociatedTableKey != null && dataView.AssociatedTableKey.Id == table.Id,
                ["attributeMappings"] = dataView == null ? new JArray() : new JArray(dataView.DataViewStructure.Attributes.Select(a => new JObject { ["attribute"] = a.Attribute?.Name, ["column"] = a.ExternalName })),
                ["implicitLifecycleActions"] = new JArray()
            };
            if (dataView?.DataViewStructure?.Platforms?.FirstOrDefault() is DataViewStructurePlatform platform)
            {
                result["physicalTable"] = JoinPhysical(platform.Properties.GetPropertyValue<string>("SCHEMA"), platform.Properties.GetPropertyValue<string>("NAME"));
                result["schema"] = platform.Properties.GetPropertyValue<string>("SCHEMA");
                result["table"] = platform.Properties.GetPropertyValue<string>("NAME");
            }
            result["version"] = ComputeVersion(request, transaction, dataView, table);
            return McpResponse.Ok(request.TransactionName, "DataViewInspected", result);
        }

        private static DataView BuildDataView(KBModel model, Request request, GxDataStore dataStore, Table logicalTable)
        {
            DataView dataView = DataView.Create(model);
            dataView.Name = request.DataViewName;
            dataView.Module = model.RootModule;
            ConfigureDataView(dataView, request, dataStore, logicalTable);
            return dataView;
        }

        private static void ConfigureDataView(DataView dataView, Request request, GxDataStore dataStore, Table logicalTable)
        {
            dataView.SetPropertyValue("DVAssocTable", new KBObjectReference(logicalTable));
            dataView.SetPropertyValue("DVDataStore", new DataStoreCategoryReference { Definition = dataStore });
            dataView.DataViewStructure.Attributes.Clear();
            foreach (Mapping mapping in request.Mappings)
            {
                var item = new DataViewAttribute(dataView.DataViewStructure, mapping.Attribute) { ExternalName = mapping.ColumnName };
                dataView.DataViewStructure.Attributes.Add(item);
            }
            dataView.DataViewStructure.Platforms.Clear();
            var platform = new DataViewStructurePlatform(dataView.Model) { Dbms = dataStore.Dbms };
            platform.Properties.SetPropertyValue("NAME", request.TableName);
            platform.Properties.SetPropertyValue("SCHEMA", request.Schema);
            platform.Properties.SetPropertyValue("LOC", "");
            platform.Properties.SetPropertyValue("EXTNAM", "YES");
            dataView.DataViewStructure.Platforms.Add(platform);
        }

        private static void AddRootAttributes(Transaction transaction, IEnumerable<Mapping> mappings)
        {
            if (transaction.Structure?.Root == null) throw new InvalidOperationException("The SDK returned a Transaction without a root level.");
            foreach (Mapping mapping in mappings)
            {
                TransactionAttribute item = transaction.Structure.Root.AddAttribute(mapping.Attribute);
                item.IsKey = mapping.IsKey;
            }
        }

        private static void VerifyPair(Request request, Transaction transaction, DataView dataView, Table logicalTable)
        {
            if (!transaction.IsBusinessComponent) throw new InvalidOperationException("Business Component property did not persist in memory.");
            if (transaction.Structure.Root.Levels.Count != 0) throw new InvalidOperationException("The Transaction contains a nested level.");
            if (!transaction.Structure.Root.Attributes.Select(a => a.Name).SequenceEqual(request.Mappings.Select(m => m.AttributeName), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Transaction root structure differs from the requested mappings.");
            if (dataView.AssociatedTableKey == null || dataView.AssociatedTableKey.Id != logicalTable.Id)
                throw new InvalidOperationException("The Data View is not associated with the Transaction's logical table.");
            if (!dataView.DataViewStructure.Attributes.Select(a => a.Attribute?.Name).SequenceEqual(request.Mappings.Select(m => m.AttributeName), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Data View attribute list differs from the requested mappings.");
        }

        private JObject VerifyPersisted(Request request, Transaction transaction, DataView dataView)
        {
            var result = new JObject { ["confirmed"] = false };
            if (transaction == null || dataView == null)
            {
                result["reason"] = "Transaction or Data View missing after reread.";
                return result;
            }
            Table logicalTable = transaction.Structure.Root.AssociatedTable;
            bool bc = transaction.IsBusinessComponent;
            bool rootOnly = transaction.Structure.Root.Levels.Count == 0;
            bool attrs = transaction.Structure.Root.Attributes.Select(a => a.Name).SequenceEqual(request.Mappings.Select(m => m.AttributeName), StringComparer.OrdinalIgnoreCase);
            bool assoc = logicalTable != null && dataView.AssociatedTableKey != null && dataView.AssociatedTableKey.Id == logicalTable.Id;
            bool mappings = dataView.DataViewStructure.Attributes.Select(a => new { Name = a.Attribute?.Name, a.ExternalName })
                .SequenceEqual(request.Mappings.Select(m => new { Name = m.AttributeName, ExternalName = m.ColumnName }));
            DataViewStructurePlatform platform = dataView.DataViewStructure.Platforms.FirstOrDefault();
            bool physical = platform != null
                && string.Equals(platform.Properties.GetPropertyValue<string>("NAME"), request.TableName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(platform.Properties.GetPropertyValue<string>("SCHEMA"), request.Schema, StringComparison.OrdinalIgnoreCase);
            result["businessComponent"] = bc;
            result["rootOnly"] = rootOnly;
            result["rootAttributes"] = attrs;
            result["associatedTable"] = assoc;
            result["attributeMappings"] = mappings;
            result["physicalMapping"] = physical;
            result["confirmed"] = bc && rootOnly && attrs && assoc && mappings && physical;
            return result;
        }

        private static JObject SuccessPayload(Request request, string beforeVersion, string afterVersion, Transaction transaction, DataView dataView, JObject verified)
        {
            return new JObject
            {
                ["persisted"] = true,
                ["mutationDetected"] = true,
                ["transaction"] = transaction?.Name,
                ["dataViewName"] = dataView?.Name,
                ["physicalTable"] = PhysicalName(request),
                ["businessComponent"] = true,
                ["rootOnly"] = true,
                ["updatable"] = true,
                ["beforeVersion"] = beforeVersion,
                ["afterVersion"] = afterVersion,
                ["version"] = afterVersion,
                ["reread"] = verified,
                ["newTables"] = new JArray(),
                ["newIndexes"] = new JArray(),
                ["reorgRequired"] = false,
                ["ddl"] = new JArray(),
                ["implicitLifecycleActions"] = new JArray()
            };
        }

        private static string ComputeVersion(Request request, Transaction transaction, DataView dataView, Table sourceTable)
        {
            var text = new StringBuilder();
            text.Append(request.TransactionName).Append('|').Append(request.DataViewName).Append('|');
            if (transaction != null || dataView != null)
            {
                if (transaction != null) text.Append(transaction.SerializeToXml());
                if (dataView != null) text.Append(dataView.SerializeToXml());
            }
            else
            {
                text.Append(request.DataStoreName).Append('|').Append(request.Schema).Append('|').Append(request.TableName).Append('|');
                if (sourceTable != null) text.Append(sourceTable.SerializeToXml());
                foreach (Mapping mapping in request.Mappings)
                {
                    text.Append('|').Append(mapping.AttributeName).Append('=').Append(mapping.ColumnName).Append(':').Append(mapping.IsKey);
                    if (mapping.Attribute != null) text.Append(':').Append(mapping.Attribute.SerializeToXml());
                }
            }
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
                return string.Concat(hash.Select(b => b.ToString("x2")));
            }
        }

        private static string ValidateExpectedVersion(Request request, string currentVersion)
        {
            if (string.IsNullOrEmpty(request.ExpectedVersion) || string.Equals(request.ExpectedVersion, currentVersion, StringComparison.Ordinal)) return null;
            return McpResponse.Err("ConcurrentModification",
                "The Data View baseline changed since expectedVersion was captured; nothing was saved.",
                "Run action=inspect or action=dry_run and retry with the returned version.", target: request.TransactionName,
                errorExtra: new JObject { ["expectedVersion"] = request.ExpectedVersion, ["currentVersion"] = currentVersion });
        }

        private static JObject FieldError(string field, string message)
            => new JObject { ["field"] = field, ["errors"] = new JArray(message) };

        private static string ValidationError(Request request)
            => McpResponse.Err("DataViewValidationFailed", "The Data View request has validation errors; nothing was saved.",
                "Correct the field errors and retry.", target: request.TransactionName,
                errorExtra: new JObject { ["fieldErrors"] = request.Errors });

        private static JArray MappingJson(IEnumerable<Mapping> mappings)
            => new JArray(mappings.Select(m => new JObject
            {
                ["attribute"] = m.AttributeName,
                ["column"] = m.ColumnName,
                ["key"] = m.IsKey,
                ["type"] = m.Attribute == null ? null : m.Attribute.Type.ToString()
            }));

        private static string PhysicalName(Request request) => JoinPhysical(request.Schema, request.TableName);
        private static string JoinPhysical(string schema, string table) => string.IsNullOrWhiteSpace(schema) ? table : schema + "." + table;

        private static Transaction FindTransaction(KBModel model, string name)
            => string.IsNullOrWhiteSpace(name) ? null : model.GetObjects<Transaction>().GetByName(name).FirstOrDefault();

        private static DataView FindDataView(KBModel model, string name)
            => string.IsNullOrWhiteSpace(name) ? null : model.GetObjects<DataView>().GetByName(name).FirstOrDefault();

        private static void TrySetProperty(KBObject obj, string name, object value)
        {
            try { obj.SetPropertyValue(name, value); } catch { }
        }

        private void TryUpdateIndex(params KBObject[] objects)
        {
            try
            {
                foreach (KBObject obj in objects) if (obj != null) _kbService.GetIndexCache()?.UpdateEntry(obj);
            }
            catch { }
        }
    }
}
