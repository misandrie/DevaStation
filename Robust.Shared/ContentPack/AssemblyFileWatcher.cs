using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Asynchronous;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Utility;

namespace Robust.Shared.ContentPack;

internal sealed class AssemblyFileWatcher : IDisposable
{
    [Dependency] private readonly IResourceManagerInternal _res = default!;
    [Dependency] private readonly ITaskManager _taskManager = default!;
    [Dependency] private readonly ILogManager _logManager = default!;

    private readonly List<FileSystemWatcher> _watchers = new();
    private ISawmill _sawmill = default!;
    private CancellationTokenSource? _debounceCts;
    private Action<string>? _onAssemblyChanged;
    private string? _pendingChangedAssembly;

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    /// <param name="assemblyDirectory">VFS path where assemblies are mounted (e.g. /Assemblies/).</param>
    /// <param name="filterPrefix">DLL filter prefix (e.g. "Content.").</param>
    /// <param name="onAssemblyChanged">Callback invoked when assembly files change (after debounce).</param>
    /// <remarks>Assembly path resolution is done via VFS</remarks>
    public void Initialize(ResPath assemblyDirectory, string filterPrefix, Action<string> onAssemblyChanged)
    {
        _sawmill = _logManager.GetSawmill("hotreload.watcher");
        _onAssemblyChanged = onAssemblyChanged;
        var watchDirs = new HashSet<string>();

        foreach (var filePath in _res.ContentFindRelativeFiles(assemblyDirectory)
                     .Where(p => p.Filename.StartsWith(filterPrefix) && p.Extension == "dll"))
        {
            var fullVfsPath = assemblyDirectory / filePath;
            if (_res.TryGetDiskFilePath(fullVfsPath, out var diskPath))
            {
                var dir = Path.GetDirectoryName(diskPath);
                if (dir != null)
                    watchDirs.Add(dir);
            }
        }

        foreach (var dir in watchDirs)
        {
            try
            {
                var watcher = new FileSystemWatcher(dir, "*.dll")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                watcher.Changed += OnDllChanged;
                watcher.Created += OnDllChanged;
                _watchers.Add(watcher);

            }
            catch (Exception e)
            {
                _sawmill.Error($"Failed to create watcher for {dir}: {e}");
            }
        }

        if (_watchers.Count == 0)
        {
            _sawmill.Info("No assembly directories found to watch. Hot-reload file watcher is snoozing.");
        }
    }

    private void OnDllChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce in case dlls change multiple times, multiple places
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();

        _pendingChangedAssembly = Path.GetFileNameWithoutExtension(e.FullPath);

        var token = _debounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, token);
            }
            catch (TaskCanceledException)
            {
                return; // I rebuilt pls restart
            }

            var assemblyName = _pendingChangedAssembly ?? "Unknown";
            _sawmill.Info($"Assembly file changed (after debounce): {e.FullPath} (assembly: {assemblyName})");
            _sawmill.Info("Scheduling hot-reload");

            _taskManager.RunOnMainThread(() =>
            {
                try
                {
                    _onAssemblyChanged?.Invoke(assemblyName);
                }
                catch (Exception ex)
                {
                    _sawmill.Fatal($"Hot-reload callback failed: {ex}");
                }
            });
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnDllChanged;
            watcher.Created -= OnDllChanged;
            watcher.Dispose();
        }

        _watchers.Clear();
    }
}
