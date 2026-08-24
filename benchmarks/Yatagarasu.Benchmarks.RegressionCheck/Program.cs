using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yatagarasu.Benchmarks.RegressionCheck;

/// <summary>
/// BenchmarkDotNet JsonExporter.Full の出力を読み、baseline と比較して
/// 20% (かつ &gt; 3σ) 以上の wall-clock 劣化があれば exit code 1 を返す CLI。
///
/// 想定ユース:
///   regression-check --baseline benchmarks/baselines/main.json
///                    --current  artifacts/pr-merged.json
///   regression-check --merge    artifacts/results
///                    --output   benchmarks/baselines/main.json
///
/// Mean / StandardDeviation はナノ秒 (BDN 既定単位)。
/// 基準は: new.Mean &gt; old.Mean * 1.20 かつ new.Mean &gt; old.Mean + 3 * old.SD。
/// 相対閾値だけだとノイズに過敏で、σ だけだと小さい変化を見逃すため両方を要求する。
/// </summary>
public static class Program
{
    public const double RegressionRatio = 1.20;
    public const double SigmaMultiplier = 3.0;

    public static int Main(string[] args)
    {
        try
        {
            var parsed = ParseArgs(args);
            return parsed.Mode switch
            {
                CliMode.Compare => RunCompare(parsed),
                CliMode.Merge   => RunMerge(parsed),
                _               => PrintHelp(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"regression-check: {ex.Message}");
            return 2;
        }
    }

    private static int RunCompare(CliArgs args)
    {
        if (string.IsNullOrEmpty(args.Baseline) || string.IsNullOrEmpty(args.Current))
            throw new ArgumentException("--baseline and --current are both required for compare mode.");

        var baseline = LoadReport(args.Baseline);
        var current  = LoadReport(args.Current);

        // LoadReport は読み込み時に Benchmarks を非 null 化し、各 FullName も補完する。
        // その事後条件はモデルの nullable 注釈には現れないため、利用側で非 null を明示する。
        var byName = baseline.Benchmarks!.ToDictionary(b => b.FullName!, b => b.Statistics);
        int regressions = 0, comparisons = 0, missingBaseline = 0;
        foreach (var bench in current.Benchmarks!)
        {
            if (!byName.TryGetValue(bench.FullName!, out var oldStats))
            {
                missingBaseline++;
                Console.WriteLine($"  [skip] no baseline   {bench.FullName}  cur={Fmt(bench.Statistics.Mean)}");
                continue;
            }
            comparisons++;
            double oldMean = oldStats.Mean;
            double newMean = bench.Statistics.Mean;
            double sd      = oldStats.StandardDeviation;
            double ratio   = oldMean > 0 ? newMean / oldMean : double.NaN;
            double sigmaGap = (newMean - oldMean) / Math.Max(sd, 1e-9);

            bool ratioFail = newMean > oldMean * RegressionRatio;
            bool sigmaFail = newMean > oldMean + SigmaMultiplier * sd;
            bool regressed = ratioFail && sigmaFail;

            string tag = regressed ? "[FAIL]" : (newMean < oldMean * 0.9 ? "[fast]" : "[ ok ]");
            Console.WriteLine(
                $"  {tag} {bench.FullName}  " +
                $"old={Fmt(oldMean)} cur={Fmt(newMean)}  " +
                $"ratio={ratio,5:F2}x σ-gap={sigmaGap,5:F2}");

            if (regressed) regressions++;
        }
        Console.WriteLine();
        Console.WriteLine($"Compared {comparisons} benchmarks ({missingBaseline} without baseline). Regressions: {regressions}.");
        return regressions > 0 ? 1 : 0;
    }

    private static int RunMerge(CliArgs args)
    {
        if (string.IsNullOrEmpty(args.MergeDir) || string.IsNullOrEmpty(args.Output))
            throw new ArgumentException("--merge and --output are both required for merge mode.");

        if (!Directory.Exists(args.MergeDir))
            throw new DirectoryNotFoundException($"Merge source not found: {args.MergeDir}");

        var merged = new List<BenchmarkEntry>();
        foreach (var path in Directory.EnumerateFiles(args.MergeDir, "*-report-full.json", SearchOption.AllDirectories))
        {
            var report = LoadReport(path);
            merged.AddRange(report.Benchmarks!);   // LoadReport が非 null 化済み
        }
        merged.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));

        var output = new Report
        {
            Title = "Yatagarasu Operator Micro-Benchmarks Baseline",
            GeneratedAt = DateTimeOffset.UtcNow.ToString("o"),
            Benchmarks = merged,
        };
        WriteReport(args.Output, output);
        Console.WriteLine($"Wrote {merged.Count} benchmark entries to {args.Output}.");
        return 0;
    }

    private static Report LoadReport(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Report not found: {path}", path);
        using var fs = File.OpenRead(path);
        var report = JsonSerializer.Deserialize<Report>(fs, JsonOptions)
                     ?? throw new InvalidDataException($"Could not parse {path} as a BDN-style report.");
        report.Benchmarks ??= new List<BenchmarkEntry>();
        foreach (var b in report.Benchmarks)
        {
            // BDN's full report uses Method/Type/Namespace; FullName synthesizes them.
            // Accept a pre-computed FullName too (used by main.json baseline format).
            b.FullName ??= ComposeFullName(b);
        }
        return report;
    }

    private static string ComposeFullName(BenchmarkEntry b)
    {
        var ns = string.IsNullOrEmpty(b.Namespace) ? "" : b.Namespace + ".";
        var type = b.Type ?? "";
        var method = b.Method ?? "";
        var p = string.IsNullOrEmpty(b.Parameters) ? "" : "(" + b.Parameters + ")";
        return $"{ns}{type}.{method}{p}";
    }

    private static void WriteReport(string path, Report report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        JsonSerializer.Serialize(fs, report, JsonOptionsWrite);
    }

    private static string Fmt(double ns) => ns switch
    {
        >= 1_000_000_000 => $"{ns / 1_000_000_000:F2} s",
        >= 1_000_000     => $"{ns / 1_000_000:F2} ms",
        >= 1_000         => $"{ns / 1_000:F2} µs",
        _                => $"{ns:F2} ns",
    };

    private static CliArgs ParseArgs(string[] args)
    {
        var r = new CliArgs();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--baseline": r.Baseline = args[++i]; r.Mode = CliMode.Compare; break;
                case "--current":  r.Current  = args[++i]; r.Mode = CliMode.Compare; break;
                case "--merge":    r.MergeDir = args[++i]; r.Mode = CliMode.Merge;   break;
                case "--output":   r.Output   = args[++i]; break;
                case "-h":
                case "--help":     r.Mode = CliMode.Help; break;
                default:
                    throw new ArgumentException($"Unknown arg: {args[i]}");
            }
        }
        if (r.Mode == CliMode.None) r.Mode = CliMode.Help;
        return r;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("Yatagarasu.Benchmarks.RegressionCheck —  regression sentinel");
        Console.WriteLine();
        Console.WriteLine("  compare two BDN reports and exit 1 on regression:");
        Console.WriteLine("    --baseline <main.json> --current <pr.json>");
        Console.WriteLine();
        Console.WriteLine("  merge a directory of BDN per-class reports into one baseline JSON:");
        Console.WriteLine("    --merge <results-dir> --output <baseline.json>");
        Console.WriteLine();
        Console.WriteLine($"  thresholds: ratio > {RegressionRatio:F2}x AND new > old + {SigmaMultiplier:F0}σ");
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions JsonOptionsWrite = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal enum CliMode { None, Help, Compare, Merge }

internal sealed class CliArgs
{
    public CliMode Mode { get; set; }
    public string? Baseline { get; set; }
    public string? Current { get; set; }
    public string? MergeDir { get; set; }
    public string? Output { get; set; }
}

public sealed class Report
{
    public string? Title { get; set; }
    public string? GeneratedAt { get; set; }
    public List<BenchmarkEntry>? Benchmarks { get; set; }
}

public sealed class BenchmarkEntry
{
    public string? FullName { get; set; }
    public string? Namespace { get; set; }
    public string? Type { get; set; }
    public string? Method { get; set; }
    public string? Parameters { get; set; }
    public BenchStatistics Statistics { get; set; } = new();
}

public sealed class BenchStatistics
{
    public double Mean { get; set; }
    public double StandardDeviation { get; set; }
}
