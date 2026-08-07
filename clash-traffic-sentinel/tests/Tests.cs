using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ClashTrafficSentinel
{
    internal static class Tests
    {
        private static int failures;

        public static int Main(string[] args)
        {
            Run("YAML scalar parsing", TestYamlScalar);
            Run("Proxy chain filtering", TestProxyFiltering);
            Run("Mihomo JSON parsing", TestSnapshotParsing);
            Run("Traffic delta calculation", TestDeltaCalculation);
            Run("Local metadata sanitization", TestMetadataSanitization);
            Run("SQLite persistence and aggregation", TestDatabase);
            if (Array.Exists(args, delegate(string value) { return value == "--live"; }))
                Run("Live Clash named pipe", TestLivePipe);
            Console.WriteLine(failures == 0 ? "All tests passed." : failures + " test(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("PASS  " + name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAIL  " + name + " :: " + ex.Message);
            }
        }

        private static void TestYamlScalar()
        {
            Equal("alpha beta", NamedPipeMihomoClient.ParseYamlScalar(" 'alpha beta' "));
            Equal("it's-safe", NamedPipeMihomoClient.ParseYamlScalar("'it''s-safe'"));
            Equal("value", NamedPipeMihomoClient.ParseYamlScalar("value # comment"));
        }

        private static void TestProxyFiltering()
        {
            Assert(NamedPipeMihomoClient.IsProxyChain(ConnectionWithChains("Node A", "Auto")), "Proxy chain was rejected.");
            Assert(!NamedPipeMihomoClient.IsProxyChain(ConnectionWithChains("DIRECT")), "DIRECT chain was counted.");
            Assert(!NamedPipeMihomoClient.IsProxyChain(ConnectionWithChains("REJECT")), "REJECT chain was counted.");
        }

        private static Dictionary<string, object> ConnectionWithChains(params string[] chains)
        {
            object[] values = new object[chains.Length];
            for (int i = 0; i < chains.Length; i++) values[i] = chains[i];
            return new Dictionary<string, object> { { "chains", values } };
        }

        private static void TestSnapshotParsing()
        {
            string json = "{\"uploadTotal\":30,\"downloadTotal\":70,\"connections\":[" +
                "{\"id\":\"abc\",\"upload\":12,\"download\":34,\"chains\":[\"Node\",\"Auto\"]," +
                "\"metadata\":{\"host\":\"Example.COM.\",\"destinationIP\":\"1.2.3.4\"," +
                "\"process\":\"Browser.exe\",\"processPath\":\"C:\\\\Apps\\\\Browser.exe\"}}]}";
            MihomoSnapshot snapshot = new NamedPipeMihomoClient().ParseSnapshot(json);
            Equal(1, snapshot.Connections.Count);
            Equal("example.com", snapshot.Connections[0].Domain);
            Equal("Browser.exe", snapshot.Connections[0].AppName);
            Assert(snapshot.Connections[0].IsProxy, "Expected proxy connection.");
        }

        private static void TestDeltaCalculation()
        {
            DateTime now = new DateTime(2026, 8, 7, 10, 0, 0);
            Dictionary<string, ConnectionSample> previous = new Dictionary<string, ConnectionSample>
            {
                { "same", Sample("same", 100, 200, true) },
                { "direct", Sample("direct", 10, 10, false) }
            };
            MihomoSnapshot current = new MihomoSnapshot();
            current.Connections.Add(Sample("same", 140, 260, true));
            current.Connections.Add(Sample("new", 20, 30, true));
            current.Connections.Add(Sample("direct", 100, 100, false));
            List<TrafficDelta> deltas = TrafficCalculator.Calculate(previous, current, true, now);
            Equal(2, deltas.Count);
            Equal(40L, deltas[0].Upload);
            Equal(60L, deltas[0].Download);
            Equal(50L, deltas[1].Total);
            Equal(0, TrafficCalculator.Calculate(previous, current, false, now).Count - 1);
        }

        private static ConnectionSample Sample(string id, long upload, long download, bool proxy)
        {
            return new ConnectionSample
            {
                Id = id,
                AppName = "test.exe",
                AppPath = @"C:\\Apps\\test.exe",
                Domain = "example.com",
                Upload = upload,
                Download = download,
                IsProxy = proxy
            };
        }

        private static void TestMetadataSanitization()
        {
            Equal("odd app.exe", TrafficIdentity.NormalizeAppName("odd\rapp.exe", null));
            string longDomain = new string('a', 600) + ".example";
            Assert(TrafficIdentity.NormalizeDomain(longDomain, null).Length == 512, "Domain length was not limited.");
        }

        private static void TestDatabase()
        {
            string path = Path.Combine(Path.GetTempPath(), "clash-sentinel-test-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (TrafficDatabase database = new TrafficDatabase(path))
                {
                    DateTime now = new DateTime(2026, 8, 7, 10, 15, 0);
                    database.RecordBatch(new List<TrafficDelta>
                    {
                        new TrafficDelta { LocalTime=now,AppKey="app'one",AppName="App ' One",AppPath=@"C:\\App.exe",Domain="a.example",Upload=100,Download=200 },
                        new TrafficDelta { LocalTime=now,AppKey="app'one",AppName="App ' One",AppPath=@"C:\\App.exe",Domain="b.example",Upload=50,Download=650 }
                    });
                    TrafficSummary apps = database.GetSummary(now.Date, now.Date.AddDays(1), "app", null);
                    Equal(1, apps.Rows.Count);
                    Equal(1000L, apps.Total);
                    TrafficSummary domains = database.GetSummary(now.Date, now.Date.AddDays(1), "domain", "app'one");
                    Equal(2, domains.Rows.Count);
                    Equal(1000L, domains.Total);
                    Equal(1000L, database.GetRecentAppBytes("app'one", now, 3));

                    MonitorSettings settings = new MonitorSettings { BurstMegabytes = 350, DailyGigabytes = 2.5, AppCooldownMinutes = 45 };
                    database.SaveSettings(settings);
                    MonitorSettings loaded = database.LoadSettings();
                    Equal(350, loaded.BurstMegabytes);
                    Equal(2.5, loaded.DailyGigabytes);
                    Equal(45, loaded.AppCooldownMinutes);
                }
            }
            finally
            {
                DeleteTestFile(path);
                DeleteTestFile(path + "-wal");
                DeleteTestFile(path + "-shm");
            }
        }

        private static void TestLivePipe()
        {
            MihomoSnapshot snapshot = new NamedPipeMihomoClient().FetchAsync().GetAwaiter().GetResult();
            Assert(snapshot.Connections.Count >= 0, "Invalid connection count.");
            Console.WriteLine("      Active connection records: " + snapshot.Connections.Count);
        }

        private static void DeleteTestFile(string path)
        {
            try
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("clash-sentinel-test-", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Equal(object expected, object actual)
        {
            if (!object.Equals(expected, actual))
                throw new InvalidOperationException("Expected " + expected + ", got " + actual + ".");
        }
    }
}
