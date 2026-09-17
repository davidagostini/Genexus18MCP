using System;
using System.Reflection;
using System.Threading;
using SdkServices = Artech.Architecture.Common.Services.Services;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Generalization of <see cref="SdkServiceResolver"/> for SDK service interfaces that
    /// do NOT implement <c>IGxService</c> (e.g. <c>ISecurityScannerService</c>,
    /// <c>ISpecifierService</c>, <c>IStatisticsService</c>, <c>IModelInformationService</c>,
    /// <c>IDeploymentTargetService</c>, <c>ILibraryService</c>).
    ///
    /// Those interfaces fail the <c>where T : IGxService</c> constraint on the generic
    /// <c>Services.TryGetService&lt;T&gt;()</c>, and their concrete implementations are not
    /// public types (so the GamService "resolve concrete, cast to interface" trick can't be
    /// used). The one compile-legal path left is the non-generic
    /// <c>Services.TryGetService(Guid serviceId)</c> keyed by the interface's COM GUID —
    /// the standard GeneXus service-locator idiom <c>Services.GetService(typeof(IFoo).GUID)</c>.
    ///
    /// Resolution success is verified at runtime, not assumed: <see cref="TryResolve{T}"/>
    /// returns null (never throws) when the service is not registered under its interface
    /// GUID in this worker session, so callers degrade to a clean <c>*Unavailable</c> error
    /// instead of crashing the worker.
    /// </summary>
    public static class SdkServiceLocator
    {
        /// <summary>
        /// Resolve an SDK service by its interface GUID via the non-generic locator, with a
        /// bounded self-heal retry (mirrors <see cref="SdkServiceResolver"/> — a service can
        /// lag registration right after a worker respawn). Returns null when unavailable.
        /// </summary>
        /// <summary>
        /// Prefer constructing the concrete service impl directly (its public parameterless
        /// ctor) and casting to the interface — the idiom GamService uses — because several
        /// IDE-package services (ModelInformation, Specifier, Deployment, …) are never
        /// registered in the headless worker's service registry. Falls back to the
        /// interface-GUID registry lookup if construction fails. Returns null when neither works.
        /// </summary>
        public static T ConstructOrResolve<T>(Func<object> concreteFactory, int attempts = 5, int delayMs = 200) where T : class
        {
            try { if (concreteFactory() is T t && t != null) return t; } catch { /* concrete unavailable; try registry */ }
            return TryResolve<T>(attempts, delayMs);
        }

        public static T TryResolve<T>(int attempts = 5, int delayMs = 200) where T : class
        {
            return TryResolve(typeof(T).GUID, attempts, delayMs) as T;
        }

        public static object TryResolve(Guid id, int attempts = 5, int delayMs = 200)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    var s = SdkServices.TryGetService(id);
                    if (s != null) return s;
                }
                catch { /* not registered yet */ }
                try
                {
                    var s = SdkServices.GetService(id);
                    if (s != null) return s;
                }
                catch { /* forcing variant may throw */ }
                if (i < attempts - 1) Thread.Sleep(delayMs);
            }
            return null;
        }
    }
}
