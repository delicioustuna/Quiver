using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Quiver.Core;

namespace Quiver.Benchmarks.Standalone;

/// <summary>既存 1 accumulator と展開版を同一プロセスで比較する。</summary>
public static class ScorerAccumulatorRunner
{
    public static int Run()
    {
        Console.WriteLine("=== scorer accumulator spike ===");
        Console.WriteLine("metric, dim, baseline_ns, unrolled_ns, speedup");
        foreach (int dim in new[] { 64, 384, 768, 1536 })
        {
            var random = new Random(1234 + dim);
            var left = new float[dim];
            var right = new float[dim];
            for (int i = 0; i < dim; i++)
            {
                left[i] = (float)(random.NextDouble() * 2 - 1);
                right[i] = (float)(random.NextDouble() * 2 - 1);
            }

            int operations = Math.Max(20_000, 40_000_000 / dim);
            foreach (var metric in new[]
                     {
                         DistanceMetric.Dot,
                         DistanceMetric.Cosine,
                         DistanceMetric.Euclidean,
                     })
            {
                for (int i = 0; i < 2_000; i++)
                {
                    _ = Baseline(metric, left, right);
                    _ = Candidate(metric, left, right);
                }

                double baseline = Measure(
                    operations, () => Baseline(metric, left, right));
                double candidate = Measure(
                    operations, () => Candidate(metric, left, right));
                Console.WriteLine(
                    $"{metric}, {dim}, {baseline:F2}, {candidate:F2}, {baseline / candidate:F3}");
            }
        }
        return 0;
    }

    private static double Measure(int operations, Func<float> score)
    {
        double best = double.MaxValue;
        float checksum = 0;
        for (int round = 0; round < 5; round++)
        {
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < operations; i++) checksum += score();
            long elapsed = Stopwatch.GetTimestamp() - started;
            best = Math.Min(best, elapsed * 1_000_000_000.0 / Stopwatch.Frequency / operations);
        }
        GC.KeepAlive(checksum);
        return best;
    }

    private static float Candidate(
        DistanceMetric metric,
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right)
        => metric switch
        {
            DistanceMetric.Dot => DotUnrolled(left, right),
            DistanceMetric.Cosine => CosineUnrolled(left, right),
            _ => EuclideanUnrolled(left, right),
        };

    private static float Baseline(
        DistanceMetric metric,
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right)
        => metric switch
        {
            DistanceMetric.Dot => Dot(left, right),
            DistanceMetric.Cosine => Cosine(left, right),
            _ => Euclidean(left, right),
        };

    private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var a = MemoryMarshal.Cast<float, Vector<float>>(left);
        var b = MemoryMarshal.Cast<float, Vector<float>>(right);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < a.Length; i++) acc += a[i] * b[i];
        float sum = Vector.Dot(acc, Vector<float>.One);
        for (int i = a.Length * Vector<float>.Count; i < left.Length; i++)
            sum += left[i] * right[i];
        return sum;
    }

    private static float Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var a = MemoryMarshal.Cast<float, Vector<float>>(left);
        var b = MemoryMarshal.Cast<float, Vector<float>>(right);
        var dot = Vector<float>.Zero;
        var na = Vector<float>.Zero;
        var nb = Vector<float>.Zero;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        float d = Vector.Dot(dot, Vector<float>.One);
        float x = Vector.Dot(na, Vector<float>.One);
        float y = Vector.Dot(nb, Vector<float>.One);
        for (int i = a.Length * Vector<float>.Count; i < left.Length; i++)
        {
            d += left[i] * right[i];
            x += left[i] * left[i];
            y += right[i] * right[i];
        }
        float denominator = MathF.Sqrt(x) * MathF.Sqrt(y);
        return denominator == 0 ? 0 : d / denominator;
    }

    private static float Euclidean(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var a = MemoryMarshal.Cast<float, Vector<float>>(left);
        var b = MemoryMarshal.Cast<float, Vector<float>>(right);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            acc += d * d;
        }
        float sum = Vector.Dot(acc, Vector<float>.One);
        for (int i = a.Length * Vector<float>.Count; i < left.Length; i++)
        {
            float d = left[i] - right[i];
            sum += d * d;
        }
        return MathF.Sqrt(sum);
    }

    private static float DotUnrolled(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var a = MemoryMarshal.Cast<float, Vector<float>>(left);
        var b = MemoryMarshal.Cast<float, Vector<float>>(right);
        var a0 = Vector<float>.Zero;
        var a1 = Vector<float>.Zero;
        var a2 = Vector<float>.Zero;
        var a3 = Vector<float>.Zero;
        int i = 0;
        for (; i + 3 < a.Length; i += 4)
        {
            a0 += a[i] * b[i];
            a1 += a[i + 1] * b[i + 1];
            a2 += a[i + 2] * b[i + 2];
            a3 += a[i + 3] * b[i + 3];
        }
        for (; i < a.Length; i++) a0 += a[i] * b[i];
        float sum = Vector.Dot((a0 + a1) + (a2 + a3), Vector<float>.One);
        for (i = a.Length * Vector<float>.Count; i < left.Length; i++)
            sum += left[i] * right[i];
        return sum;
    }

    private static float CosineUnrolled(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var a = MemoryMarshal.Cast<float, Vector<float>>(left);
        var b = MemoryMarshal.Cast<float, Vector<float>>(right);
        var d0 = Vector<float>.Zero;
        var d1 = Vector<float>.Zero;
        var a0 = Vector<float>.Zero;
        var a1 = Vector<float>.Zero;
        var b0 = Vector<float>.Zero;
        var b1 = Vector<float>.Zero;
        int i = 0;
        for (; i + 1 < a.Length; i += 2)
        {
            var x0 = a[i];
            var y0 = b[i];
            var x1 = a[i + 1];
            var y1 = b[i + 1];
            d0 += x0 * y0;
            a0 += x0 * x0;
            b0 += y0 * y0;
            d1 += x1 * y1;
            a1 += x1 * x1;
            b1 += y1 * y1;
        }
        for (; i < a.Length; i++)
        {
            var x = a[i];
            var y = b[i];
            d0 += x * y;
            a0 += x * x;
            b0 += y * y;
        }
        float dot = Vector.Dot(d0 + d1, Vector<float>.One);
        float na = Vector.Dot(a0 + a1, Vector<float>.One);
        float nb = Vector.Dot(b0 + b1, Vector<float>.One);
        for (i = a.Length * Vector<float>.Count; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            na += left[i] * left[i];
            nb += right[i] * right[i];
        }
        float denominator = MathF.Sqrt(na) * MathF.Sqrt(nb);
        return denominator == 0 ? 0 : dot / denominator;
    }

    private static float EuclideanUnrolled(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var a = MemoryMarshal.Cast<float, Vector<float>>(left);
        var b = MemoryMarshal.Cast<float, Vector<float>>(right);
        var a0 = Vector<float>.Zero;
        var a1 = Vector<float>.Zero;
        var a2 = Vector<float>.Zero;
        var a3 = Vector<float>.Zero;
        int i = 0;
        for (; i + 3 < a.Length; i += 4)
        {
            var d0 = a[i] - b[i];
            var d1 = a[i + 1] - b[i + 1];
            var d2 = a[i + 2] - b[i + 2];
            var d3 = a[i + 3] - b[i + 3];
            a0 += d0 * d0;
            a1 += d1 * d1;
            a2 += d2 * d2;
            a3 += d3 * d3;
        }
        for (; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            a0 += d * d;
        }
        float sum = Vector.Dot((a0 + a1) + (a2 + a3), Vector<float>.One);
        for (i = a.Length * Vector<float>.Count; i < left.Length; i++)
        {
            float d = left[i] - right[i];
            sum += d * d;
        }
        return MathF.Sqrt(sum);
    }
}
