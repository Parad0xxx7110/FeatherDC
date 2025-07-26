    using BenchmarkDotNet.Attributes;
    using BenchmarkDotNet.Order;
    using FeatherDC;

    // Dummy services
    public interface IServiceA { }
    public class ServiceA : IServiceA { }

    public interface IServiceB { }
    public class ServiceB : IServiceB
    {
        public IServiceA A { get; }
        public ServiceB(IServiceA a) => A = a;
    }

    public interface IServiceC { }
    public class ServiceC : IServiceC
    {
        public IServiceB B { get; }
        public IServiceA A { get; }
        public ServiceC(IServiceB b, IServiceA a)
        {
            B = b;
            A = a;
        }
    }

    public interface IDisposableService : IDisposable { }
    public class DisposableService : IDisposableService
    {
        public void Dispose()
        {

        }
    }

    public interface IMultiService { }
    public class MultiService1 : IMultiService { }
    public class MultiService2 : IMultiService { }
    public class MultiService3 : IMultiService { }

    [MemoryDiagnoser]
    [Orderer(SummaryOrderPolicy.FastestToSlowest)]
    [RankColumn]
    public class FeatherContainerBenchmark
    {
        private IFeatherProvider _singletonProvider;
        private IFeatherProvider _scopedProvider;
        private IFeatherProvider _transientProvider;

        private IFeatherScopeFactory _scopedFactory;
        private IFeatherScopeFactory _transientFactory;

        [GlobalSetup]
        public void Setup()
        {
            // Singleton container
            _singletonProvider = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>()
                .Build();

            // Scoped container
            _scopedProvider = new FeatherBuilder()
                .AddScoped<IServiceA, ServiceA>()
                .Build();
            _scopedFactory = (IFeatherScopeFactory)_scopedProvider;

            // Transient container
            _transientProvider = new FeatherBuilder()
                .AddTransient<IServiceA, ServiceA>()
                .Build();
            _transientFactory = (IFeatherScopeFactory)_transientProvider;
        }

        [Benchmark(Baseline = true)]
        public IServiceA ResolveSingleton()
        {
            return _singletonProvider.GetService<IServiceA>();
        }

        [Benchmark]
        public IServiceA ResolveScoped()
        {
            using var scope = _scopedFactory.CreateScope();
            return scope.GetService<IServiceA>();
        }

        [Benchmark]
        public IServiceA ResolveTransient()
        {
            using var scope = _transientFactory.CreateScope();
            return scope.GetService<IServiceA>();
        }

        [Benchmark]
        public void ResolveSingleton_Massive()
        {
            for (int i = 0; i < 100_000; i++)
                _singletonProvider.GetService<IServiceA>();
        }

        [Benchmark]
        public IServiceB ResolveNestedService()
        {
            var provider = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>()
                .AddTransient<IServiceB, ServiceB>()
                .Build();

            return provider.GetService<IServiceB>();
        }

        [Benchmark]
        public IServiceC ResolveDoubleNestedService()
        {
            var provider = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>()
                .AddTransient<IServiceB, ServiceB>()
                .AddTransient<IServiceC, ServiceC>()
                .Build();

            return provider.GetService<IServiceC>();
        }


        [Benchmark]
        public void ResolveDisposableService()
        {
            var provider = new FeatherBuilder()
                .AddTransient<IDisposableService, DisposableService>()
                .Build();

            using var service = provider.GetService<IDisposableService>();
        }

        [Benchmark]
        public async Task ResolveConcurrent()
        {
            var provider = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>()
                .AddTransient<IServiceB, ServiceB>()
                .AddTransient<IServiceC, ServiceC>()
                .Build();

            var tasks = new Task[20];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    var a = provider.GetService<IServiceA>();
                    var b = provider.GetService<IServiceB>();
                    var c = provider.GetService<IServiceC>();
                });
            }
            await Task.WhenAll(tasks);
        }

        [Benchmark]
        public IServiceC ColdStart()
        {
            var provider = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>()
                .AddTransient<IServiceB, ServiceB>()
                .AddTransient<IServiceC, ServiceC>()
                .Build();

            return provider.GetService<IServiceC>();
        }

        private IServiceC? _cachedC;
        [Benchmark]
        public IServiceC WarmStart()
        {
            if (_cachedC == null)
                _cachedC = ColdStart();

            return _cachedC;
        }
    }
