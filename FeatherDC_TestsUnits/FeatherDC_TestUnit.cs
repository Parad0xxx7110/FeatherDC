using System.Collections.Concurrent;

namespace FeatherDC.Tests
{
    // Test interfaces
    public interface IServiceA { Guid Id { get; } }
    public interface IServiceB { Guid Id { get; } }
    public interface IServiceC { Guid Id { get; } }
    public interface IServiceD { IServiceA A { get; } IServiceB B { get; } }

    // Test implementations
    public class ServiceA : IServiceA
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ServiceA() { }
    }

    public class ServiceB : IServiceB
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ServiceB(IServiceA a)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
        }
    }

    public class ServiceC : IServiceC, IDisposable, IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public bool AsyncDisposed { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();

        public void Dispose()
        {
            Disposed = true;
        }

        public interface IServiceD { }


        public ValueTask DisposeAsync()
        {
            AsyncDisposed = true;
            return ValueTask.CompletedTask;
        }

        public class DisposableService : IDisposable, IAsyncDisposable
        {
            public bool Disposed { get; private set; } = false;
            public bool AsyncDisposed { get; private set; } = false;

            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                AsyncDisposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    public class ServiceD : IServiceD
    {
        public IServiceA A { get; }
        public IServiceB B { get; }
        public ServiceD(IServiceA a, IServiceB b)
        {
            A = a;
            B = b;
        }
    }

    public class FeatherContainerTests
    {
        [Fact]
        public void Singleton_Should_Return_Same_Instance_And_Warmup()
        {
            var builder = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>();

            var provider = builder.Build();

            // Warmup: singleton built by build() via warmup
            var instance1 = provider.GetService<IServiceA>();
            var instance2 = provider.GetService<IServiceA>();

            Assert.NotNull(instance1);
            Assert.Same(instance1, instance2);
        }





        [Fact]
        public void Scoped_Should_Return_Same_Instance_Within_Scope_And_Different_Between_Scopes()
        {
            var builder = new FeatherBuilder()
                .AddScoped<IServiceA, ServiceA>();

            var provider = builder.Build();

            var scopeFactory = (IFeatherScopeFactory)provider; // cast to access CreateScope()

            using var scope1 = scopeFactory.CreateScope();
            using var scope2 = scopeFactory.CreateScope();

            var instance1 = scope1.GetService<IServiceA>();
            var instance2 = scope1.GetService<IServiceA>();
            var instance3 = scope2.GetService<IServiceA>();

            Assert.Same(instance1, instance2);
            Assert.NotSame(instance1, instance3);
        }

        [Fact]
        public void Transient_Should_Return_Different_Instances()
        {
            var builder = new FeatherBuilder()
                .AddTransient<IServiceA, ServiceA>();

            var provider = builder.Build();

            var instance1 = provider.GetService<IServiceA>();
            var instance2 = provider.GetService<IServiceA>();

            Assert.NotSame(instance1, instance2);
        }

        [Fact]
        public void Factory_Should_Resolve_Dependencies()
        {
            var builder = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>()
                .AddTransient<IServiceB>(sp =>
                {
                    var a = sp.GetService<IServiceA>();
                    return new ServiceB(a);
                });

            var provider = builder.Build();

            var b = provider.GetService<IServiceB>();

            Assert.NotNull(b);
        }

        [Fact]
        public void Instance_Should_Be_Registered_As_Singleton()
        {
            var inst = new ServiceA();

            var builder = new FeatherBuilder()
                .AddSingleton<IServiceA>(inst);

            var provider = builder.Build();

            var resolved = provider.GetService<IServiceA>();

            Assert.Same(inst, resolved);
        }


        // Checking the throw here but should also check the inner exception for more details
        [Fact]
        public void Circular_Dependency_Should_Throw()
        {
            var builder = new FeatherBuilder()
                .AddTransient<IServiceA, ServiceAWithCircularDependency>()
                .AddTransient<IServiceB, ServiceBWithCircularDependency>();

            var provider = builder.Build();

            Assert.Throws<InvalidOperationException>(() => provider.GetService<IServiceA>());
        }

        [Fact]
        public void Scoped_Should_Be_Isolated_Per_Scope_Even_With_Parallel_Creation()
        {
            var builder = new FeatherBuilder().AddScoped<IServiceA, ServiceA>();
            var provider = builder.Build();
            var scopeFactory = (IFeatherScopeFactory)provider;

            var scope1 = scopeFactory.CreateScope();
            var scope2 = scopeFactory.CreateScope();

            var bag1 = new ConcurrentBag<IServiceA>();
            var bag2 = new ConcurrentBag<IServiceA>();

            Parallel.Invoke(
                () => Parallel.For(0, 20, _ => bag1.Add(scope1.GetService<IServiceA>())),
                () => Parallel.For(0, 20, _ => bag2.Add(scope2.GetService<IServiceA>()))
            );

            Assert.All(bag1, i => Assert.Same(bag1.First(), i));
            Assert.All(bag2, i => Assert.Same(bag2.First(), i));
            Assert.NotSame(bag1.First(), bag2.First());

            scope1.Dispose();
            scope2.Dispose();
        }

        [Fact]
        public void Deep_Dependency_Resolution()
        {
            var builder = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>()
                .AddScoped<IServiceB, ServiceB>()
                .AddTransient<IServiceD, ServiceD>();

            var provider = builder.Build();
            var scope = ((IFeatherScopeFactory)provider).CreateScope();

            var d = scope.GetService<IServiceD>();
            Assert.NotNull(d);
            Assert.NotNull(((ServiceD)d).A);
            Assert.NotNull(((ServiceD)d).B);
        }

        [Fact]
        public void Scoped_Dispose_Should_Dispose_Instances()
        {
            var builder = new FeatherBuilder()
                .AddScoped<IServiceC, ServiceC>();

            var provider = builder.Build();
            var scopeFactory = (IFeatherScopeFactory)provider;

            var scope = scopeFactory.CreateScope();

            var instance = scope.GetService<IServiceC>() as ServiceC;

            Assert.False(instance.Disposed);

            scope.Dispose();

            Assert.True(instance.Disposed);
        }




        [Fact]
        public async Task Scoped_DisposeAsync_Should_DisposeAsync_Instances()
        {
            var builder = new FeatherBuilder()
                .AddScoped<IServiceC, ServiceC>();

            var provider = builder.Build();
            var scopeFactory = (IFeatherScopeFactory)provider;

            await using var scope = scopeFactory.CreateScope();

            var instance = scope.GetService<IServiceC>() as ServiceC;

            Assert.False(instance.AsyncDisposed);

            await scope.DisposeAsync();

            Assert.True(instance.AsyncDisposed);
        }

        [Fact]
        public void Singleton_Should_Be_ThreadSafe()
        {
            var builder = new FeatherBuilder()
                .AddSingleton<IServiceA, ServiceA>();

            var provider = builder.Build();

            IServiceA[] results = new IServiceA[50];
            Parallel.For(0, 50, i =>
            {
                results[i] = provider.GetService<IServiceA>();
            });

            var distinct = results.Distinct().Count();
            Assert.Equal(1, distinct);
        }

    }

    // Circuylar dependency testing classes
    public class ServiceAWithCircularDependency : IServiceA
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ServiceAWithCircularDependency(IServiceB b) { }
    }

    public class ServiceBWithCircularDependency : IServiceB
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ServiceBWithCircularDependency(IServiceA a) { }
    }
}
