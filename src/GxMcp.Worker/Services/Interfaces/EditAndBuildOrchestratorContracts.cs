using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public interface IWriteServiceFacade
    {
        string WriteObject(string target, JObject args);
    }

    public interface IAnalyzeServiceFacade
    {
        string ImpactAnalysis(string target, bool waitForIndex, int waitTimeoutMs);
    }

    public interface IBuildServiceFacade
    {
        string Build(string action, string target, string includeCallees, int buildPlanCap);
        string Specify(string target);
    }
}
