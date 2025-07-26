using System.Collections.Concurrent;



namespace FeatherDC
{
    public enum FeatherLifetime { Singleton, Scoped, Transient }

    public interface IFeatherProvider : IDisposable, IAsyncDisposable
    {
        T GetService<T>();
        object GetService(Type type);
    }

    public interface IFeatherScope : IFeatherProvider { }

    public interface IFeatherScopeFactory
    {
        IFeatherScope CreateScope();
    }

    public class FeatherBuilder
    {
        private readonly List<FeatherDescriptor> _descriptors = new();

        public FeatherBuilder AddSingleton<TInterface, TImplementation>()
            where TImplementation : class, TInterface
        {
            _descriptors.Add(new FeatherDescriptor(typeof(TInterface), typeof(TImplementation), FeatherLifetime.Singleton));
            return this;
        }

        public FeatherBuilder AddSingleton<TInterface>(TInterface instance)
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));
            _descriptors.Add(new FeatherDescriptor(typeof(TInterface), instance));
            return this;
        }

        public FeatherBuilder AddScoped<TInterface, TImplementation>()
            where TImplementation : class, TInterface
        {
            _descriptors.Add(new FeatherDescriptor(typeof(TInterface), typeof(TImplementation), FeatherLifetime.Scoped));
            return this;
        }

        public FeatherBuilder AddTransient<TInterface, TImplementation>()
            where TImplementation : class, TInterface
        {
            _descriptors.Add(new FeatherDescriptor(typeof(TInterface), typeof(TImplementation), FeatherLifetime.Transient));
            return this;
        }

        public FeatherBuilder AddTransient<TInterface>(Func<IFeatherProvider, TInterface> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            // Casting result to object to avoid nullability warnings
            _descriptors.Add(new FeatherDescriptor(typeof(TInterface), sp => (object)factory(sp)!, FeatherLifetime.Transient));
            return this;
        }

        public IFeatherProvider Build()
        {
            var singletons = new ConcurrentDictionary<Type, Lazy<object>>();
            var descriptorMap = _descriptors.ToDictionary(d => d.ServiceType);

            foreach (var d in descriptorMap.Values)
            {
                if (d.Instance == null && d.Factory == null)
                    d.Factory = CompileFactory(d.ImplementationType);
            }

            var provider = new FeatherProvider(descriptorMap, singletons);

            foreach (var descriptor in descriptorMap.Values)
            {
                if (descriptor.Lifetime == FeatherLifetime.Singleton && descriptor.Instance == null)
                {
                    _ = provider.GetService(descriptor.ServiceType);
                }
            }

            return provider;
        }

        private static Func<IFeatherProvider, object> CompileFactory(Type implType)
        {
            var ctor = implType.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (ctor == null)
                throw new InvalidOperationException($"No public constructor found for {implType.Name}");

            var paramInfos = ctor.GetParameters();
            var spParam = System.Linq.Expressions.Expression.Parameter(typeof(IFeatherProvider), "sp");

            var args = new System.Linq.Expressions.Expression[paramInfos.Length];
            for (int i = 0; i < paramInfos.Length; i++)
            {
                var pType = paramInfos[i].ParameterType;
                if (IsPrimitiveOrString(pType))
                {
                    args[i] = System.Linq.Expressions.Expression.Constant(GetPrimitiveDefault(pType), pType);
                }
                else
                {
                    var getServiceMethod = typeof(IFeatherProvider).GetMethod(nameof(IFeatherProvider.GetService), new[] { typeof(Type) });
                    if (getServiceMethod == null)
                        // throw new InvalidOperationException("Fatal error");
                        throw new InvalidOperationException("FATAL ERROR : IFeatherProvider contract broken — GetService(Type) method not found."); // Just a bit more explicit for futur logs
                    var callGetService = System.Linq.Expressions.Expression.Call(spParam, getServiceMethod, System.Linq.Expressions.Expression.Constant(pType));
                    args[i] = System.Linq.Expressions.Expression.Convert(callGetService, pType);
                }
            }

            var newExpr = System.Linq.Expressions.Expression.New(ctor, args);
            var lambda = System.Linq.Expressions.Expression.Lambda<Func<IFeatherProvider, object>>(
                System.Linq.Expressions.Expression.Convert(newExpr, typeof(object)),
                spParam
            );

            return lambda.Compile();
        }

        private static bool IsPrimitiveOrString(Type t) =>
            t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime);

        private static object GetPrimitiveDefault(Type t)
        {
            if (t == typeof(string)) return "";
            if (t == typeof(int)) return 0;
            if (t == typeof(bool)) return false;
            if (t == typeof(decimal)) return 0m;
            if (t == typeof(DateTime)) return default(DateTime);
            return Activator.CreateInstance(t) ?? throw new InvalidOperationException($"Unable to create instance for {t.FullName}.");
        }
    }

    public class FeatherDescriptor
    {
        public Type ServiceType { get; }
        public Type ImplementationType { get; }
        public object? Instance { get; }
        public Func<IFeatherProvider, object>? Factory { get; set; }
        public FeatherLifetime Lifetime { get; }

        public FeatherDescriptor(Type serviceType, Type implementationType, FeatherLifetime lifetime)
        {
            ServiceType = serviceType;
            ImplementationType = implementationType;
            Lifetime = lifetime;
        }

        public FeatherDescriptor(Type serviceType, object instance)
        {
            ServiceType = serviceType;
            Instance = instance;
            Lifetime = FeatherLifetime.Singleton;
            ImplementationType = instance.GetType(); 
        }

        public FeatherDescriptor(Type serviceType, Func<IFeatherProvider, object> factory, FeatherLifetime lifetime)
        {
            ServiceType = serviceType;
            Factory = factory;
            Lifetime = lifetime;
            ImplementationType = serviceType;
        }
    }

    public class FeatherProvider : IFeatherProvider, IFeatherScopeFactory
    {
        private readonly IReadOnlyDictionary<Type, FeatherDescriptor> _descriptors;
        private readonly ConcurrentDictionary<Type, Lazy<object>> _singletons;
        private readonly ConcurrentDictionary<Type, object>? _scopedCache; 
        private static readonly AsyncLocal<HashSet<Type>> _callStack = new();

        public FeatherProvider(IReadOnlyDictionary<Type, FeatherDescriptor> descriptors, ConcurrentDictionary<Type, Lazy<object>> singletons, ConcurrentDictionary<Type, object>? scopedCache = null)
        {
            _descriptors = descriptors;
            _singletons = singletons;
            _scopedCache = scopedCache;
        }

        public T GetService<T>() => (T)GetService(typeof(T));

        public object GetService(Type serviceType)
        {
            if (serviceType == typeof(IFeatherProvider) || serviceType == typeof(IFeatherScopeFactory))
                return this;

            if (!_descriptors.TryGetValue(serviceType, out var descriptor))
                throw new InvalidOperationException($"Service {serviceType.Name} not registered.");

            var callStack = _callStack.Value ??= new HashSet<Type>();

            if (callStack.Contains(serviceType))
                throw new InvalidOperationException($"Circular dependency detected for service type {serviceType.Name}");

            callStack.Add(serviceType);
            try
            {
                if (descriptor.Instance != null) return descriptor.Instance;

                if (descriptor.Factory != null)
                {
                    if (descriptor.Lifetime == FeatherLifetime.Singleton)
                    {
                        return _singletons.GetOrAdd(serviceType, _ =>
                            new Lazy<object>(() => descriptor.Factory(this), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
                    }

                    if (descriptor.Lifetime == FeatherLifetime.Scoped)
                    {
                        if (_scopedCache == null)
                            throw new InvalidOperationException("Scoped services can only be resolved from a scope.");

                        // Using GetorAdd to ensure thread safety in scoped cache
                        return _scopedCache.GetOrAdd(serviceType, _ => descriptor.Factory(this));
                    }

                    return descriptor.Factory(this);
                }

                throw new InvalidOperationException($"No factory found for {serviceType.Name}");
            }
            finally
            {
                callStack.Remove(serviceType);
                if (callStack.Count == 0)
                    _callStack.Value = null; // Avoid memory leak by clearing the AsyncLocal call stack, compiler warning CS8625 but it's safe here.
            }
        }

        public IFeatherScope CreateScope() => new FeatherScope(_descriptors, _singletons);

        public void Dispose()
        {
            foreach (var lazy in _singletons.Values)
                if (lazy.IsValueCreated && lazy.Value is IDisposable d) d.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var lazy in _singletons.Values)
            {
                if (lazy.IsValueCreated)
                {
                    if (lazy.Value is IAsyncDisposable ad) await ad.DisposeAsync();
                    else if (lazy.Value is IDisposable d) d.Dispose();
                }
            }
        }
    }

    public class FeatherScope : IFeatherScope
    {
        private readonly FeatherProvider _scopedProvider;
        private readonly ConcurrentDictionary<Type, object> _scopedCache = new();

        public FeatherScope(IReadOnlyDictionary<Type, FeatherDescriptor> descriptors, ConcurrentDictionary<Type, Lazy<object>> singletons)
        {
            _scopedProvider = new FeatherProvider(descriptors, singletons, _scopedCache);
        }

        public T GetService<T>() => _scopedProvider.GetService<T>();
        public object GetService(Type type) => _scopedProvider.GetService(type);

        public void Dispose()
        {
            foreach (var d in _scopedCache.Values.OfType<IDisposable>())
                d.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var d in _scopedCache.Values)
            {
                if (d is IAsyncDisposable ad) await ad.DisposeAsync();
                else if (d is IDisposable id) id.Dispose();
            }
        }
    }
}
