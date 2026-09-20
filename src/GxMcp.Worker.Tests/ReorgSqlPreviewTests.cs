using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ReorgSqlPreviewTests
    {
        [Fact]
        public void Parse_ClassifiesTablesIndexesAndDestructiveChanges()
        {
            JObject plan = ReorgSqlPreview.Parse(@"
CREATE TABLE [Customer] ([CustomerId] int NOT NULL);
ALTER TABLE [Customer] ADD [Name] varchar(80) NULL;
CREATE INDEX IX_Customer_Name ON [Customer] ([Name]);
ALTER TABLE [Customer] DROP COLUMN [Legacy];", true);

            Assert.True((bool)plan["ddlEffective"]);
            Assert.Equal(4, ((JArray)plan["ddl"]).Count);
            Assert.Contains("Customer", plan["affectedTables"].Values<string>());
            Assert.Single((JArray)plan["indexes"]);
            Assert.Single((JArray)plan["destructiveConversions"]);
        }
    }
}
