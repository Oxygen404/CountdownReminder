using System;

namespace ClaudeConsole
{
    internal static class Tests
    {
        private static int failures;

        public static int Main(string[] args)
        {
            TestLegacyPayload();
            TestLimitsPayload();
            TestClamping();

            if (Array.Exists(args, delegate(string value) { return value == "--live"; }))
            {
                TestLiveFetch();
            }

            Console.WriteLine(failures == 0 ? "PASS" : "FAIL: " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void TestLegacyPayload()
        {
            const string json = "{\"five_hour\":{\"utilization\":64,\"resets_at\":\"2026-08-03T14:10:00+08:00\"},\"seven_day\":{\"utilization\":19,\"resets_at\":\"2026-08-05T18:00:00+08:00\"}}";
            UsageSnapshot value = new UsageService().ParseUsage(json, DateTimeOffset.Now);
            AssertEqual("legacy weekly remaining", 81, value.Weekly.RemainingPercent);
            AssertEqual("legacy session remaining", 36, value.Session.RemainingPercent);
        }

        private static void TestLimitsPayload()
        {
            const string json = "{\"five_hour\":null,\"seven_day\":null,\"limits\":[{\"kind\":\"session\",\"percent\":22,\"resets_at\":\"2026-08-03T14:10:00Z\"},{\"kind\":\"weekly_all\",\"percent\":41,\"resets_at\":\"2026-08-05T18:00:00Z\"}]}";
            UsageSnapshot value = new UsageService().ParseUsage(json, DateTimeOffset.Now);
            AssertEqual("limits weekly remaining", 59, value.Weekly.RemainingPercent);
            AssertEqual("limits session remaining", 78, value.Session.RemainingPercent);
        }

        private static void TestClamping()
        {
            QuotaInfo high = new QuotaInfo(140, DateTimeOffset.Now);
            QuotaInfo low = new QuotaInfo(-7, DateTimeOffset.Now);
            AssertEqual("clamp high", 0, high.RemainingPercent);
            AssertEqual("clamp low", 100, low.RemainingPercent);
        }

        private static void TestLiveFetch()
        {
            try
            {
                UsageSnapshot value = new UsageService().FetchAsync().GetAwaiter().GetResult();
                bool valid = value.Weekly.RemainingPercent >= 0 && value.Weekly.RemainingPercent <= 100
                    && value.Session.RemainingPercent >= 0 && value.Session.RemainingPercent <= 100;
                if (!valid)
                {
                    failures++;
                    Console.WriteLine("live fetch: invalid percentages");
                }
                else
                {
                    Console.WriteLine("live fetch: weekly remaining {0}%, session remaining {1}%",
                        value.Weekly.RemainingPercent, value.Session.RemainingPercent);
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("live fetch: " + ex.Message);
            }
        }

        private static void AssertEqual(string name, int expected, int actual)
        {
            if (expected == actual) return;
            failures++;
            Console.WriteLine("{0}: expected {1}, actual {2}", name, expected, actual);
        }
    }
}
