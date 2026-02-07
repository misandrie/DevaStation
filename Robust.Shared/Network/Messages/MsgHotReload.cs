using Lidgren.Network;
using Robust.Shared.Serialization;

namespace Robust.Shared.Network.Messages;

public sealed class MsgHotReload : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;

    public string AssemblyName { get; set; } = string.Empty;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        AssemblyName = buffer.ReadString();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(AssemblyName);
    }
}
