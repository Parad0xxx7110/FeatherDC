using System.Diagnostics;

namespace FeatherDC.Tests.PerformanceTests
{

    // Kinda deprecated since the addition of the BenchmarkDotNet project, but kept for reference.
    public class FeatherContainerPerformanceTests
    {
        private const int Iterations = 100_000;

        [Fact]
        public void Singleton_Resolution_Should_Be_Fast()
        {
            var builder = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>();
            var provider = builder.Build();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var svc = provider.GetService<IServiceA>();
            }
            sw.Stop();

            var avg = sw.Elapsed.TotalMilliseconds / Iterations;
           
            System.Console.WriteLine($"Singleton resolution avg: {avg:F6} ms");

            // Optionnel : assert que c'est moins de 0.01ms par résolution
            Assert.True(avg < 0.01, "Singleton resolution is too slow :(...");
        }

        [Fact]
        public void Transient_Resolution_Should_Be_Fast()
        {
            var builder = new FeatherBuilder()
                .AddTransient<IServiceA, ServiceA>();
            var provider = builder.Build();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var svc = provider.GetService<IServiceA>();
            }
            sw.Stop();

            var avg = sw.Elapsed.TotalMilliseconds / Iterations;
            System.Console.WriteLine($"Transient resolution avg: {avg:F6} ms");

            Assert.True(avg < 0.05, "Transient resolution is too slow :(...");
        }

        [Fact]
        public void Scoped_Resolution_Should_Be_Fast()
        {
            var builder = new FeatherBuilder()
                .AddScoped<IServiceA, ServiceA>();
            var provider = builder.Build();
            var scopeFactory = (IFeatherScopeFactory)provider;

            using var scope = scopeFactory.CreateScope();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var svc = scope.GetService<IServiceA>();
            }
            sw.Stop();

            var avg = sw.Elapsed.TotalMilliseconds / Iterations;
            System.Console.WriteLine($"Scoped resolution avg: {avg:F6} ms");

            Assert.True(avg < 0.02, "Scoped resolution is too slow :(...");
        }

        [Fact]
        public void Singleton_Resolution_Should_Be_ThreadSafe_And_Performant()
        {
            var builder = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>();

            var provider = builder.Build();

            IServiceA[] results = new IServiceA[Iterations];
            var sw = Stopwatch.StartNew();

            Parallel.For(0, Iterations, i =>
            {
                results[i] = provider.GetService<IServiceA>();
            });

            sw.Stop();

            var avg = sw.Elapsed.TotalMilliseconds / Iterations;
            System.Console.WriteLine($"Singleton parallel resolution avg: {avg:F6} ms");

            Assert.True(results.Distinct().Count() == 1, "Multiple singleton instances created");
            
            Assert.True(avg < 0.02, "Singleton parallel resolution is too slow :(...");
        }

        [Fact]
        public void HighConcurrency_MultiThreaded_Resolution_Should_Be_ThreadSafe()
        {
            var builder = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>()
                .AddScoped<IServiceB, ServiceB>();

            var provider = builder.Build();
            var scopeFactory = (IFeatherScopeFactory)provider;

            const int threadCount = 1000;

            
            IServiceA[] singletonInstances = new IServiceA[threadCount];
            IServiceB[] scopedInstancesSameScope = new IServiceB[threadCount];

            using var scope = scopeFactory.CreateScope();

            Parallel.For(0, threadCount, i =>
            {
                // Root scope singleton
                singletonInstances[i] = provider.GetService<IServiceA>();
                // Scoped service in the same scope
                scopedInstancesSameScope[i] = scope.GetService<IServiceB>();
            });

            // Every singleton should be the same instance
            var distinctSingletons = singletonInstances.Distinct().Count();
            Assert.Equal(1, distinctSingletons);

            // Every scoped service in the same scope should be the same instance
            var distinctScoped = scopedInstancesSameScope.Distinct().Count();
            Assert.Equal(1, distinctScoped);
        }

    }
}