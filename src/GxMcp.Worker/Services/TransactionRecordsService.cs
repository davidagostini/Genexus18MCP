using System;
using System.Collections.Generic;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Artech.Genexus.Common.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Typed CRUD for application rows represented by a GeneXus Transaction.
    ///
    /// The SDK remains the source of truth for the table, attributes, keys and
    /// scalar types. Values are always parameters; callers cannot inject a table,
    /// column or predicate. Writes use a serializable ADO transaction and a
    /// compensating, verified rollback when the post-commit reread diverges.
    /// </summary>
    public sealed class TransactionRecordsService
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 1000;
        private const int DefaultCommandTimeoutSeconds = 15;

        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public TransactionRecordsService(KbService kbService, ObjectService objectService)
        {
            _kbService = kbService;
            _objectService = objectService;
        }

        public string Execute(string action, string target, JObject args)
        {
            try
            {
                args = args ?? new JObject();
                string transactionName = FirstText(args, "transaction", "name") ?? target;
                if (string.IsNullOrWhiteSpace(transactionName))
                    return Error("TransactionRequired", "Transaction name is required.", "Provide transaction or name.");

                var transaction = _objectService?.FindObject(transactionName, "Transaction") as Transaction;
                if (transaction == null)
                    return Error("TransactionNotFound", "The requested Transaction could not be resolved by the SDK.", "Read the Transaction with type=Transaction and retry.", transactionName);

                var metadata = ReadMetadata(transaction);
                if (metadata.Attributes.Count == 0)
                    return Error("TransactionSchemaUnavailable", "The SDK returned no root-level attributes for the Transaction.", "The operation currently supports the root level of a Transaction.", transaction.Name);

                if (string.Equals(action, "QueryRecords", StringComparison.OrdinalIgnoreCase))
                    return Query(metadata, args);
                if (string.Equals(action, "InsertRecord", StringComparison.OrdinalIgnoreCase))
                    return Write(metadata, args, isInsert: true);
                if (string.Equals(action, "UpdateRecords", StringComparison.OrdinalIgnoreCase))
                    return Write(metadata, args, isInsert: false);

                return Error("InvalidTransactionRecordsAction", "Unsupported Transaction records action.", "Use records_query, records_insert or records_update.", transaction.Name);
            }
            catch (RecordOperationException ex)
            {
                return Error(ex.Code, ex.Message, ex.Hint, target, ex.Extra);
            }
            catch (DbException ex)
            {
                Logger.Error("[TRANSACTION-RECORDS] datastore failure: " + ex.Message);
                return Error("TransactionRecordsDatabaseFailed", "The datastore rejected the Transaction records operation.", "Check the active datastore and retry; no GeneXus lifecycle action was run.", target,
                    new JObject
                    {
                        ["persisted"] = false,
                        ["rereadConfirmed"] = false,
                        ["diagnostic"] = "The database provider returned an error; connection details were omitted."
                    });
            }
            catch (Exception ex)
            {
                Logger.Error("[TRANSACTION-RECORDS] unexpected failure: " + ex.Message);
                return Error("TransactionRecordsFailed", "The Transaction records operation failed.", "Inspect the datastore diagnostics and retry with a fresh version token.", target,
                    new JObject { ["diagnostic"] = "Unexpected backend failure; sensitive connection details were omitted." });
            }
        }

        private string Query(TransactionMetadata metadata, JObject args)
        {
            var filterObject = ReadObject(args, "where", "filters");
            var filters = filterObject == null ? null : NormalizeValues(metadata, filterObject);
            var fields = ResolveFields(metadata, args["fields"] as JArray);
            int limit = ClampLimit(args["limit"]?.Value<int?>() ?? DefaultLimit);
            var db = OpenDatabase(args);
            using (var connection = db.Factory.CreateConnection())
            {
                db.Bind(metadata);
                connection.ConnectionString = db.ConnectionString;
                connection.Open();
                using (var command = BuildSelect(connection, metadata, fields, filters, limit, null, db))
                {
                    command.CommandTimeout = ReadTimeout(args);
                    var rows = ReadRows(command, fields);
                    var result = BuildReadResult(metadata, db, fields, filters, rows, limit);
                    return McpResponse.Ok(target: metadata.Name, code: "TransactionRecordsRead", result: result);
                }
            }
        }

        private string Write(TransactionMetadata metadata, JObject args, bool isInsert)
        {
            bool dryRun = args["dryRun"] == null || args["dryRun"].Value<bool>();
            bool rollbackOnFailure = args["rollbackOnFailure"]?.Value<bool?>() ?? true;
            string expectedVersion = FirstText(args, "expectedVersion", "versionToken");
            var filters = ReadObject(args, "where", "filters");
            var values = ReadObject(args, "values", "data", "record");
            if (values == null || values.Count == 0)
                throw new RecordOperationException("RecordValuesRequired", "Record values are required.", "Provide values as an object.");
            if (!isInsert && (filters == null || filters.Count == 0))
                throw new RecordOperationException("UpdateFilterRequired", "An update requires an explicit equality filter.", "Provide where and keep expectedCount=1 unless a broader update is intentional.");
            if (!IsWriteAllowed(dryRun, expectedVersion))
                throw new RecordOperationException("ExpectedVersionRequired", "A version token is required for a persisted write.", "Run the same operation with dryRun=true and send its versionToken back as expectedVersion.");

            var normalizedValues = NormalizeValues(metadata, values);
            var normalizedFilters = filters == null ? null : NormalizeValues(metadata, filters);
            if (metadata.Keys.Count == 0)
                throw new RecordOperationException("TransactionPrimaryKeyUnavailable", "The Transaction does not expose a primary key in the SDK structure.", "Record writes are refused because they cannot be rolled back safely.");
            if (!isInsert)
            {
                foreach (var key in metadata.Keys)
                {
                    if (normalizedValues.ContainsKey(key.Name))
                        throw new RecordOperationException("KeyMutationNotSupported", "Primary-key changes are not supported by the atomic update operation.", "Update only non-key attributes so rollback can identify the original row.");
                }
            }

            var db = OpenDatabase(args);
            using (var connection = db.Factory.CreateConnection())
            {
                db.Bind(metadata);
                connection.ConnectionString = db.ConnectionString;
                connection.Open();
                int timeout = ReadTimeout(args);
                List<JObject> before;
                string currentToken;
                using (var readTx = connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    var beforeFilters = isInsert ? null : normalizedFilters;
                    using (var select = BuildSelect(connection, metadata, metadata.Attributes, beforeFilters, isInsert ? 0 : 0, readTx, db))
                    {
                        select.CommandTimeout = timeout;
                        before = ReadRows(select, metadata.Attributes);
                    }
                    currentToken = ComputeVersionToken(metadata, normalizedFilters, before);
                    readTx.Commit();
                }

                if (!string.IsNullOrWhiteSpace(expectedVersion) && !string.Equals(expectedVersion, currentToken, StringComparison.Ordinal))
                {
                    throw new RecordOperationException(
                        "VersionConflict",
                        "The datastore changed after the supplied version token was issued.",
                        "Re-read the records, review the diff and retry with the current versionToken.",
                        new JObject { ["expectedVersion"] = expectedVersion, ["currentVersion"] = currentToken, ["persisted"] = false });
                }

                int expectedCount = args["expectedCount"]?.Value<int?>() ?? (isInsert ? 0 : 1);
                if (!isInsert && expectedCount != 1)
                    throw new RecordOperationException("SingleRowUpdateRequired", "The atomic adapter currently updates exactly one primary-keyed record.", "Use a unique where filter and expectedCount=1.");
                if (!isInsert && before.Count != expectedCount)
                    throw new RecordOperationException("ExpectedCountMismatch", "The update matched a different number of records than expected.", "Review where and expectedCount before retrying.",
                        new JObject { ["expectedCount"] = expectedCount, ["matchedCount"] = before.Count, ["persisted"] = false });

                var dryRunResult = BuildDryRunResult(metadata, db, isInsert, normalizedFilters, normalizedValues, before, currentToken, expectedCount, rollbackOnFailure);
                if (dryRun)
                    return McpResponse.Ok(target: metadata.Name, code: "TransactionRecordDryRun", result: dryRunResult);

                var snapshot = before.Select(CloneRow).ToList();
                List<JObject> expectedAfter;
                using (var writeTx = connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    // Re-check inside the write transaction. This closes the gap between
                    // the optimistic read and the mutation when another writer races us.
                    List<JObject> lockedBefore;
                    using (var select = BuildSelect(connection, metadata, metadata.Attributes, isInsert ? null : normalizedFilters, 0, writeTx, db))
                    {
                        select.CommandTimeout = timeout;
                        lockedBefore = ReadRows(select, metadata.Attributes);
                    }
                    string lockedToken = ComputeVersionToken(metadata, normalizedFilters, lockedBefore);
                    if (!string.Equals(lockedToken, currentToken, StringComparison.Ordinal))
                        throw new RecordOperationException("VersionConflict", "The datastore changed while the write was being prepared.", "Re-read the records and retry with a fresh versionToken.",
                            new JObject { ["expectedVersion"] = currentToken, ["currentVersion"] = lockedToken, ["persisted"] = false });
                    if (!isInsert && lockedBefore.Count != expectedCount)
                        throw new RecordOperationException("ExpectedCountMismatch", "The update matched a different number of records inside the write transaction.", "Retry after re-reading the records.");

                    JToken generatedKey = null;
                    ExecuteWrite(connection, writeTx, db, isInsert, normalizedFilters, normalizedValues, lockedBefore, out generatedKey, timeout);
                    if (isInsert && generatedKey != null)
                    {
                        if (generatedKey.Type == JTokenType.Null)
                            throw new RecordOperationException("GeneratedKeyUnavailable", "The datastore did not return the generated primary key.", "Provide the primary key in values or configure generated-key support.");
                        var generated = metadata.Keys.Single(k => !normalizedValues.ContainsKey(k.Name));
                        normalizedValues[generated.Name] = generatedKey;
                    }

                    var afterFilters = isInsert
                        ? BuildKeyFilter(metadata, normalizedValues)
                        : BuildKeyFilterForRows(metadata, lockedBefore);
                    using (var verify = BuildSelect(connection, metadata, metadata.Attributes, afterFilters, 0, writeTx, db))
                    {
                        verify.CommandTimeout = timeout;
                        expectedAfter = ReadRows(verify, metadata.Attributes);
                    }
                    if (!VerifyRows(metadata, isInsert, normalizedValues, lockedBefore, expectedAfter))
                    {
                        throw new RecordOperationException("WriteVerificationFailed", "The write did not round-trip inside the transaction.", "The transaction was rolled back before commit; no persisted change was accepted.",
                            new JObject { ["persisted"] = false, ["rereadConfirmed"] = false, ["rollbackPerformed"] = true });
                    }
                    writeTx.Commit();
                }

                var finalFilters = isInsert ? BuildKeyFilter(metadata, normalizedValues) : BuildKeyFilterForRows(metadata, snapshot);
                List<JObject> persisted;
                using (var rereadConnection = db.Factory.CreateConnection())
                {
                    rereadConnection.ConnectionString = db.ConnectionString;
                    rereadConnection.Open();
                    using (var reread = BuildSelect(rereadConnection, metadata, metadata.Attributes, finalFilters, 0, null, db))
                    {
                        reread.CommandTimeout = timeout;
                        persisted = ReadRows(reread, metadata.Attributes);
                    }
                }

                bool confirmed = VerifyRows(metadata, isInsert, normalizedValues, snapshot, persisted);
                bool rollbackAttempted = false;
                bool stateRestored = false;
                if (!confirmed && rollbackOnFailure)
                {
                    rollbackAttempted = true;
                    stateRestored = Compensate(metadata, db, isInsert, snapshot, expectedAfter, persisted, timeout);
                }
                if (!confirmed)
                {
                    throw new RecordOperationException("WriteNotPersisted", "The post-save reread diverged from the requested record state.", "No lifecycle operation was run; inspect the returned rollback status before retrying.",
                        new JObject
                        {
                            ["persisted"] = false,
                            ["rereadConfirmed"] = false,
                            ["rollbackAttempted"] = rollbackAttempted,
                            ["stateRestored"] = stateRestored,
                            ["versionToken"] = ComputeVersionToken(metadata, finalFilters, persisted)
                        });
                }

                var result = new JObject
                {
                    ["transaction"] = metadata.Name,
                    ["table"] = metadata.Table,
                    ["persisted"] = true,
                    ["rereadConfirmed"] = true,
                    ["rollbackAttempted"] = false,
                    ["stateRestored"] = true,
                    ["versionTokenBefore"] = currentToken,
                    ["versionToken"] = ComputeVersionToken(metadata, finalFilters, persisted),
                    ["matchedCount"] = persisted.Count,
                    ["records"] = new JArray(persisted),
                    ["keys"] = BuildKeys(metadata, persisted)
                };
                return McpResponse.Ok(target: metadata.Name, code: isInsert ? "TransactionRecordInserted" : "TransactionRecordsUpdated", result: result);
            }
        }

        private static void ExecuteWrite(DbConnection connection, DbTransaction tx, DatabaseMetadata db, bool isInsert, Dictionary<string, JToken> filters,
            Dictionary<string, JToken> values, List<JObject> before, out JToken generatedKey, int timeout)
        {
            generatedKey = null;
            var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandTimeout = timeout;
            try
            {
                if (isInsert)
                {
                    var missingKeys = db.Keys.Where(k => !values.ContainsKey(k.Name)).ToList();
                    if (missingKeys.Count > 1)
                        throw new RecordOperationException("GeneratedKeyUnavailable", "An insert with multiple missing primary-key values cannot be rolled back safely.", "Provide the complete primary key in values.");
                    if (missingKeys.Count == 1 && !string.Equals(db.Family, "sqlserver", StringComparison.OrdinalIgnoreCase))
                        throw new RecordOperationException("GeneratedKeyUnavailable", "This datastore cannot return a generated primary key through the native provider adapter.", "Provide the primary key in values or use a provider with generated-key support.");

                    var columns = values.Keys.Select(name => db.AttributeMap[name]).ToList();
                    string prefix = ParameterPrefix(db.Family);
                    string output = missingKeys.Count == 1 ? " OUTPUT INSERTED." + QuoteIdentifier(missingKeys[0].Name, db.Family) : string.Empty;
                    command.CommandText = "INSERT INTO " + db.QualifiedTable + " (" + string.Join(", ", columns.Select(a => QuoteIdentifier(a.Name, db.Family))) + ")" + output + " VALUES ("
                        + string.Join(", ", columns.Select((a, i) => AddParameter(command, prefix, "v" + i, a, values[a.Name]))) + ")";
                    generatedKey = missingKeys.Count == 1 ? ToJsonToken(command.ExecuteScalar()) : null;
                    if (missingKeys.Count == 0) command.ExecuteNonQuery();
                    return;
                }

                var updateColumns = values.Keys.Select(name => db.AttributeMap[name]).ToList();
                string parameterPrefix = ParameterPrefix(db.Family);
                command.CommandText = "UPDATE " + db.QualifiedTable + " SET "
                    + string.Join(", ", updateColumns.Select((a, i) => QuoteIdentifier(a.Name, db.Family) + "=" + AddParameter(command, parameterPrefix, "v" + i, a, values[a.Name])))
                    + BuildWhere(command, db, filters, parameterPrefix, "w");
                int affected = command.ExecuteNonQuery();
                if (affected != before.Count)
                    throw new RecordOperationException("ConcurrentWriteDetected", "The update affected a different number of rows than the locked snapshot.", "Retry with the versionToken returned by a fresh dry-run.");
            }
            finally { command.Dispose(); }
        }

        private static bool Compensate(TransactionMetadata metadata, DatabaseMetadata db, bool isInsert,
            List<JObject> snapshot, List<JObject> expectedAfter, List<JObject> persisted, int timeout)
        {
            try
            {
                if (persisted == null || persisted.Count == 0) return isInsert;
                using (var connection = db.Factory.CreateConnection())
                {
                    connection.ConnectionString = db.ConnectionString;
                    connection.Open();
                    using (var tx = connection.BeginTransaction(IsolationLevel.Serializable))
                    {
                        using (var currentCommand = BuildSelect(connection, metadata, metadata.Attributes, BuildKeyFilterForRows(metadata, persisted), 0, tx, db))
                        {
                            currentCommand.CommandTimeout = timeout;
                            var current = ReadRows(currentCommand, metadata.Attributes);
                            // Compensate only when the committed row still exactly
                            // matches the row observed before commit. This restores
                            // trigger/default columns too, while refusing to clobber
                            // a concurrent change made after the commit.
                            if (!RowsEquivalent(expectedAfter, current)) return false;
                            if (isInsert)
                            {
                                foreach (var row in current) ExecuteDelete(connection, tx, db, row, timeout);
                            }
                            else
                            {
                                foreach (var row in snapshot) ExecuteRestore(connection, tx, db, row, timeout);
                            }
                        }
                        tx.Commit();
                    }
                    using (var verify = BuildSelect(connection, metadata, metadata.Attributes,
                        isInsert ? BuildKeyFilterForRows(metadata, expectedAfter) : BuildKeyFilterForRows(metadata, snapshot), 0, null, db))
                    {
                        verify.CommandTimeout = timeout;
                        var rows = ReadRows(verify, metadata.Attributes);
                        return isInsert ? rows.Count == 0 : RowsEquivalent(snapshot, rows);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[TRANSACTION-RECORDS] compensating rollback failed: " + ex.Message);
                return false;
            }
        }

        private static void ExecuteDelete(DbConnection connection, DbTransaction tx, DatabaseMetadata db, JObject row, int timeout)
        {
            var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandTimeout = timeout;
            try
            {
                string prefix = ParameterPrefix(db.Family);
                command.CommandText = "DELETE FROM " + db.QualifiedTable + BuildWhere(command, db, BuildKeyFilterForRow(db, row), prefix, "d");
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Rollback delete affected an unexpected number of rows.");
            }
            finally { command.Dispose(); }
        }

        private static void ExecuteRestore(DbConnection connection, DbTransaction tx, DatabaseMetadata db, JObject row, int timeout)
        {
            var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandTimeout = timeout;
            try
            {
                var nonKeys = db.Attributes.Where(a => !a.IsKey).ToList();
                string prefix = ParameterPrefix(db.Family);
                command.CommandText = "UPDATE " + db.QualifiedTable + " SET "
                    + string.Join(", ", nonKeys.Select((a, i) => QuoteIdentifier(a.Name, db.Family) + "=" + AddParameter(command, prefix, "r" + i, a, row[a.Name])))
                    + BuildWhere(command, db, BuildKeyFilterForRow(db, row), prefix, "k");
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Rollback restore affected an unexpected number of rows.");
            }
            finally { command.Dispose(); }
        }

        private static DbCommand BuildSelect(DbConnection connection, TransactionMetadata metadata, IList<AttributeMetadata> fields,
            Dictionary<string, JToken> filters, int limit, DbTransaction tx, DatabaseMetadata db)
        {
            var command = connection.CreateCommand();
            command.Transaction = tx;
            string selectLimit = string.Empty;
            string suffix = string.Empty;
            if (limit > 0 && db.Family == "sqlserver") selectLimit = "TOP " + limit.ToString(CultureInfo.InvariantCulture) + " ";
            else if (limit > 0 && db.Family == "oracle") suffix = " FETCH FIRST " + limit.ToString(CultureInfo.InvariantCulture) + " ROWS ONLY";
            else if (limit > 0 && (db.Family == "postgres" || db.Family == "mysql")) suffix = " LIMIT " + limit.ToString(CultureInfo.InvariantCulture);
            command.CommandText = "SELECT " + selectLimit + string.Join(", ", fields.Select(a => QuoteIdentifier(a.Name, db.Family)))
                + " FROM " + db.QualifiedTable + BuildWhere(command, db, filters, ParameterPrefix(db.Family), "f") + suffix;
            return command;
        }

        private static string BuildWhere(DbCommand command, DatabaseMetadata db, Dictionary<string, JToken> filters, string prefix, string parameterStem)
        {
            if (filters == null || filters.Count == 0) return string.Empty;
            var clauses = new List<string>();
            int index = 0;
            foreach (var pair in filters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var attribute = db.AttributeMap[pair.Key];
                if (pair.Value == null || pair.Value.Type == JTokenType.Null)
                {
                    clauses.Add(QuoteIdentifier(attribute.Name, db.Family) + " IS NULL");
                }
                else
                {
                    string parameter = AddParameter(command, prefix, parameterStem + index.ToString(CultureInfo.InvariantCulture), attribute, pair.Value);
                    clauses.Add(QuoteIdentifier(attribute.Name, db.Family) + "=" + parameter);
                    index++;
                }
            }
            return " WHERE " + string.Join(" AND ", clauses);
        }

        private static List<JObject> ReadRows(DbCommand command, IList<AttributeMetadata> fields)
        {
            var rows = new List<JObject>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var row = new JObject();
                    for (int i = 0; i < fields.Count; i++) row[fields[i].Name] = ToJsonToken(reader.IsDBNull(i) ? null : reader.GetValue(i));
                    rows.Add(row);
                }
            }
            return rows;
        }

        private static JObject BuildReadResult(TransactionMetadata metadata, DatabaseMetadata db, IList<AttributeMetadata> fields,
            Dictionary<string, JToken> filters, List<JObject> rows, int limit)
        {
            return new JObject
            {
                ["transaction"] = metadata.Name,
                ["table"] = metadata.Table,
                ["dataStore"] = db.Name,
                ["fields"] = new JArray(fields.Select(a => a.Name)),
                ["records"] = new JArray(rows),
                ["matchedCount"] = rows.Count,
                ["limit"] = limit,
                ["truncated"] = limit > 0 && rows.Count >= limit,
                ["versionToken"] = ComputeVersionToken(metadata, filters, rows),
                ["keys"] = BuildKeys(metadata, rows)
            };
        }

        private static JObject BuildDryRunResult(TransactionMetadata metadata, DatabaseMetadata db, bool isInsert,
            Dictionary<string, JToken> filters, Dictionary<string, JToken> values, List<JObject> before, string version, int expectedCount, bool rollbackOnFailure)
        {
            var diff = new JObject
            {
                ["operation"] = isInsert ? "insert" : "update",
                ["matchedCount"] = isInsert ? 0 : before.Count,
                ["expectedCount"] = expectedCount,
                ["changedFields"] = new JArray(values.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["values"] = ToObject(values)
            };
            if (!isInsert) diff["keys"] = BuildKeys(metadata, before);
            return new JObject
            {
                ["transaction"] = metadata.Name,
                ["table"] = metadata.Table,
                ["dataStore"] = db.Name,
                ["action"] = isInsert ? "records_insert" : "records_update",
                ["persisted"] = false,
                ["rereadConfirmed"] = false,
                ["rollbackOnFailure"] = rollbackOnFailure,
                ["diff"] = diff,
                ["versionToken"] = version
            };
        }

        private static bool VerifyRows(TransactionMetadata metadata, bool isInsert, Dictionary<string, JToken> values,
            List<JObject> before, List<JObject> after)
        {
            if (after == null || after.Count == 0) return false;
            if (isInsert)
            {
                return after.Any(row => values.All(pair => ValueEquals(row[pair.Key], pair.Value)));
            }
            if (before == null || before.Count != after.Count) return false;
            foreach (var oldRow in before)
            {
                var current = after.FirstOrDefault(row => KeysEqual(metadata, oldRow, row));
                if (current == null) return false;
                foreach (var attribute in metadata.Attributes)
                {
                    JToken expected = values.ContainsKey(attribute.Name) ? values[attribute.Name] : oldRow[attribute.Name];
                    if (!ValueEquals(current[attribute.Name], expected)) return false;
                }
            }
            return true;
        }

        private static bool RowsEquivalent(List<JObject> expected, List<JObject> actual)
        {
            if (expected == null || actual == null || expected.Count != actual.Count) return false;
            var left = expected.Select(r => Canonical(r)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var right = actual.Select(r => Canonical(r)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return left.SequenceEqual(right, StringComparer.Ordinal);
        }

        private static bool KeysEqual(TransactionMetadata metadata, JObject left, JObject right)
            => metadata.Keys.All(k => ValueEquals(left[k.Name], right[k.Name]));

        private static JObject BuildKeys(TransactionMetadata metadata, IEnumerable<JObject> rows)
        {
            var result = new JObject();
            foreach (var key in metadata.Keys)
            {
                var values = new JArray();
                foreach (var row in rows ?? Enumerable.Empty<JObject>()) values.Add(row[key.Name]);
                result[key.Name] = values.Count == 1 ? values[0] : values;
            }
            return result;
        }

        private static Dictionary<string, JToken> BuildKeyFilter(TransactionMetadata metadata, Dictionary<string, JToken> values)
        {
            if (metadata.Keys.Any(key => !values.ContainsKey(key.Name)))
                throw new RecordOperationException("PrimaryKeyRequired", "The operation needs a complete primary key to verify and roll back the row.", "Supply every key attribute in values.");
            return metadata.Keys.ToDictionary(key => key.Name, key => values[key.Name], StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, JToken> BuildKeyFilterForRows(TransactionMetadata metadata, IEnumerable<JObject> rows)
        {
            var list = (rows ?? Enumerable.Empty<JObject>()).ToList();
            if (list.Count != 1) throw new RecordOperationException("CompositeRollbackScopeUnsupported", "Rollback requires one identifiable row per operation.", "Keep expectedCount=1 for update or provide an explicit unique key filter.");
            return BuildKeyFilterForRow(metadata, list[0]);
        }

        private static Dictionary<string, JToken> BuildKeyFilterForRow(TransactionMetadata metadata, JObject row)
            => metadata.Keys.ToDictionary(key => key.Name, key => row[key.Name], StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, JToken> BuildKeyFilterForRow(DatabaseMetadata db, JObject row)
            => db.Keys.ToDictionary(key => key.Name, key => row[key.Name], StringComparer.OrdinalIgnoreCase);

        private static TransactionMetadata ReadMetadata(Transaction transaction)
        {
            dynamic root = transaction.Structure?.Root;
            if (root == null) throw new RecordOperationException("TransactionSchemaUnavailable", "The Transaction has no root structure.", "Read the Transaction structure and retry.");
            string table = TryString(() => root.AssociatedTable?.Name) ?? transaction.Name;
            var attributes = new List<AttributeMetadata>();
            foreach (dynamic item in (IEnumerable)root.Attributes)
            {
                string name = TryString(() => item.Attribute?.Name) ?? TryString(() => item.Name);
                if (string.IsNullOrWhiteSpace(name)) continue;
                string type = TryString(() => item.Attribute?.Type?.ToString()) ?? "";
                bool isKey = TryBool(() => item.IsKey);
                int length = TryInt(() => item.Attribute?.Length);
                int decimals = TryInt(() => item.Attribute?.Decimals);
                attributes.Add(new AttributeMetadata { Name = name, Type = type, Length = length, Decimals = decimals, IsKey = isKey });
            }
            var keys = attributes.Where(a => a.IsKey).ToList();
            return new TransactionMetadata { Name = transaction.Name, Table = table, Attributes = attributes, Keys = keys };
        }

        private DatabaseMetadata OpenDatabase(JObject args)
        {
            dynamic kb = _kbService?.GetKB();
            if (kb == null) throw new RecordOperationException("KbNotOpen", "No KB is currently open.", "Open the KB before accessing Transaction records.");
            string requested = FirstText(args, "dataStore", "datastore");
            dynamic first = null;
            dynamic selected = null;
            foreach (dynamic ds in DatabaseInfoService.EnumerateViaDataStoresPart(kb))
            {
                if (ds == null) continue;
                if (first == null) first = ds;
                bool isDefault = TryBool(() => ds.IsDefault);
                string name = FirstDynamicString(ds, "Name", "Category.Name", "Type");
                if ((!string.IsNullOrWhiteSpace(requested) && string.Equals(requested, name, StringComparison.OrdinalIgnoreCase))
                    || (string.IsNullOrWhiteSpace(requested) && isDefault)) { selected = ds; break; }
            }
            if (selected == null && string.IsNullOrWhiteSpace(requested)) selected = first;
            if (selected == null)
                throw new RecordOperationException("DataStoreNotFound", "The requested GeneXus datastore was not found in the active environment.", "Use the exact dataStore name returned by the datastore inspection.", requested);

            string provider = FirstDynamicProperty(selected, "ADONET_DRIVER", "Provider", "AdoNetProvider");
            string family = DetectFamily(provider, TryInt(() => selected.Dbms));
            if (family == "unknown")
                throw new RecordOperationException("DataStoreProviderUnsupported", "The active datastore provider could not be mapped to a supported SQL dialect.", "Use SQL Server or Oracle with a registered ADO.NET provider.");
            string connectionString = FirstConnectionString(selected, "CONNECTION_STRING", "ConnectionString", "CS_CONNECTIONSTRING", "DS_DBMS_ADDINFO", "DBMS_ADDINFO");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                string server = FirstDynamicProperty(selected, "CS_SERVER", "ServerName", "Server");
                string database = FirstDynamicProperty(selected, "CS_DBNAME", "CS_DATABASE", "DBNAME", "DATABASE", "DATABASE_NAME", "DB_NAME");
                string schema = FirstDynamicProperty(selected, "CS_SCHEMA", "DatabaseSchema", "Schema");
                string user = FirstDynamicProperty(selected, "USER_ID", "UserId", "User");
                string password = FirstDynamicProperty(selected, "USER_PASSWORD", "PASSWORD", "Password");
                bool integrated = ParseYesNo(FirstDynamicProperty(selected, "TRUSTED_CONNECTION", "INTEGRATED_SECURITY", "IntegratedSecurity"));
                if (string.IsNullOrWhiteSpace(server) || (family != "oracle" && string.IsNullOrWhiteSpace(database)))
                    throw new RecordOperationException("DataStoreConnectionUnavailable", "The selected datastore does not expose enough connection metadata for a safe native record operation.", "Use a datastore with server/database metadata; credentials and connection strings are never returned by this tool.");
                if (family == "sqlserver")
                {
                    connectionString = "Server=" + server + ";Initial Catalog=" + database + ";" + (integrated ? "Integrated Security=SSPI;" : "User ID=" + user + ";Password=" + password + ";") + "Application Name=GeneXusMCP;Connect Timeout=15";
                }
                else if (family == "oracle")
                {
                    connectionString = "Data Source=" + server + ";User Id=" + user + ";Password=" + password + ";Connection Timeout=15";
                }
                else
                {
                    throw new RecordOperationException("DataStoreProviderUnsupported", "The active datastore provider is not supported by the native Transaction records adapter.", "Use SQL Server or Oracle with an ADO.NET provider registered in the worker.");
                }
            }
            var factory = ResolveFactory(provider, family);
            string schemaName = FirstDynamicProperty(selected, "CS_SCHEMA", "DatabaseSchema", "Schema");
            return new DatabaseMetadata
            {
                Name = FirstDynamicString(selected, "Name", "Category.Name", "Type") ?? "default",
                Family = family,
                Factory = factory,
                ConnectionString = connectionString,
                Schema = schemaName
            };
        }

        private static DbProviderFactory ResolveFactory(string provider, string family)
        {
            if (family == "sqlserver") return System.Data.SqlClient.SqlClientFactory.Instance;
            if (family == "oracle")
            {
                var oracle = Type.GetType("Oracle.ManagedDataAccess.Client.OracleClientFactory, Oracle.ManagedDataAccess", false);
                var instance = oracle?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) as DbProviderFactory;
                if (instance != null) return instance;
            }
            if (!string.IsNullOrWhiteSpace(provider))
            {
                try { return DbProviderFactories.GetFactory(provider); } catch { }
                var type = Type.GetType(provider, false);
                var instance = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) as DbProviderFactory;
                if (instance != null) return instance;
            }
            throw new RecordOperationException("DataStoreProviderUnavailable", "The ADO.NET provider for the selected datastore is not available in the worker process.", "Install/register the provider used by the GeneXus environment before retrying.");
        }

        private static string DetectFamily(string provider, int dbms)
        {
            string p = provider ?? string.Empty;
            if (p.IndexOf("oracle", StringComparison.OrdinalIgnoreCase) >= 0) return "oracle";
            if (p.IndexOf("sqlclient", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("sql server", StringComparison.OrdinalIgnoreCase) >= 0) return "sqlserver";
            if (p.IndexOf("mysql", StringComparison.OrdinalIgnoreCase) >= 0) return "mysql";
            if (p.IndexOf("npgsql", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("postgres", StringComparison.OrdinalIgnoreCase) >= 0) return "postgres";
            switch (dbms)
            {
                case 1: case 12: return "sqlserver";
                case 4: case 7: return "oracle";
                case 5: return "mysql";
                case 6: return "postgres";
                default: return "unknown";
            }
        }

        private static bool LooksLikeConnectionString(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('=') < 0) return false;
            string text = value.ToLowerInvariant();
            return text.Contains("server=") || text.Contains("data source=") || text.Contains("host=")
                || text.Contains("user id=") || text.Contains("uid=") || text.Contains("integrated security=");
        }

        private static Dictionary<string, JToken> NormalizeValues(TransactionMetadata metadata, JObject input)
        {
            var result = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in input.Properties())
            {
                var attr = metadata.Attributes.FirstOrDefault(a => string.Equals(a.Name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (attr == null) throw new RecordOperationException("TransactionAttributeNotFound", "The requested attribute is not part of the Transaction root structure.", "Use the attribute names returned by the Transaction metadata.", property.Name);
                result[attr.Name] = NormalizeToken(property.Value, attr);
            }
            return result;
        }

        private static JToken NormalizeToken(JToken token, AttributeMetadata attr)
        {
            if (token == null || token.Type == JTokenType.Null) return JValue.CreateNull();
            object value;
            try
            {
                string text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
                string type = (attr.Type ?? string.Empty).ToUpperInvariant();
                if (type.Contains("NUMERIC") || type.Contains("PACKED") || type.Contains("ZONED") || type.Contains("DECIMAL")) value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
                else if (type.Contains("INT")) value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                else if (type.Contains("DATE") && !type.Contains("DATETIME")) value = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).Date;
                else if (type.Contains("DATETIME")) value = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                else if (type.Contains("BOOLEAN") || type == "BIT") value = ParseBoolean(text);
                else if (type.Contains("GUID")) value = Guid.Parse(text);
                else value = token.Type == JTokenType.String ? (object)text : token.ToObject<object>();
                if (value is string s && attr.Length > 0 && s.Length > attr.Length) throw new FormatException("value exceeds the GeneXus attribute length");
                return ToJsonToken(value);
            }
            catch (Exception ex) { throw new RecordOperationException("InvalidTransactionValue", "A record value does not match the SDK type or length of the attribute.", "Correct the value using the Transaction metadata.", attr.Name, ex); }
        }

        private static string AddParameter(DbCommand command, string prefix, string name, AttributeMetadata attr, JToken token)
        {
            string parameterName = prefix + name;
            var parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.Value = ToDbValue(token, attr);
            command.Parameters.Add(parameter);
            return parameterName;
        }

        private static object ToDbValue(JToken token, AttributeMetadata attr)
        {
            if (token == null || token.Type == JTokenType.Null) return DBNull.Value;
            string type = (attr.Type ?? string.Empty).ToUpperInvariant();
            if (type.Contains("NUMERIC") || type.Contains("PACKED") || type.Contains("ZONED") || type.Contains("DECIMAL")) return token.Value<decimal>();
            if (type.Contains("INT")) return token.Value<long>();
            if (type.Contains("DATE") && !type.Contains("DATETIME")) return token.Value<DateTime>().Date;
            if (type.Contains("DATETIME")) return token.Value<DateTime>();
            if (type.Contains("BOOLEAN") || type == "BIT") return token.Value<bool>();
            if (type.Contains("GUID")) return token.Value<Guid>();
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToObject<object>();
        }

        private static string ComputeVersionToken(TransactionMetadata metadata, Dictionary<string, JToken> filters, IEnumerable<JObject> rows)
        {
            var payload = new JObject
            {
                ["transaction"] = metadata.Name,
                ["table"] = metadata.Table,
                ["attributes"] = new JArray(metadata.Attributes.Select(a => a.Name + ":" + a.Type + ":" + a.Length + ":" + a.Decimals)),
                ["where"] = ToObject(filters),
                ["rows"] = new JArray((rows ?? Enumerable.Empty<JObject>()).Select(CloneRow).OrderBy(Canonical, StringComparer.Ordinal))
            };
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(payload)));
                return "trn-v1:" + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static JObject ToObject(Dictionary<string, JToken> values)
        {
            var result = new JObject();
            if (values == null) return result;
            foreach (var pair in values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) result[pair.Key] = pair.Value?.DeepClone() ?? JValue.CreateNull();
            return result;
        }

        internal static string QuoteIdentifier(string name, string family)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Identifier is required.", nameof(name));
            var parts = name.Split('.');
            string quote = family == "sqlserver" ? "[" : family == "mysql" ? "`" : "\"";
            string close = family == "sqlserver" ? "]" : quote;
            return string.Join(".", parts.Select(part => quote + part.Trim().Trim('[', ']', '`', '"') + close));
        }

        internal static bool IsWriteAllowed(bool dryRun, string expectedVersion)
            => dryRun || !string.IsNullOrWhiteSpace(expectedVersion);

        private static string ParameterPrefix(string family) => family == "oracle" ? ":" : "@";
        private static int ClampLimit(int limit) => limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);
        private static int ReadTimeout(JObject args) => Math.Max(1, Math.Min(args["timeoutSeconds"]?.Value<int?>() ?? DefaultCommandTimeoutSeconds, 60));

        private static List<AttributeMetadata> ResolveFields(TransactionMetadata metadata, JArray requested)
        {
            if (requested == null || requested.Count == 0) return metadata.Attributes;
            var result = new List<AttributeMetadata>();
            foreach (var token in requested)
            {
                string name = token?.ToString();
                var attr = metadata.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                if (attr == null) throw new RecordOperationException("TransactionAttributeNotFound", "A requested field is not part of the Transaction root structure.", "Use the field names returned by the Transaction metadata.", name);
                if (!result.Any(a => string.Equals(a.Name, attr.Name, StringComparison.OrdinalIgnoreCase))) result.Add(attr);
            }
            // Keep identity visible even when the caller asks for a projection. It
            // makes the returned version token and keys actionable for a later write.
            foreach (var key in metadata.Keys)
                if (!result.Any(a => string.Equals(a.Name, key.Name, StringComparison.OrdinalIgnoreCase))) result.Add(key);
            return result;
        }

        private static JObject ReadObject(JObject args, params string[] names)
        {
            foreach (string name in names)
                if (args[name] is JObject obj) return obj;
            return null;
        }

        private static string FirstText(JObject args, params string[] names)
        {
            foreach (string name in names) if (!string.IsNullOrWhiteSpace(args[name]?.ToString())) return args[name].ToString();
            return null;
        }

        private static string FirstDynamicProperty(dynamic target, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    object value = target.Properties.GetPropertyValue(name);
                    if (value != null && !string.IsNullOrWhiteSpace(value.ToString())) return value.ToString();
                }
                catch { }
            }
            return null;
        }

        private static string FirstConnectionString(dynamic target, params string[] names)
        {
            foreach (string name in names)
            {
                string value = FirstDynamicProperty(target, name);
                if (LooksLikeConnectionString(value)) return value;
            }
            return null;
        }

        private static string FirstDynamicString(dynamic target, params string[] paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    object current = target;
                    foreach (string segment in path.Split('.')) current = current?.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance)?.GetValue(current, null);
                    if (current != null && !string.IsNullOrWhiteSpace(current.ToString())) return current.ToString();
                }
                catch { }
            }
            return null;
        }

        private static bool ParseYesNo(string value)
            => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) || value == "1";

        private static bool ParseBoolean(string value)
        {
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "t", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "f", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)) return false;
            throw new FormatException("invalid boolean");
        }

        private static JToken ToJsonToken(object value)
        {
            if (value == null || value == DBNull.Value) return JValue.CreateNull();
            if (value is byte[] bytes) return Convert.ToBase64String(bytes);
            if (value is Guid guid) return guid.ToString("D");
            if (value is DateTime dateTime) return dateTime.ToString("o", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset dateTimeOffset) return dateTimeOffset.ToString("o", CultureInfo.InvariantCulture);
            return JToken.FromObject(value);
        }

        private static bool ValueEquals(JToken left, JToken right)
        {
            if (left == null || left.Type == JTokenType.Null) return right == null || right.Type == JTokenType.Null;
            if (right == null || right.Type == JTokenType.Null) return false;
            if (left.Type == JTokenType.String || right.Type == JTokenType.String) return string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
            return JToken.DeepEquals(left, right);
        }

        private static string Canonical(JToken token)
        {
            if (token == null) return "null";
            if (token is JObject obj) return "{" + string.Join(",", obj.Properties().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => JsonConvert.ToString(p.Name) + ":" + Canonical(p.Value))) + "}";
            if (token is JArray array) return "[" + string.Join(",", array.Select(Canonical)) + "]";
            return token.ToString(Formatting.None);
        }

        private static JObject CloneRow(JObject row) => row == null ? null : (JObject)row.DeepClone();

        private static string TryString(Func<object> getter) { try { return getter()?.ToString(); } catch { return null; } }
        private static bool TryBool(Func<object> getter) { try { return Convert.ToBoolean(getter(), CultureInfo.InvariantCulture); } catch { return false; } }
        private static int TryInt(Func<object> getter) { try { return Convert.ToInt32(getter(), CultureInfo.InvariantCulture); } catch { return 0; } }

        private static string Error(string code, string message, string hint, string target = null, JObject extra = null)
            => McpResponse.Err(code, message, hint, target: target, errorExtra: extra);

        internal sealed class RecordOperationException : Exception
        {
            public string Code { get; }
            public string Hint { get; }
            public JObject Extra { get; }
            public RecordOperationException(string code, string message, string hint, JObject extra = null, Exception inner = null) : base(message, inner) { Code = code; Hint = hint; Extra = extra; }
            public RecordOperationException(string code, string message, string hint, string target, Exception inner = null)
                : this(code, message, hint, new JObject { ["target"] = target }, inner) { }
        }

        private sealed class TransactionMetadata
        {
            public string Name;
            public string Table;
            public List<AttributeMetadata> Attributes = new List<AttributeMetadata>();
            public List<AttributeMetadata> Keys = new List<AttributeMetadata>();
        }

        private sealed class AttributeMetadata
        {
            public string Name;
            public string Type;
            public int Length;
            public int Decimals;
            public bool IsKey;
        }

        private sealed class DatabaseMetadata
        {
            public string Name;
            public string Family;
            public string Schema;
            public DbProviderFactory Factory;
            public string ConnectionString;
            public string QualifiedTable;
            public List<AttributeMetadata> Attributes;
            public List<AttributeMetadata> Keys;
            public Dictionary<string, AttributeMetadata> AttributeMap;

            public string Table { get; set; }

            public void Bind(TransactionMetadata metadata)
            {
                Attributes = metadata.Attributes;
                Keys = metadata.Keys;
                AttributeMap = Attributes.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
                string table = string.IsNullOrWhiteSpace(Schema) ? metadata.Table : Schema + "." + metadata.Table;
                QualifiedTable = QuoteIdentifier(table, Family);
                Table = metadata.Table;
            }
        }
    }
}
