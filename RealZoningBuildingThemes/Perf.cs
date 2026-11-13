using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace RealZoningBuildingThemes
{
    /// <summary>
    /// TEMPORARY performance check (perf-check branch). Each of the mod's systems reports how long
    /// its update took on the main thread; every five minutes of real time the totals go to the log,
    /// per system and per frame. Entries named "probe: ..." are sampled timings of the spawner's
    /// background jobs (the probe waits for them, so those passes stall briefly); they are reported
    /// separately and left out of the main-thread total. Remove before release.
    /// </summary>
    internal static class Perf
    {
        private const double kReportSeconds = 300;

        private sealed class Stat
        {
            public int Calls;
            public double Total;
            public double Max;
        }

        private static readonly Dictionary<string, Stat> s_Stats = new Dictionary<string, Stat>();
        private static readonly Stopwatch s_Clock = Stopwatch.StartNew();
        private static double s_WindowStart = -1;
        private static int s_WindowStartFrame;

        public static long Start() => Stopwatch.GetTimestamp();

        public static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        public static void Stop(string name, long start) => Add(name, Elapsed(start));

        public static void Add(string name, double ms)
        {
            if (s_WindowStart < 0)
            {
                s_WindowStart = s_Clock.Elapsed.TotalSeconds;
                s_WindowStartFrame = UnityEngine.Time.frameCount;
            }

            if (!s_Stats.TryGetValue(name, out Stat stat))
            {
                s_Stats[name] = stat = new Stat();
            }

            stat.Calls++;
            stat.Total += ms;
            if (ms > stat.Max)
            {
                stat.Max = ms;
            }

            double now = s_Clock.Elapsed.TotalSeconds;
            if (now - s_WindowStart >= kReportSeconds)
            {
                Report(now);
            }
        }

        private static void Report(double now)
        {
            int frames = System.Math.Max(1, UnityEngine.Time.frameCount - s_WindowStartFrame);
            double seconds = now - s_WindowStart;
            var sb = new StringBuilder();
            sb.Append($"Perf: last {seconds / 60:0.#} min, {frames} frames ({frames / seconds:0.#} fps on average). Main-thread time by system:");
            double sum = 0;
            foreach (var kv in s_Stats.Where(k => !k.Key.StartsWith("probe")).OrderByDescending(k => k.Value.Total))
            {
                Stat s = kv.Value;
                sum += s.Total;
                sb.Append($"\n  {kv.Key}: {s.Calls} runs, {s.Total / s.Calls:0.###} ms avg, {s.Max:0.##} ms max, {s.Total / frames:0.####} ms per frame");
            }

            sb.Append($"\n  All mod systems: {sum / frames:0.####} ms per frame, {sum / (seconds * 1000) * 100:0.###}% of real time");
            foreach (var kv in s_Stats.Where(k => k.Key.StartsWith("probe")).OrderBy(k => k.Key))
            {
                Stat s = kv.Value;
                sb.Append($"\n  {kv.Key}: {s.Calls} samples, {s.Total / s.Calls:0.###} ms avg, {s.Max:0.##} ms max");
            }

            Mod.log.Info(sb.ToString());
            s_Stats.Clear();
            s_WindowStart = now;
            s_WindowStartFrame = UnityEngine.Time.frameCount;
        }
    }
}
