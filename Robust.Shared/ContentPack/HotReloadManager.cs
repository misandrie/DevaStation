using System;
using System.Diagnostics;
using System.Reflection;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Reflection;
using Robust.Shared.Utility;

namespace Robust.Shared.ContentPack;

/// <summary>
/// Does... does the thing.
/// </summary>
[Virtual]
internal class HotReloadManager
{
    [Dependency] private readonly IModLoaderInternal _modLoader = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;
    [Dependency] private readonly ISerializationManager _serializationManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ISharedPlayerManager _playerManager = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly IReflectionManager _reflectionManager = default!;

    protected ISawmill Sawmill = default!;

    /// <summary>
    /// Whether a hot-reload is currently in progress. Prevents re-entrant reloads.
    /// </summary>
    public bool IsReloading { get; private set; }

    /// <summary>
    /// The assembly directory used for the initial load. Stored so we can reload from the same path.
    /// </summary>
    public ResPath AssemblyDirectory { get; set; }

    /// <summary>
    /// The filter prefix used for the initial load (e.g., "Content.").
    /// </summary>
    public string FilterPrefix { get; set; } = "Content.";

    /// <summary>
    /// Raised before hot-reload teardown begins. Content can subscribe to clean up static state.
    /// </summary>
    public event Action? HotReloadPreparing;

    /// <summary>
    /// Raised after hot-reload completes successfully. Content can subscribe to restart rounds, etc.
    /// </summary>
    public event Action? HotReloadComplete;

    public virtual void Initialize()
    {
        Sawmill = _logManager.GetSawmill("hotreload");
    }

    protected virtual void TriggerReload(string assemblyName)
    {
        if (!_cfg.GetCVar(CVars.HotReload))
        {
            Sawmill.Warning("Hot reload triggered but dev.hot_reload CVar is disabled. Ignoring.");
            return;
        }

        if (IsReloading)
        {
            Sawmill.Warning("Hot reload already in progress. Ignoring re-entrant trigger.");
            return;
        }

        IsReloading = true;
        var totalSw = Stopwatch.StartNew();
        Sawmill.Info($"Hot-reloading {assemblyName}");

        try
        {
            // Before reloading maybe have to check if we have that assembly
            Assembly? oldAssembly = null;
            foreach (var asm in _modLoader.LoadedModules)
            {
                if (asm.GetName().Name == assemblyName)
                {
                    oldAssembly = asm;
                    break;
                }
            }

            if (oldAssembly == null)
            {
                Sawmill.Warning($"{assemblyName} wasn't present. Skipping reload");
                return;
            }

            Sawmill.Info("Tearing everything down");
            HotReloadPreparing?.Invoke();

            var stepSw = Stopwatch.StartNew();
            _entityManager.FlushEntities();
            Sawmill.Info($"Entities flushed in {stepSw.ElapsedMilliseconds}ms");

            TeardownAssembly(oldAssembly);

            stepSw.Restart();
            var result = _modLoader.ReloadSingleAssembly(assemblyName, AssemblyDirectory, FilterPrefix);
            if (result == null)
            {
                throw new HotReloadException($"Failed to reload assembly '{assemblyName}'.");
            }
            var newAssembly = result.Value.newAssembly;
            Sawmill.Info($"Assembly reloaded in {stepSw.ElapsedMilliseconds}ms");

            ReinitializeAssembly(newAssembly);

            HotReloadComplete?.Invoke();
            Sawmill.Info($"Finished hot-reloading in {totalSw.ElapsedMilliseconds}ms");
        }
        catch (Exception e)
        {
            Sawmill.Fatal("Hot-reload failed:");
            Sawmill.Fatal($"{e}");
            throw new HotReloadException("Assembly hot-reload failed. See log for details.", e);
        }
        finally
        {
            IsReloading = false;
        }
    }

    /// <summary>
    /// Removes registrations from a single old assembly.
    /// </summary>
    private void TeardownAssembly(Assembly oldAssembly)
    {
        var sw = Stopwatch.StartNew();
        Sawmill.Info($"Tearing down {oldAssembly.GetName().Name}");

        _playerManager.ClearContentEventSubscribers(oldAssembly);
        foreach (var sessionData in _playerManager.GetAllPlayerData())
        {
            sessionData.ContentDataUncast = null;
        }

        _entityManager.EntitySysManager.RemoveContentSystems(oldAssembly);
        _componentFactory.RemoveComponentsByAssembly(oldAssembly);
        _serializationManager.RemoveContentTypes(oldAssembly);
        _prototypeManager.RemoveKindsByAssembly(oldAssembly);
        IoCManager.RemoveRegistrations(oldAssembly);
        _netManager.RemoveNetMessages(oldAssembly);

        // But chuddha, where's the reflectionmanager?
        // ReflectionManager is handled in ReloadSingleAssembly.

        Sawmill.Info($"Teardown for {oldAssembly.GetName()} done in {sw.ElapsedMilliseconds}ms");
    }

    private void ReinitializeAssembly(Assembly newAssembly)
    {
        var asm = newAssembly.GetName().Name!;

        var sw = Stopwatch.StartNew();

        var stepSw = Stopwatch.StartNew();
        _modLoader.BroadcastRunLevelForAssembly(ModRunLevel.PreInit, newAssembly);
        Sawmill.Info($"[{asm}] PreInit done in {stepSw.ElapsedMilliseconds}ms");

        stepSw.Restart();
        _serializationManager.RegisterContentTypes();

        stepSw.Restart();
        _modLoader.BroadcastRunLevelForAssembly(ModRunLevel.Init, newAssembly);
        Sawmill.Info($"[{asm}] Init done in {stepSw.ElapsedMilliseconds}ms");

        _componentFactory.GenerateNetIds();

        // This takes a trillion years TODO find a way to skip this
        stepSw.Restart();
        _prototypeManager.Reset();
        Sawmill.Info($"[{asm}] Prototypes done in {stepSw.ElapsedMilliseconds}ms");

        stepSw.Restart();
        _entityManager.EntitySysManager.AddContentSystems();
        Sawmill.Info($"[{asm}] Entity systems done in {stepSw.ElapsedMilliseconds}ms");

        stepSw.Restart();
        _modLoader.BroadcastRunLevelForAssembly(ModRunLevel.PostInit, newAssembly);
        Sawmill.Info($"[{asm}] PostInit done in {stepSw.ElapsedMilliseconds}ms");

        Sawmill.Info($"[{asm}] Reloading done in {sw.ElapsedMilliseconds}ms");
    }
}
