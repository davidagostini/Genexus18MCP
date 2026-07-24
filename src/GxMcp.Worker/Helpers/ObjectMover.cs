using System;
using System.Reflection;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Persists an object's folder/module placement (its KB Explorer parent).
    ///
    /// History: issues #30/#50 concluded folder placement was unsupported because
    /// decompiling <c>Artech.Architecture.Common.dll</c> showed <c>KBObject.set_Parent</c>
    /// as an empty body. That DLL is a FACADE/reference assembly — every member is a
    /// stub (getters return null, setters empty) and the real implementation binds at
    /// runtime from elsewhere. The empty setter proves nothing about runtime behaviour.
    ///
    /// The IDE moves an object by setting its parent and persisting via the low-level
    /// Udm EntityManager. This helper mirrors that: set <c>Parent</c>, then call
    /// <c>EntityManager.SaveWithParent(entity, parentEntity, prefs)</c> (which passes the
    /// parent explicitly, so it does not depend on the property setter), falling back to
    /// <c>UpdateParent(entity, prefs)</c> and finally <c>obj.Save()</c>. The caller must
    /// re-read the parent afterwards to confirm the move actually persisted.
    /// </summary>
    internal static class ObjectMover
    {
        public struct MoveResult
        {
            public bool Ok;
            public string Strategy;   // which persist path was taken
            public string Error;      // populated when Ok == false
        }

        /// <summary>
        /// Set <paramref name="obj"/>'s parent to <paramref name="container"/> and persist.
        /// Both are KBObject instances (folder/module are KBObjects that derive from the
        /// Udm Entity type). Does NOT verify persistence — the caller re-reads the parent.
        /// </summary>
        public static MoveResult SetParentAndSave(object obj, object container)
        {
            if (obj == null) return new MoveResult { Ok = false, Error = "object is null" };
            if (container == null) return new MoveResult { Ok = false, Error = "destination container is null" };

            // 1. Set the in-memory parent (works at runtime; the facade-DLL no-op is a
            //    decompilation artefact). Best-effort — SaveWithParent also passes the
            //    parent explicitly, so this is belt-and-suspenders.
            TrySetParentProperty(obj, container);

            // 2. Resolve the Udm EntityManager (same lookup WebFormSaveDiagnostics uses).
            Type emType = FindType("Artech.Layers.BL.EntityManager")
                        ?? FindType("Artech.Udm.Framework.EntityManager")
                        ?? FindFirstType("EntityManager");
            if (emType == null)
                return new MoveResult { Ok = false, Error = "EntityManager type not found in loaded assemblies" };

            object prefs = BuildSavePreferences();

            // 3a. SaveWithParent(entity, parentEntity[, prefs]) — preferred: parent explicit.
            var swp = FindMethod(emType, "SaveWithParent", obj, container);
            if (swp != null)
            {
                var inv = InvokeSave(emType, swp, BuildArgs(swp, obj, container, prefs));
                if (inv == null) return new MoveResult { Ok = true, Strategy = "EntityManager.SaveWithParent" };
                // fall through to try UpdateParent if SaveWithParent threw
            }

            // 3b. UpdateParent(entity[, prefs]) — relies on the parent set in step 1.
            var up = FindMethod(emType, "UpdateParent", obj);
            if (up != null)
            {
                var inv = InvokeSave(emType, up, BuildArgs(up, obj, null, prefs));
                if (inv == null) return new MoveResult { Ok = true, Strategy = "EntityManager.UpdateParent" };
            }

            // 3c. Last resort: plain Save on the object (some SDK builds persist the
            //     parent as part of a normal header save once Parent is set).
            try
            {
                var save = obj.GetType().GetMethod("Save", Type.EmptyTypes);
                if (save != null)
                {
                    save.Invoke(obj, null);
                    return new MoveResult { Ok = true, Strategy = "KBObject.Save" };
                }
            }
            catch (Exception ex)
            {
                return new MoveResult { Ok = false, Error = "obj.Save() threw: " + Unwrap(ex).Message };
            }

            return new MoveResult { Ok = false, Error = "no persist path (SaveWithParent/UpdateParent/Save) resolved on " + emType.FullName };
        }

        // ---- internals -------------------------------------------------------

        private static void TrySetParentProperty(object obj, object container)
        {
            try
            {
                var p = obj.GetType().GetProperty("Parent",
                    BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite && p.PropertyType.IsInstanceOfType(container))
                    p.SetValue(obj, container, null);
            }
            catch (Exception ex) { Logger.Info("[Move] set_Parent threw: " + Unwrap(ex).Message); }
        }

        private static MethodInfo FindMethod(Type emType, string name, object arg0, object arg1 = null)
        {
            foreach (var mi in emType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (mi.Name != name) continue;
                var ps = mi.GetParameters();
                if (ps.Length < 1) continue;
                if (!ps[0].ParameterType.IsInstanceOfType(arg0)) continue;
                if (arg1 != null)
                {
                    if (ps.Length < 2 || !ps[1].ParameterType.IsInstanceOfType(arg1)) continue;
                }
                return mi;
            }
            return null;
        }

        // Returns null on success, or the thrown exception on failure.
        private static Exception InvokeSave(Type emType, MethodInfo mi, object[] args)
        {
            try
            {
                object instance = mi.IsStatic ? null : ResolveInstance(emType);
                if (!mi.IsStatic && instance == null)
                    return new InvalidOperationException("could not resolve EntityManager instance for " + mi.Name);
                mi.Invoke(instance, args);
                return null;
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                Logger.Info("[Move] " + mi.Name + " threw: " + inner.GetType().Name + ": " + inner.Message);
                return inner;
            }
        }

        private static object[] BuildArgs(MethodInfo mi, object entity, object parent, object prefs)
        {
            var ps = mi.GetParameters();
            var args = new object[ps.Length];
            args[0] = entity;
            for (int i = 1; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (parent != null && pt.IsInstanceOfType(parent) && args.Length > 1 && i == 1)
                    args[i] = parent;
                else if (prefs != null && pt.IsInstanceOfType(prefs))
                    args[i] = prefs;
                else if (pt.Name.IndexOf("Preferences", StringComparison.OrdinalIgnoreCase) >= 0)
                    args[i] = prefs; // may be null; SDK tolerates default prefs
                else
                    args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
            }
            return args;
        }

        private static object BuildSavePreferences()
        {
            try
            {
                var t = FindType("Artech.Architecture.Common.Objects.KBObjectSavePreferences");
                if (t == null) return null;
                var prefs = Activator.CreateInstance(t);
                TrySetBool(prefs, "SkipValidation", true);
                return prefs;
            }
            catch { return null; }
        }

        private static object ResolveInstance(Type emType)
        {
            var instProp = emType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (instProp != null)
            {
                var v = instProp.GetValue(null, null);
                if (v != null) return v;
            }
            var instField = emType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)
                          ?? emType.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                          ?? emType.GetField("m_Instance", BindingFlags.NonPublic | BindingFlags.Static);
            return instField?.GetValue(null);
        }

        private static void TrySetBool(object o, string propName, bool value)
        {
            try
            {
                var p = o.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) p.SetValue(o, value, null);
            }
            catch { }
        }

        private static Exception Unwrap(Exception ex) => ex?.InnerException ?? ex;

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullName, false); if (t != null) return t; } catch { }
            }
            return null;
        }

        private static Type FindFirstType(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                if (types == null) continue;
                foreach (var t in types)
                    if (t != null && t.Name == simpleName) return t;
            }
            return null;
        }
    }
}
