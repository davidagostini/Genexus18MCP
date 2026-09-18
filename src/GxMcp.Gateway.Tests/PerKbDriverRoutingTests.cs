using System.Collections.Generic;
using Newtonsoft.Json;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class PerKbDriverRoutingTests
    {
        [Fact]
        public void ConfigCatalogConverterReadsPerKbDriverObjectShape()
        {
            string json = "{\"Environment\":{\"KBs\":{\"sect80\":{\"Path\":\"D:\\\\GX80\\\\SECT\",\"Driver\":\"com-gxpublic\",\"InstallationPath\":\"C:\\\\gxw80\",\"Major\":\"8\"}}}}";
            var config = JsonConvert.DeserializeObject<Configuration>(json);
            var entry = Assert.Single(config!.Environment!.KBs);

            Assert.Equal("sect80", entry.Alias);
            Assert.Equal("com-gxpublic", entry.Driver);
            Assert.Equal("8", entry.Major);
            Assert.Equal(@"C:\gxw80", entry.InstallationPath);
        }

        [Fact]
        public void ResolverPreservesPerKbDriverAndInstallationPath()
        {
            var config = new Configuration
            {
                Environment = new EnvironmentConfig
                {
                    ResolutionPolicy = "strict",
                    KBs = new List<KbEntry>
                    {
                        new KbEntry
                        {
                            Alias = "sect80",
                            Path = @"D:\GX80\SECT",
                            Driver = "com-gxpublic",
                            InstallationPath = @"C:\Program Files (x86)\ARTech\GeneXus\gxw80",
                            Major = "8"
                        }
                    }
                }
            };

            var handle = new KbResolver(config).Resolve(
                "sect80",
                new List<KbHandle>(),
                new List<KbHandle>(),
                null,
                out _);

            Assert.Equal("sect80", handle.Alias);
            Assert.Equal(@"D:\GX80\SECT", handle.Path);
            Assert.Equal("com-gxpublic", handle.Driver);
            Assert.Equal("8", handle.Major);
            Assert.Equal(@"C:\Program Files (x86)\ARTech\GeneXus\gxw80", handle.InstallationPath);
        }
    }
}
