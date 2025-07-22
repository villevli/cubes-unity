using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Cubes
{
    public struct TimerResult
    {
        public string Name;
        public TimeSpan Elapsed;
    }

    public class TimerResults
    {
        public Dictionary<string, TimerResult> dict = new();

        public void Add(in TimerResult result)
        {
            if (!dict.TryGetValue(result.Name, out var eResult))
                eResult = result;
            else
                eResult.Elapsed += result.Elapsed;
            dict[eResult.Name] = eResult;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var r in dict)
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(r.Key).Append(' ');
                sb.Append(r.Value.Elapsed.TotalMilliseconds.ToString("F4", NumberFormatInfo.InvariantInfo)).Append("ms");
            }
            return sb.ToString();
        }
    }

    public readonly struct TimerScope : IDisposable
    {
        readonly string name;
        readonly long timestamp;
        readonly TimerResults results;

        public TimerScope(string name, TimerResults results)
        {
            this.name = name;
            this.timestamp = Stopwatch.GetTimestamp();
            this.results = results;
        }

        public string ResultToString()
        {
            return TimeSpan.FromTicks(Stopwatch.GetTimestamp() - timestamp).TotalMilliseconds.ToString("F4", NumberFormatInfo.InvariantInfo) + "ms";
        }

        public void Dispose()
        {
            results.Add(new()
            {
                Name = name,
                Elapsed = TimeSpan.FromTicks(Stopwatch.GetTimestamp() - timestamp)
            });
        }
    }
}
