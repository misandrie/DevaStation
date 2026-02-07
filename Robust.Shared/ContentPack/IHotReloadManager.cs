using System;
using Robust.Shared.Utility;

namespace Robust.Shared.ContentPack;

public interface IHotReloadManager
{
    /// <summary>
    /// Whether a hot-reload is currently in progress.
    /// </summary>
    bool IsReloading { get; }

    /// <summary>
    /// The assembly directory used for the initial load.
    /// </summary>
    ResPath AssemblyDirectory { get; set; }

    /// <summary>
    /// The filter prefix used for the initial load (e.g., "Content.").
    /// </summary>
    string FilterPrefix { get; set; }

    /// <summary>
    /// Raised before hot-reload teardown begins. Content can subscribe to clean up static state.
    /// </summary>
    event Action? HotReloadPreparing;

    /// <summary>
    /// Raised after hot-reload completes successfully. Content can subscribe to restart rounds, etc.
    /// </summary>
    event Action? HotReloadComplete;

    void Initialize();
}
