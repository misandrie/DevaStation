using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Reflection;
using Robust.Shared.Timing;

namespace Robust.Shared.ContentPack
{
    internal abstract class BaseModLoader : IPostInjectInit
    {
        [Dependency] protected readonly IReflectionManager ReflectionManager = default!;
        [Dependency] protected readonly ILogManager LogManager = default!;
        [Dependency] private readonly IDependencyCollection _dependencies = default!;

        private readonly List<ModuleTestingCallbacks> _testingCallbacks = new();

        /// <summary>
        ///     Loaded assemblies.
        /// </summary>
        protected readonly List<ModInfo> Mods = new();

        protected ISawmill Sawmill { get; private set; } = default!;

        public IEnumerable<Assembly> LoadedModules => Mods.Select(p => p.GameAssembly);

        public Assembly GetAssembly(string name)
        {
            return Mods.Select(p => p.GameAssembly).Single(p => p.GetName().Name == name);
        }

        protected void InitMod(Assembly assembly)
        {
            var mod = new ModInfo(assembly);

            ReflectionManager.LoadAssemblies(mod.GameAssembly);

            var entryPoints = mod.GameAssembly.GetTypes().Where(t => typeof(GameShared).IsAssignableFrom(t));

            foreach (var entryPoint in entryPoints)
            {
                var entryPointInstance = (GameShared) Activator.CreateInstance(entryPoint)!;
                entryPointInstance.Dependencies = _dependencies;
                if (_testingCallbacks != null)
                {
                    entryPointInstance.SetTestingCallbacks(_testingCallbacks);
                }

                mod.EntryPoints.Add(entryPointInstance);
            }

            Mods.Add(mod);
        }

        public bool IsContentAssembly(Assembly typeAssembly)
        {
            foreach (var mod in Mods)
            {
                if (mod.GameAssembly == typeAssembly)
                {
                    return true;
                }
            }

            return false;
        }

        // DevaStation start - hot-reload
        internal void ClearMods()
        {
            Mods.Clear();
        }

        /// <summary>
        /// Removes a single mod by assembly name. Shuts down and disposes its entry points.
        /// Returns the old assembly, or null if not found.
        /// </summary>
        internal Assembly? RemoveMod(string assemblyName)
        {
            for (int i = 0; i < Mods.Count; i++)
            {
                var mod = Mods[i];
                if (mod.GameAssembly.GetName().Name != assemblyName)
                    continue;

                // Shutdown and dispose entry points for this mod only
                foreach (var entry in mod.EntryPoints)
                {
                    entry.Shutdown();
                }
                foreach (var entry in mod.EntryPoints)
                {
                    entry.Dispose();
                }

                Mods.RemoveAt(i);
                return mod.GameAssembly;
            }

            return null;
        }

        /// <summary>
        /// Broadcasts a run level change to entry points of a specific assembly only.
        /// </summary>
        public void BroadcastRunLevelForAssembly(ModRunLevel level, Assembly assembly)
        {
            foreach (var mod in Mods)
            {
                if (mod.GameAssembly != assembly)
                    continue;

                foreach (var entry in mod.EntryPoints)
                {
                    switch (level)
                    {
                        case ModRunLevel.PreInit:
                            entry.PreInit();
                            break;
                        case ModRunLevel.Init:
                            entry.Init();
                            break;
                        case ModRunLevel.PostInit:
                            entry.PostInit();
                            break;
                        default:
                            Sawmill.Error($"Unknown RunLevel: {level}");
                            break;
                    }
                }
            }
        }
        // DevaStation end

        public void BroadcastRunLevel(ModRunLevel level)
        {
            foreach (var mod in Mods)
            {
                foreach (var entry in mod.EntryPoints)
                {
                    switch (level)
                    {
                        case ModRunLevel.PreInit:
                            entry.PreInit();
                            break;
                        case ModRunLevel.Init:
                            entry.Init();
                            break;
                        case ModRunLevel.PostInit:
                            entry.PostInit();
                            break;
                        default:
                            Sawmill.Error($"Unknown RunLevel: {level}");
                            break;
                    }
                }
            }
        }

        public void BroadcastUpdate(ModUpdateLevel level, FrameEventArgs frameEventArgs)
        {
            foreach (var module in Mods)
            {
                foreach (var entryPoint in module.EntryPoints)
                {
                    entryPoint.Update(level, frameEventArgs);
                }
            }
        }

        public void SetModuleBaseCallbacks(ModuleTestingCallbacks testingCallbacks)
        {
            _testingCallbacks.Add(testingCallbacks);
        }

        public void Shutdown()
        {
            foreach (var module in Mods)
            {
                foreach (var entryPoint in module.EntryPoints)
                {
                    entryPoint.Shutdown();
                }

                foreach (var entryPoint in module.EntryPoints)
                {
                    entryPoint.Dispose();
                }
            }
        }

        void IPostInjectInit.PostInject()
        {
            Sawmill = LogManager.GetSawmill("res.mod");
        }

        /// <summary>
        ///     Holds info about a loaded assembly.
        /// </summary>
        protected sealed class ModInfo
        {
            public ModInfo(Assembly gameAssembly)
            {
                GameAssembly = gameAssembly;
                EntryPoints = new List<GameShared>();
            }

            public Assembly GameAssembly { get; }
            public List<GameShared> EntryPoints { get; }
        }
    }
}
