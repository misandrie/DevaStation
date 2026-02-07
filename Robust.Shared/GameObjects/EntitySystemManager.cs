using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Prometheus;
using Robust.Shared.IoC;
using Robust.Shared.IoC.Exceptions;
using Robust.Shared.Log;
using Robust.Shared.Profiling;
using Robust.Shared.Reflection;
using Robust.Shared.Utility;
using Robust.Shared.ViewVariables;
using Dependency = Robust.Shared.IoC.DependencyAttribute;
using Stopwatch = System.Diagnostics.Stopwatch;
#if EXCEPTION_TOLERANCE
using Robust.Shared.Exceptions;
#endif


namespace Robust.Shared.GameObjects
{
    public sealed class EntitySystemManager : IEntitySystemManager, IPostInjectInit
    {
        [Dependency] private readonly IReflectionManager _reflectionManager = default!;
        [Dependency] private readonly IEntityManager _entityManager = default!;
        [Dependency] private readonly ProfManager _profManager = default!;
        [Dependency] private readonly IDependencyCollection _dependencyCollection = default!;
        [Dependency] private readonly ILogManager _logManager = default!;

#if EXCEPTION_TOLERANCE
        [Dependency] private readonly IRuntimeLog _runtimeLog = default!;
#endif

        private ISawmill _sawmill = default!;

        internal DependencyCollection SystemDependencyCollection = default!;

        public IDependencyCollection DependencyCollection
        {
            get
            {
                if (_initialized)
                    return SystemDependencyCollection;

                throw new InvalidOperationException($"{nameof(EntitySystemManager)} has not been initialized.");
            }
        }

        private readonly List<Type> _systemTypes = new();

        private static readonly Histogram _tickUsageHistogram = Metrics.CreateHistogram("robust_entity_systems_update_usage",
            "Amount of time spent processing each entity system", new HistogramConfiguration
            {
                LabelNames = new[] {"system"},
                Buckets = Histogram.ExponentialBuckets(0.000_001, 1.5, 25)
            });

        [ViewVariables]
        private readonly List<Type> _extraLoadedTypes = new();

        private readonly Stopwatch _stopwatch = new();

        private bool _initialized;

        [ViewVariables] private UpdateReg[] _updateOrder = Array.Empty<UpdateReg>();
        [ViewVariables] private IEntitySystem[] _frameUpdateOrder = Array.Empty<IEntitySystem>();

        public bool MetricsEnabled { get; set; }

        /// <inheritdoc />
        public event EventHandler<SystemChangedArgs>? SystemLoaded;

        /// <inheritdoc />
        public event EventHandler<SystemChangedArgs>? SystemUnloaded;

        /// <exception cref="UnregisteredTypeException">Thrown if the provided type is not registered.</exception>
        public T GetEntitySystem<T>()
            where T : IEntitySystem
        {
            return SystemDependencyCollection.Resolve<T>();
        }

        public T? GetEntitySystemOrNull<T>() where T : IEntitySystem
        {
            SystemDependencyCollection.TryResolveType<T>(out var system);
            return system;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resolve<T>([NotNull] ref T? instance)
            where T : IEntitySystem
        {
            SystemDependencyCollection.Resolve(ref instance);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resolve<T1, T2>([NotNull] ref T1? instance1, [NotNull] ref T2? instance2)
            where T1 : IEntitySystem
            where T2 : IEntitySystem
        {
            SystemDependencyCollection.Resolve(ref instance1, ref instance2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resolve<T1, T2, T3>([NotNull] ref T1? instance1, [NotNull] ref T2? instance2, [NotNull] ref T3? instance3)
            where T1 : IEntitySystem
            where T2 : IEntitySystem
            where T3 : IEntitySystem
        {
            SystemDependencyCollection.Resolve(ref instance1, ref instance2, ref instance3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resolve<T1, T2, T3, T4>([NotNull] ref T1? instance1, [NotNull] ref T2? instance2, [NotNull] ref T3? instance3, [NotNull] ref T4? instance4)
            where T1 : IEntitySystem
            where T2 : IEntitySystem
            where T3 : IEntitySystem
            where T4 : IEntitySystem
        {
            SystemDependencyCollection.Resolve(ref instance1, ref instance2, ref instance3, ref instance4);
        }

        /// <inheritdoc />
        public bool TryGetEntitySystem<T>([NotNullWhen(true)] out T? entitySystem)
            where T : IEntitySystem
        {
            return SystemDependencyCollection.TryResolveType<T>(out entitySystem);
        }

        /// <inheritdoc />
        public void Initialize(bool discover = true)
        {
            // Tempted to make this an assert
            // However, EntityManager calls this directly so we'd need to remove that and manually call it.
            if (_initialized) return;

            var excludedTypes = new HashSet<Type>();

            SystemDependencyCollection = new(_dependencyCollection);
            var subTypes = new Dictionary<Type, Type>();
            _systemTypes.Clear();
            IEnumerable<Type> systems;

            if (discover)
            {
                systems = _reflectionManager.GetAllChildren<IEntitySystem>().Concat(_extraLoadedTypes);
            }
            else
            {
                systems = _extraLoadedTypes;
            }

            foreach (var type in systems)
            {
                _sawmill.Debug("Initializing entity system {0}", type);

                SystemDependencyCollection.Register(type);
                _systemTypes.Add(type);

                excludedTypes.Add(type);
                if (subTypes.ContainsKey(type))
                {
                    subTypes.Remove(type);
                }

                // also register systems under their supertypes, so they can be retrieved by their supertype.
                // We don't do this if there are multiple subtype systems of that supertype though, otherwise
                // it wouldn't be clear which instance to return when asking for the supertype
                foreach (var baseType in GetBaseTypes(type))
                {
                    // already known that there are multiple subtype systems of this type,
                    // so don't register under the supertype because it would be unclear
                    // which instance to return if we retrieved it by the supertype
                    if (excludedTypes.Contains(baseType)) continue;

                    if (subTypes.ContainsKey(baseType))
                    {
                        subTypes.Remove(baseType);
                        excludedTypes.Add(baseType);
                    }
                    else
                    {
                        subTypes.Add(baseType, type);
                    }
                }
            }

            foreach (var (baseType, type) in subTypes)
            {
                SystemDependencyCollection.Register(baseType, type, overwrite: true);
                _systemTypes.Remove(baseType);
            }

            SystemDependencyCollection.BuildGraph();

            foreach (var systemType in _systemTypes)
            {
                var system = (IEntitySystem)SystemDependencyCollection.ResolveType(systemType);
                system.Initialize();
                SystemLoaded?.Invoke(this, new SystemChangedArgs(system));
            }

            // Create update order for entity systems.
            var (fUpdate, update) = CalculateUpdateOrder(_systemTypes, subTypes, SystemDependencyCollection);

            _frameUpdateOrder = fUpdate.ToArray();
            _updateOrder = update
                .Select(s => new UpdateReg
                {
                    System = s,
                    Monitor = _tickUsageHistogram.WithLabels(s.GetType().Name)
                })
                .ToArray();

            _initialized = true;
        }

        private static (IEnumerable<IEntitySystem> frameUpd, IEnumerable<IEntitySystem> upd)
            CalculateUpdateOrder(
                List<Type> systemTypes,
                Dictionary<Type, Type> subTypes,
                DependencyCollection dependencyCollection)
        {
            var allNodes = new List<TopologicalSort.GraphNode<IEntitySystem>>();
            var typeToNode = new Dictionary<Type, TopologicalSort.GraphNode<IEntitySystem>>();

            foreach (var systemType in systemTypes)
            {
                var node = new TopologicalSort.GraphNode<IEntitySystem>((IEntitySystem) dependencyCollection.ResolveType(systemType));

                typeToNode.Add(systemType, node);
                allNodes.Add(node);
            }

            foreach (var (type, system) in subTypes)
            {
                var node = typeToNode[system];
                typeToNode[type] = node;
            }

            foreach (var node in typeToNode.Values)
            {
                foreach (var after in node.Value.UpdatesAfter)
                {
                    var system = typeToNode[after];

                    system.Dependant.Add(node);
                }

                foreach (var before in node.Value.UpdatesBefore)
                {
                    var system = typeToNode[before];

                    node.Dependant.Add(system);
                }
            }

            var order = TopologicalSort.Sort(allNodes).ToArray();
            var frameUpdate = order.Where(p => NeedsFrameUpdate(p.GetType()));
            var update = order.Where(p => NeedsUpdate(p.GetType()));

            return (frameUpdate, update);
        }

        private static IEnumerable<Type> GetBaseTypes(Type type) {
            if(type.BaseType == null) return type.GetInterfaces();

            return Enumerable.Repeat(type.BaseType, 1)
                .Concat(type.GetInterfaces())
                .Concat(type.GetInterfaces().SelectMany<Type, Type>(GetBaseTypes))
                .Concat(GetBaseTypes(type.BaseType));
        }

        // DevaStation start - hot-reload

        ///
        /// Okay so we decide to unfuck systems at runtime, what does it take?
        /// For one, we have to find out what a system is, and where it is.
        /// For two, we have to fuck up EntityManager to unlock subscriptions because ofc you cant unsub from events EVEN IF there's a public API method just for that!
        /// For three, we shut it all down
        /// For four, we unperson them, they never happened, and redo update order
        /// And at last we have removed a system from the game
        ///
        public void RemoveContentSystems(Assembly oldAssembly)
        {
            _sawmill.Info("Killing systems");

            var contentTypes = new List<Type>();
            foreach (var systemType in _systemTypes)
            {
                if (systemType.Assembly == oldAssembly)
                {
                    contentTypes.Add(systemType);
                }
            }

            _sawmill.Info($"Found {contentTypes.Count} content systems to kill, just completely eviscerate, maim, gore even");

            if (_entityManager is EntityManager entManEarly)
            {
                entManEarly.EventBusInternal.UnlockSubscriptions();
            }

            foreach (var systemType in contentTypes)
            {
                try
                {
                    var system = (IEntitySystem)SystemDependencyCollection.ResolveType(systemType);

                    SystemUnloaded?.Invoke(this, new SystemChangedArgs(system));

                    system.Shutdown();

                    _entityManager.EventBus.UnsubscribeEvents(system);
                }
                catch (Exception e)
                {
                    _sawmill.Error($"Error removing system {systemType.Name}: {e}");
                }
            }

            var contentTypeSet = new HashSet<Type>(contentTypes);
            _systemTypes.RemoveAll(t => contentTypeSet.Contains(t));

            SystemDependencyCollection.RemoveRegistrations(oldAssembly);

            var excludedTypes = new HashSet<Type>();
            var subTypes = new Dictionary<Type, Type>();

            foreach (var type in _systemTypes)
            {
                excludedTypes.Add(type);
                subTypes.Remove(type);

                foreach (var baseType in GetBaseTypes(type))
                {
                    if (excludedTypes.Contains(baseType)) continue;

                    if (subTypes.Remove(baseType))
                    {
                        excludedTypes.Add(baseType);
                    }
                    else
                    {
                        subTypes.Add(baseType, type);
                    }
                }
            }

            var (fUpdate, update) = CalculateUpdateOrder(_systemTypes, subTypes, SystemDependencyCollection);

            _frameUpdateOrder = fUpdate.ToArray();
            _updateOrder = update
                .Select(s => new UpdateReg
                {
                    System = s,
                    Monitor = _tickUsageHistogram.WithLabels(s.GetType().Name)
                })
                .ToArray();
            _sawmill.Info("Killed systems");
        }

        // Basically above but GOOD this time
        public void AddContentSystems()
        {
            _sawmill.Info("Reanimating systems");

            var allSystemTypes = _reflectionManager.GetAllChildren<IEntitySystem>();

            var existingTypes = new HashSet<Type>(_systemTypes);
            var newTypes = allSystemTypes
                .Where(type => !existingTypes.Contains(type))
                .Where(type => !SystemDependencyCollection.TryResolveType(type, out _))
                .ToList();

            _sawmill.Info($"Found {newTypes.Count} systems to reanimate.");

            if (newTypes.Count == 0)
            {
                return;
            }

            var excludedTypes = new HashSet<Type>(_systemTypes);
            var subTypes = new Dictionary<Type, Type>();

            foreach (var type in _systemTypes)
            {
                foreach (var baseType in GetBaseTypes(type))
                {
                    if (excludedTypes.Contains(baseType)) continue;

                    if (subTypes.Remove(baseType))
                    {
                        excludedTypes.Add(baseType);
                    }
                    else
                    {
                        subTypes.Add(baseType, type);
                    }
                }
            }

            foreach (var type in newTypes)
            {
                _sawmill.Debug($"Registering system {type.Name}");

                SystemDependencyCollection.Register(type);
                _systemTypes.Add(type);

                excludedTypes.Add(type);
                subTypes.Remove(type);

                // Also register under supertypes
                foreach (var baseType in GetBaseTypes(type))
                {
                    if (excludedTypes.Contains(baseType)) continue;

                    if (subTypes.Remove(baseType))
                    {
                        excludedTypes.Add(baseType);
                    }
                    else
                    {
                        subTypes.Add(baseType, type);
                    }
                }
            }

            foreach (var (baseType, type) in subTypes)
            {
                // Skip anything that survived
                if (SystemDependencyCollection.TryResolveType(baseType, out _))
                {
                    _systemTypes.Remove(baseType);
                    continue;
                }

                SystemDependencyCollection.Register(baseType, type, overwrite: true);
                _systemTypes.Remove(baseType);
            }

            SystemDependencyCollection.BuildGraph();

            foreach (var systemType in newTypes)
            {
                try
                {
                    var system = (IEntitySystem)SystemDependencyCollection.ResolveType(systemType);
                    system.Initialize();
                    SystemLoaded?.Invoke(this, new SystemChangedArgs(system));
                }
                catch (Exception e)
                {
                    _sawmill.Error($"Error initializing system {systemType.Name}: {e}");
                }
            }

            var allSubTypes = new Dictionary<Type, Type>();
            var allExcluded = new HashSet<Type>();

            foreach (var type in _systemTypes)
            {
                allExcluded.Add(type);
                allSubTypes.Remove(type);

                foreach (var baseType in GetBaseTypes(type))
                {
                    if (allExcluded.Contains(baseType)) continue;

                    if (allSubTypes.Remove(baseType))
                    {
                        allExcluded.Add(baseType);
                    }
                    else
                    {
                        allSubTypes.Add(baseType, type);
                    }
                }
            }

            var (fUpdate, update) = CalculateUpdateOrder(_systemTypes, allSubTypes, SystemDependencyCollection);

            _frameUpdateOrder = fUpdate.ToArray();
            _updateOrder = update
                .Select(s => new UpdateReg
                {
                    System = s,
                    Monitor = _tickUsageHistogram.WithLabels(s.GetType().Name)
                })
                .ToArray();

            if (_entityManager is EntityManager entMan)
            {
                entMan.EventBusInternal.LockSubscriptions();
            }

            _sawmill.Info("Systems do be reanimated tho");
        }
        // DevaStation end

        /// <inheritdoc />
        public void Shutdown()
        {
            // System.Values is modified by RemoveSystem
            foreach (var systemType in _systemTypes)
            {
                if(SystemDependencyCollection == null) continue;
                var system = (IEntitySystem)SystemDependencyCollection.ResolveType(systemType);
                SystemUnloaded?.Invoke(this, new SystemChangedArgs(system));

                // DevaStation start - hot-reload
                try
                {
                    system.Shutdown();
                }
                catch (Exception e)
                {
                    _sawmill.Error($"Caught exception shutting down system {systemType.Name}: {e}");
                }
                // DevaStation end

                _entityManager.EventBus.UnsubscribeEvents(system);
            }

            Clear();
        }

        public void Clear()
        {
            _extraLoadedTypes.Clear();
            _systemTypes.Clear();
            _updateOrder = Array.Empty<UpdateReg>();
            _frameUpdateOrder = Array.Empty<IEntitySystem>();
            _initialized = false;
            SystemDependencyCollection?.Clear();
        }

        /// <inheritdoc />
        public void TickUpdate(float frameTime, bool noPredictions)
        {
            foreach (var updReg in _updateOrder)
            {
                if (noPredictions && !updReg.System.UpdatesOutsidePrediction)
                    continue;

                if (MetricsEnabled)
                {
                    _stopwatch.Restart();
                }
#if EXCEPTION_TOLERANCE
                try
                {
#endif
                    var sw = ProfSampler.StartNew();
                    updReg.System.Update(frameTime);
                    _profManager.WriteValue(updReg.System.GetType().Name, sw);
#if EXCEPTION_TOLERANCE
                }
                catch (Exception e)
                {
                    _runtimeLog.LogException(e, "entsys");
                }
#endif

                if (MetricsEnabled)
                {
                    updReg.Monitor.Observe(_stopwatch.Elapsed.TotalSeconds);
                }
            }
        }

        /// <inheritdoc />
        public void FrameUpdate(float frameTime)
        {
            foreach (var system in _frameUpdateOrder)
            {
#if EXCEPTION_TOLERANCE
                try
                {
#endif
                    var sw = ProfSampler.StartNew();
                    system.FrameUpdate(frameTime);
                    _profManager.WriteValue(system.GetType().Name, sw);
#if EXCEPTION_TOLERANCE
                }
                catch (Exception e)
                {
                    _runtimeLog.LogException(e, "entsys");
                }
#endif
            }
        }

        public void LoadExtraSystemType<T>() where T : IEntitySystem, new()
        {
            if (_initialized)
            {
                throw new InvalidOperationException(
                    "Cannot use LoadExtraSystemType when the entity system manager is initialized.");
            }

            _extraLoadedTypes.Add(typeof(T));
        }

        public IEnumerable<Type> GetEntitySystemTypes()
        {
            return _systemTypes;
        }

        public bool TryGetEntitySystem(Type sysType, [NotNullWhen(true)] out object? system)
        {
            return SystemDependencyCollection.TryResolveType(sysType, out system);
        }

        public object GetEntitySystem(Type sysType)
        {
            return SystemDependencyCollection.ResolveType(sysType);
        }

        private static bool NeedsUpdate(Type type)
        {
            if (!typeof(EntitySystem).IsAssignableFrom(type))
            {
                return true;
            }

            var mUpdate = type.GetMethod(nameof(EntitySystem.Update), new[] {typeof(float)});

            DebugTools.AssertNotNull(mUpdate);

            return mUpdate!.DeclaringType != typeof(EntitySystem);
        }

        private static bool NeedsFrameUpdate(Type type)
        {
            if (!typeof(EntitySystem).IsAssignableFrom(type))
            {
                return true;
            }

            var mFrameUpdate = type.GetMethod(nameof(EntitySystem.FrameUpdate), new[] {typeof(float)});

            DebugTools.AssertNotNull(mFrameUpdate);

            return mFrameUpdate!.DeclaringType != typeof(EntitySystem);
        }

        internal IEnumerable<Type> FrameUpdateOrder => _frameUpdateOrder.Select(c => c.GetType());
        internal IEnumerable<Type> TickUpdateOrder => _updateOrder.Select(c => c.System.GetType());

        private struct UpdateReg
        {
            [ViewVariables] public IEntitySystem System;
            [ViewVariables] public Histogram.Child Monitor;

            public override string? ToString()
            {
                return System.ToString();
            }
        }

        void IPostInjectInit.PostInject()
        {
            _sawmill = _logManager.GetSawmill("go.sys");
        }
    }

    public sealed class SystemChangedArgs : EventArgs
    {
        public IEntitySystem System { get; }

        public SystemChangedArgs(IEntitySystem system)
        {
            System = system;
        }
    }
}
