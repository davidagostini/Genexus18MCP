using System.Threading;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class ObjectTextService
    {
        internal static string ValidateInMemory(JObject args, CancellationToken ct)
            => TextTreeFileService.ValidateInMemory(args, ct);

        internal static string ListInMemory(JObject args, CancellationToken ct)
            => TextTreeFileService.ListInMemory(args, ct);
    }
}
