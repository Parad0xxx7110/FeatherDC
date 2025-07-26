using BenchmarkDotNet.Running;

namespace FeatherDC.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            BenchmarkRunner.Run<FeatherContainerBenchmark>();
        }
    }
}
