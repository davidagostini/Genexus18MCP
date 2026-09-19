using System;
using System.Globalization;
using System.Reflection;

namespace GxMcp.Worker.Services
{
    // GeneXus exposes datastore identity through different members/property-bag
    // descriptors across SDK majors. Keep family detection in one place so
    // diagnostics and records_query cannot disagree about the active provider.
    internal static class DatabaseProviderResolver
    {
        private static readonly string[] ProviderProperties =
        {
            "ADONET_DRIVER", "ADO.NET_DRIVER", "ADO_NET_DRIVER", "ADONET_PROVIDER",
            "Provider", "AdoNetProvider", "Driver", "JDBC_DRIVER"
        };

        private static readonly string[] DbmsProperties =
        {
            "DBMS", "DBMS_TYPE", "DBMS_CODE", "DBMS_NAME",
            "DATABASE_DBMS", "DATABASE_MANAGEMENT_SYSTEM"
        };

        internal static string ResolveFamily(dynamic dataStore)
            => DetectFamily(GetProvider(dataStore), GetDbmsValue(dataStore));

        internal static string GetProvider(dynamic dataStore)
            => FirstText(dataStore, ProviderProperties);

        internal static int GetDbmsCode(dynamic dataStore)
            => TryInt(GetDbmsValue(dataStore));

        internal static object GetDbmsValue(dynamic dataStore)
        {
            if (dataStore == null) return null;

            object direct = ReadMember(dataStore, "Dbms");
            if (IsMeaningful(direct)) return direct;

            foreach (string name in DbmsProperties)
            {
                object value = ReadPropertyBagValue(dataStore, name);
                if (IsMeaningful(value)) return value;
            }

            return direct;
        }

        internal static string DetectFamily(string provider, object dbms)
        {
            string text = ((provider ?? string.Empty) + " " + (dbms?.ToString() ?? string.Empty)).Trim();
            if (ContainsAny(text, "npgsql", "postgresql", "postgres", "pgsql")) return "postgres";
            if (ContainsAny(text, "oracle")) return "oracle";
            if (ContainsAny(text, "sqlclient", "sql server", "sqlserver", "mssql")) return "sqlserver";
            if (ContainsAny(text, "mysql", "mariadb")) return "mysql";
            if (ContainsAny(text, "db2", "as400")) return "db2";
            if (ContainsAny(text, "informix")) return "informix";
            if (ContainsAny(text, "saphana", "sap hana", "hana")) return "saphana";

            switch (TryInt(dbms))
            {
                case 1: case 12: return "sqlserver";
                case 4: case 7: return "oracle";
                case 5: return "mysql";
                case 6: return "postgres";
                case 2: case 8: case 9: return "db2";
                case 3: return "informix";
                case 10: return "saphana";
                default: return "unknown";
            }
        }

        internal static string TryProperty(dynamic dataStore, string name)
        {
            object value = ReadPropertyBagValue(dataStore, name);
            if (value == null) value = ReadMember(dataStore, name);
            string text = value?.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string FirstText(dynamic dataStore, string[] names)
        {
            foreach (string name in names)
            {
                string value = TryProperty(dataStore, name);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        private static object ReadPropertyBagValue(dynamic dataStore, string name)
        {
            if (dataStore == null) return null;
            try
            {
                object bag = ReadMember(dataStore, "Properties");
                if (bag == null) return null;
                var method = bag.GetType().GetMethod("GetPropertyValue", BindingFlags.Public | BindingFlags.Instance);
                return method?.Invoke(bag, new object[] { name });
            }
            catch { return null; }
        }

        private static object ReadMember(dynamic target, string name)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target, null);
            }
            catch { return null; }
        }

        private static bool IsMeaningful(object value)
        {
            if (value == null) return false;
            string text = value.ToString();
            return !string.IsNullOrWhiteSpace(text) && !string.Equals(text, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static int TryInt(object value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch
            {
                return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                    ? result
                    : 0;
            }
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            foreach (string term in terms)
                if (value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
