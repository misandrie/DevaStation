using System;

namespace Robust.Shared.ContentPack;

/// <summary>
/// Exception thrown when assembly hot-reload fails at any stage.
/// </summary>
public sealed class HotReloadException : Exception
{
    public HotReloadException(string message) : base(message)
    {
    }

    public HotReloadException(string message, Exception inner) : base(message, inner)
    {
    }
}
