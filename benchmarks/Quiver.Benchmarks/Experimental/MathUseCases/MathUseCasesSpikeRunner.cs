using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Quiver.Benchmarks.Experimental.MathUseCases;

internal static class MathUseCasesSpikeRunner
{
    public static int Run()
    {
        Console.WriteLine("=== Mathematical use-case exploratory spikes ===");
        Console.WriteLine($"runtime={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os={RuntimeInformation.OSDescription}");
        Console.WriteLine($"arch={RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"processorCount={Environment.ProcessorCount}");

        try
        {
            DirectedHypergraphExperiments.Run();
            OptimizationAndProvenanceExperiments.Run();
            RankingExperiments.Run();
            TopologyAndConceptExperiments.Run();
            JoinAndFrontierExperiments.Run();
            Console.WriteLine("overall=PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"overall=FAIL type={ex.GetType().Name} message={ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}

internal readonly record struct TimedSample(
    double MedianMilliseconds,
    double IqrPercent,
    long AllocatedBytes,
    long Checksum);

internal readonly record struct AlternatingSamples(TimedSample First, TimedSample Second)
{
    public double SecondOverFirst => Second.MedianMilliseconds / First.MedianMilliseconds;
}

internal static class SpikeMeasurement
{
    public static long Repeat(Func<long> action, int count)
    {
        long checksum = 0;
        for (int i = 0; i < count; i++) checksum = action();
        return checksum;
    }

    public static AlternatingSamples Alternate(
        Func<long> first,
        Func<long> second,
        int warmup = 2,
        int samples = 9)
    {
        for (int i = 0; i < warmup; i++)
        {
            _ = first();
            _ = second();
        }

        var firstTimes = new double[samples];
        var secondTimes = new double[samples];
        long firstChecksum = 0;
        long secondChecksum = 0;

        for (int i = 0; i < samples; i++)
        {
            if ((i & 1) == 0)
            {
                firstChecksum = Measure(first, out firstTimes[i]);
                secondChecksum = Measure(second, out secondTimes[i]);
            }
            else
            {
                secondChecksum = Measure(second, out secondTimes[i]);
                firstChecksum = Measure(first, out firstTimes[i]);
            }
        }

        long firstAllocated = MeasureAllocated(first, out long firstAllocationChecksum);
        long secondAllocated = MeasureAllocated(second, out long secondAllocationChecksum);

        Array.Sort(firstTimes);
        Array.Sort(secondTimes);
        return new AlternatingSamples(
            new TimedSample(
                firstTimes[samples / 2],
                IqrPercent(firstTimes),
                firstAllocated,
                firstChecksum == firstAllocationChecksum ? firstChecksum : long.MinValue),
            new TimedSample(
                secondTimes[samples / 2],
                IqrPercent(secondTimes),
                secondAllocated,
                secondChecksum == secondAllocationChecksum ? secondChecksum : long.MinValue));
    }

    private static long Measure(Func<long> action, out double elapsedMilliseconds)
    {
        long start = Stopwatch.GetTimestamp();
        long checksum = action();
        elapsedMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return checksum;
    }

    private static long MeasureAllocated(Func<long> action, out long checksum)
    {
        _ = action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        checksum = action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static double IqrPercent(double[] sortedSamples)
    {
        double median = sortedSamples[sortedSamples.Length / 2];
        if (median == 0) return 0;
        double firstQuartile = sortedSamples[sortedSamples.Length / 4];
        double thirdQuartile = sortedSamples[sortedSamples.Length * 3 / 4];
        return (thirdQuartile - firstQuartile) / median * 100.0;
    }
}

internal static class SpikeCheck
{
    public static void Equal<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(message);
    }
}
