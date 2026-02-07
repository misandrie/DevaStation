using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;

namespace Robust.Client.ContentPack;

internal sealed class ClientHotReloadManager : HotReloadManager
{
    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    public override void Initialize()
    {
        base.Initialize();

        _netManager.RegisterNetMessage<MsgHotReload>(HandleHotReloadMessage);
    }

    private void HandleHotReloadMessage(MsgHotReload msg)
    {
        if (!_cfg.GetCVar(CVars.HotReload))
        {
            return;
        }

        TriggerReload(msg.AssemblyName);
    }
}
