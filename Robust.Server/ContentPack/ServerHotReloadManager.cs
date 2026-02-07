using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;

namespace Robust.Server.ContentPack;

internal sealed class ServerHotReloadManager : HotReloadManager
{
    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    private AssemblyFileWatcher? _fileWatcher;

    public override void Initialize()
    {
        base.Initialize();

        _netManager.RegisterNetMessage<MsgHotReload>();

        if (!_cfg.GetCVar(CVars.HotReload))
            return;

        _fileWatcher = new AssemblyFileWatcher();
        IoCManager.InjectDependencies(_fileWatcher);
        _fileWatcher.Initialize(AssemblyDirectory, FilterPrefix, TriggerReloadForAssembly);
    }

    private void TriggerReloadForAssembly(string assemblyName)
    {
        if (!_cfg.GetCVar(CVars.HotReload))
            return;

        _netManager.ServerSendToAll(new MsgHotReload { AssemblyName = assemblyName });

        TriggerReload(assemblyName);
    }
}
