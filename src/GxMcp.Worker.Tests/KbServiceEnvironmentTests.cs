using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class KbServiceEnvironmentTests : IDisposable
    {
        private readonly string _tempDir;

        public KbServiceEnvironmentTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp_env_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch { }
        }

        public class FakeModel
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string TargetPath { get; set; }
            public string Type { get; set; } = "Prototype";
            public object GetDesignModel() => null;
        }

        public class FakeEnvironment
        {
            public string Name { get; set; }
            public object TargetModel { get; set; }
            public List<object> Models { get; set; } = new List<object>();
        }

        public class FakeUser
        {
            public object ActiveTargetModel { get; set; }
            public void SetTargetModel(object design, object target) { ActiveTargetModel = target; }
            public void Save() { }
        }

        public class FakeKb
        {
            public string Location { get; set; }
            public FakeEnvironment Environment { get; set; }
            public FakeModel DesignModel { get; set; }
            public FakeUser User { get; set; } = new FakeUser();
        }

        private static void SetKb(KbService svc, object kb)
        {
            var fld = typeof(KbService).GetField("_kb", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(fld);
            fld.SetValue(svc, kb);
        }

        [Fact]
        public void ListEnvironments_NullKb_ThrowsInvalidOperationException()
        {
            var svc = new KbService(new IndexCacheService());
            var ex = Assert.Throws<InvalidOperationException>(() => svc.ListEnvironments());
            Assert.Contains("not open", ex.Message);
        }

        [Fact]
        public void SetActiveEnvironment_EmptyOrNull_ThrowsArgumentException()
        {
            var svc = new KbService(new IndexCacheService());
            Assert.Throws<ArgumentException>(() => svc.SetActiveEnvironment(""));
            Assert.Throws<ArgumentException>(() => svc.SetActiveEnvironment("   "));
            Assert.Throws<ArgumentException>(() => svc.SetActiveEnvironment(null));
        }

        [Fact]
        public void SetActiveEnvironment_NullKb_ThrowsInvalidOperationException()
        {
            var svc = new KbService(new IndexCacheService());
            var ex = Assert.Throws<InvalidOperationException>(() => svc.SetActiveEnvironment("NetCore"));
            Assert.Contains("not open", ex.Message);
        }

        [Fact]
        public void ListEnvironments_WithConfiguredEnvironments_ReturnsActiveAndList()
        {
            var svc = new KbService(new IndexCacheService());

            var netModelDir = Path.Combine(_tempDir, "NetModel", "web");
            Directory.CreateDirectory(netModelDir);
            var javaModelDir = Path.Combine(_tempDir, "JavaModel", "web");
            Directory.CreateDirectory(javaModelDir);

            var model1 = new FakeModel
            {
                Name = ".NET",
                Description = ".NET Environment",
                TargetPath = Path.Combine(_tempDir, "NetModel")
            };
            var model2 = new FakeModel
            {
                Name = "Java",
                Description = "Java Environment",
                TargetPath = Path.Combine(_tempDir, "JavaModel")
            };

            var fakeEnv = new FakeEnvironment
            {
                Name = ".NET",
                TargetModel = model1,
                Models = new List<object> { model1, model2 }
            };

            var fakeKb = new FakeKb
            {
                Location = _tempDir,
                Environment = fakeEnv,
                DesignModel = new FakeModel
                {
                    Name = "Design",
                    Description = "Design Model",
                    Type = "Design"
                }
            };

            SetKb(svc, fakeKb);

            string raw = svc.ListEnvironments();
            Assert.NotNull(raw);

            var json = JObject.Parse(raw);
            Assert.Equal(".NET", (string)json["activeEnvironment"]);

            var envs = (JArray)json["environments"];
            Assert.NotNull(envs);
            Assert.Equal(2, envs.Count);

            var env1 = (JObject)envs[0];
            Assert.Equal(".NET", (string)env1["name"]);
            Assert.Equal(".NET Environment", (string)env1["description"]);
            Assert.True((bool)env1["isActive"]);

            var env2 = (JObject)envs[1];
            Assert.Equal("Java", (string)env2["name"]);
            Assert.Equal("Java Environment", (string)env2["description"]);
            Assert.False((bool)env2["isActive"]);
        }

        [Fact]
        public void SetActiveEnvironment_DirectSdk_SwitchesTargetModelAndReturnsChangedEnvelope()
        {
            var svc = new KbService(new IndexCacheService());

            var model1 = new FakeModel
            {
                Name = ".NET",
                Description = ".NET Environment",
                TargetPath = Path.Combine(_tempDir, "NetModel")
            };
            var model2 = new FakeModel
            {
                Name = "Java",
                Description = "Java Environment",
                TargetPath = Path.Combine(_tempDir, "JavaModel")
            };

            var fakeEnv = new FakeEnvironment
            {
                Name = ".NET",
                TargetModel = model1,
                Models = new List<object> { model1, model2 }
            };

            var fakeKb = new FakeKb
            {
                Location = _tempDir,
                Environment = fakeEnv,
                DesignModel = new FakeModel { Name = "Design", Type = "Design" }
            };

            SetKb(svc, fakeKb);

            string result = svc.SetActiveEnvironment("Java");
            var json = JObject.Parse(result);

            Assert.Equal(".NET", (string)json["previous"]);
            Assert.Equal("Java", (string)json["requested"]);
            Assert.Equal("Java", (string)json["active"]);
            Assert.True((bool)json["changed"]);

            Assert.Same(model2, fakeEnv.TargetModel);
        }

        [Fact]
        public void SetActiveEnvironment_NonExistentEnvironment_ThrowsDescriptiveException()
        {
            var svc = new KbService(new IndexCacheService());

            var model1 = new FakeModel
            {
                Name = ".NET",
                Description = ".NET Environment",
                TargetPath = Path.Combine(_tempDir, "NetModel")
            };

            var fakeEnv = new FakeEnvironment
            {
                Name = ".NET",
                TargetModel = model1,
                Models = new List<object> { model1 }
            };

            var fakeKb = new FakeKb
            {
                Location = _tempDir,
                Environment = fakeEnv,
                DesignModel = new FakeModel { Name = "Design", Type = "Design" }
            };

            SetKb(svc, fakeKb);

            var ex = Assert.Throws<InvalidOperationException>(() => svc.SetActiveEnvironment("NonExistentEnv"));
            Assert.Contains("NonExistentEnv", ex.Message);
            Assert.Contains("could not activate", ex.Message);
        }
    }
}
