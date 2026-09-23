using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class JsonIngressContractTests
    {
        [Fact]
        public void ProtocolIngressFiles_UseJsonIngressInsteadOfDefaultNewtonsoftDateParsing()
        {
            var root = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (root != null && (!Directory.Exists(Path.Combine(root.FullName, "src", "GxMcp.Gateway"))
                || !Directory.Exists(Path.Combine(root.FullName, "src", "GxMcp.Worker"))))
            {
                root = root.Parent;
            }
            Assert.NotNull(root);

            string[] files =
            {
                "src/GxMcp.Gateway/Program.cs",
                "src/GxMcp.Gateway/Program.Http.cs",
                "src/GxMcp.Gateway/SharedWorkerConnection.cs",
                "src/GxMcp.Worker/Program.cs",
                "src/GxMcp.Worker/SharedWorkerHost.cs",
                "src/GxMcp.Worker/SharedWorkerHostProtocol.cs"
            };
            foreach (string relativePath in files)
            {
                string path = Path.Combine(root!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                string source = File.ReadAllText(path);
                Assert.DoesNotMatch(new Regex(@"\b(?:JObject|JToken)\.Parse\s*\(\s*(?:line|capturedLine|replayLine|body|initializeLine)\b"), source);
                Assert.DoesNotMatch(new Regex(@"JsonConvert\.DeserializeObject\s*<\s*JObject\s*>"), source);
            }
        }
    }
}
