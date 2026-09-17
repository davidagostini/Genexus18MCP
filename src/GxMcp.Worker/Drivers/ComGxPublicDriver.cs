using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Drivers
{
    public class ComGxPublicDriver : IDisposable
    {
        private static readonly string[] CandidateProgIds = new[]
        {
            "GXPublic.GXPublic",
            "GXPublic.Application",
            "GXPublic.GXPublic.5"
        };

        private readonly Func<string, Type> _progIdResolver;
        private readonly Func<Type, object> _comInstanceFactory;
        private object _comInstance;
        private string _resolvedProgId;
        private Type _resolvedType;
        private string _activeKbPath;
        private bool _isConnected;

        public static ComGxPublicDriver Instance { get; } = new ComGxPublicDriver();

        public ComGxPublicDriver(
            Func<string, Type> progIdResolver = null,
            Func<Type, object> comInstanceFactory = null)
        {
            _progIdResolver = progIdResolver ?? (progId => Type.GetTypeFromProgID(progId));
            _comInstanceFactory = comInstanceFactory ?? (type => Activator.CreateInstance(type));
            ResolveProgId();
        }

        public bool IsRegistered => _resolvedType != null;
        public string ResolvedProgId => _resolvedProgId;
        public bool IsConnected => _isConnected;
        public string ActiveKbPath => _activeKbPath;

        private void ResolveProgId()
        {
            foreach (var progId in CandidateProgIds)
            {
                try
                {
                    var type = _progIdResolver(progId);
                    if (type != null)
                    {
                        _resolvedType = type;
                        _resolvedProgId = progId;
                        return;
                    }
                }
                catch { }
            }
        }

        public bool OpenKB(string kbPath, out string errorJson)
        {
            errorJson = null;
            if (!IsRegistered)
            {
                errorJson = McpResponse.Err(
                    code: "GXMCP_GXPUBLIC_COM_NOT_REGISTERED",
                    message: "GXPublic COM component is not registered on this machine.",
                    hint: "To use GeneXus 8.0/9.0 legacy support, register GxPublic.dll using 'regsvr32 GxPublic.dll' from your GeneXus installation directory.",
                    target: kbPath);
                return false;
            }

            try
            {
                CloseKB();
                _comInstance = _comInstanceFactory(_resolvedType);

                // Invoke Open or Connect on COM automation server
                var openMethod = _comInstance.GetType().GetMethod("Open", new[] { typeof(string) })
                              ?? _comInstance.GetType().GetMethod("Connect", new[] { typeof(string) })
                              ?? _comInstance.GetType().GetMethod("OpenKB", new[] { typeof(string) });

                if (openMethod != null)
                {
                    openMethod.Invoke(_comInstance, new object[] { kbPath });
                }
                else
                {
                    // Late-bound IDispatch call
                    _comInstance.GetType().InvokeMember("Open",
                        BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                        null, _comInstance, new object[] { kbPath });
                }

                _activeKbPath = kbPath;
                _isConnected = true;
                return true;
            }
            catch (Exception ex)
            {
                errorJson = McpResponse.Err(
                    code: "GXMCP_GXPUBLIC_OPEN_FAILED",
                    message: "Failed to open Knowledge Base via GXPublic: " + (ex.InnerException?.Message ?? ex.Message),
                    hint: "Ensure the KB is closed in the GeneXus IDE as GXPublic requires exclusive access.",
                    target: kbPath);
                CloseKB();
                return false;
            }
        }

        public void CloseKB()
        {
            if (_comInstance != null)
            {
                try
                {
                    var closeMethod = _comInstance.GetType().GetMethod("Close", Type.EmptyTypes)
                                   ?? _comInstance.GetType().GetMethod("Disconnect", Type.EmptyTypes);
                    if (closeMethod != null)
                    {
                        closeMethod.Invoke(_comInstance, null);
                    }
                    else
                    {
                        _comInstance.GetType().InvokeMember("Close",
                            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                            null, _comInstance, null);
                    }
                }
                catch { }

                try
                {
                    if (Marshal.IsComObject(_comInstance))
                    {
                        Marshal.FinalReleaseComObject(_comInstance);
                    }
                    else if (_comInstance is IDisposable disp)
                    {
                        disp.Dispose();
                    }
                }
                catch { }

                _comInstance = null;
            }

            _isConnected = false;
            _activeKbPath = null;
        }

        public string ReadObjectPart(string objectName, string partName, out string error)
        {
            error = null;
            if (!_isConnected || _comInstance == null)
            {
                error = "KB is not connected under GXPublic COM driver.";
                return null;
            }

            try
            {
                var method = _comInstance.GetType().GetMethod("GetObject", new[] { typeof(string), typeof(string) })
                          ?? _comInstance.GetType().GetMethod("GetObjectPart", new[] { typeof(string), typeof(string) });

                if (method != null)
                {
                    object result = method.Invoke(_comInstance, new object[] { objectName, partName });
                    return result?.ToString();
                }

                object lateResult = _comInstance.GetType().InvokeMember("GetObject",
                    BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                    null, _comInstance, new object[] { objectName, partName });
                return lateResult?.ToString();
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return null;
            }
        }

        public List<string> QueryObjects(string typeFilter, string nameFilter, out string error)
        {
            error = null;
            var list = new List<string>();

            if (!_isConnected || _comInstance == null)
            {
                error = "KB is not connected under GXPublic COM driver.";
                return list;
            }

            try
            {
                var method = _comInstance.GetType().GetMethod("GetObjectNames", Type.EmptyTypes)
                          ?? _comInstance.GetType().GetMethod("GetObjects", Type.EmptyTypes);

                if (method != null)
                {
                    var result = method.Invoke(_comInstance, null);
                    if (result is IEnumerable<string> enumStr)
                    {
                        list.AddRange(enumStr);
                    }
                    else if (result is System.Collections.IEnumerable rawEnum)
                    {
                        foreach (var item in rawEnum)
                        {
                            if (item != null) list.Add(item.ToString());
                        }
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return list;
            }
        }

        public bool WriteObject(string objectName, string content, out string error)
        {
            error = null;
            if (!_isConnected || _comInstance == null)
            {
                error = "KB is not connected under GXPublic COM driver.";
                return false;
            }

            try
            {
                var method = _comInstance.GetType().GetMethod("SaveObject", new[] { typeof(string), typeof(string) })
                          ?? _comInstance.GetType().GetMethod("ImportObject", new[] { typeof(string), typeof(string) });

                if (method != null)
                {
                    method.Invoke(_comInstance, new object[] { objectName, content });
                    return true;
                }

                _comInstance.GetType().InvokeMember("SaveObject",
                    BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                    null, _comInstance, new object[] { objectName, content });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public bool ExportXPZ(string xpzPath, IEnumerable<string> objectNames, out string error)
        {
            error = null;
            if (!_isConnected || _comInstance == null)
            {
                error = "KB is not connected under GXPublic COM driver.";
                return false;
            }

            try
            {
                var method = _comInstance.GetType().GetMethod("Export", new[] { typeof(string), typeof(string[]) })
                          ?? _comInstance.GetType().GetMethod("ExportXPZ", new[] { typeof(string), typeof(string[]) })
                          ?? _comInstance.GetType().GetMethod("Export", new[] { typeof(string) });

                var objArray = objectNames != null ? new List<string>(objectNames).ToArray() : new string[0];

                if (method != null)
                {
                    if (method.GetParameters().Length == 2)
                        method.Invoke(_comInstance, new object[] { xpzPath, objArray });
                    else
                        method.Invoke(_comInstance, new object[] { xpzPath });
                    return true;
                }

                _comInstance.GetType().InvokeMember("Export",
                    BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                    null, _comInstance, new object[] { xpzPath, objArray });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public bool ImportXPZ(string xpzPath, out string error)
        {
            error = null;
            if (!_isConnected || _comInstance == null)
            {
                error = "KB is not connected under GXPublic COM driver.";
                return false;
            }

            try
            {
                var method = _comInstance.GetType().GetMethod("Import", new[] { typeof(string) })
                          ?? _comInstance.GetType().GetMethod("ImportXPZ", new[] { typeof(string) });

                if (method != null)
                {
                    method.Invoke(_comInstance, new object[] { xpzPath });
                    return true;
                }

                _comInstance.GetType().InvokeMember("Import",
                    BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                    null, _comInstance, new object[] { xpzPath });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public void Dispose()
        {
            CloseKB();
        }
    }
}
