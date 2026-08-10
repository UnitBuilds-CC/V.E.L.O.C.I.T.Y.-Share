using BenchmarkDotNet.Running;
using VelocityShare.Benchmarks;

namespace VelocityShare.Benchmarks;

public class Program
{
    public static async Task Main(string[] args)
    {
        // If --integration flag is passed, run the integration benchmark
        if (args.Contains("--integration"))
        {
            await SyncIntegrationBenchmark.RunAsync();
            return;
        }

        // Otherwise run BenchmarkDotNet micro-benchmarks
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
