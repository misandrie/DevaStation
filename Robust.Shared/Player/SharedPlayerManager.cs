using System;
using System.Collections.Generic;
using System.Reflection;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.Player;

internal abstract partial class SharedPlayerManager : ISharedPlayerManager
{
    [Dependency] protected readonly IEntityManager EntManager = default!;
    [Dependency] protected readonly IComponentFactory Factory = default!;
    [Dependency] protected readonly ILogManager LogMan = default!;
    [Dependency] protected readonly IGameTiming Timing = default!;
    [Dependency] private readonly INetManager _netMan = default!;

    protected ISawmill Sawmill = default!;

    public event EventHandler<SessionStatusEventArgs>? PlayerStatusChanged;

    [ViewVariables]
    public virtual int MaxPlayers { get; protected set; }

    [ViewVariables]
    public int PlayerCount => InternalSessions.Count;

    [ViewVariables]
    public ICommonSession? LocalSession { get; protected set; }

    [ViewVariables]
    public NetUserId? LocalUser => LocalSession?.UserId;

    [ViewVariables]
    public EntityUid? LocalEntity => LocalSession?.AttachedEntity;

    public GameTick LastStateUpdate;

    [ViewVariables]
    protected readonly Dictionary<string, NetUserId> UserIdMap = new();

    public virtual void Initialize(int maxPlayers)
    {
        MaxPlayers = maxPlayers;
        Sawmill = LogMan.GetSawmill("player");
    }

    public virtual void Startup()
    {
    }

    public virtual void Shutdown()
    {
        InternalSessions.Clear();
        UserIdMap.Clear();
        PlayerData.Clear();
    }

    // DevaStation - hot-reload
    public void ClearContentEventSubscribers(Assembly oldAssembly)
    {
        if (PlayerStatusChanged == null)
            return;

        foreach (var d in PlayerStatusChanged.GetInvocationList())
        {
            if (d.Target != null && d.Target.GetType().Assembly == oldAssembly)
                PlayerStatusChanged -= (EventHandler<SessionStatusEventArgs>)d;
        }
    }
    // DevaStation end

    public bool TryGetUserId(string userName, out NetUserId userId)
    {
        return UserIdMap.TryGetValue(userName, out userId);
    }
}
