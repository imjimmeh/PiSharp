using System.Text.Json;
using PiSharp.Server.Serialization;

namespace PiSharp.Client;

public sealed record DaemonLease(int Pid, int Port, string ApiKey, DateTimeOffset StartedAt, string Version)
{
    public static DaemonLease? Load(JsonElement element) => element.Deserialize<DaemonLease>(ServerJsonSerializer.Options);

    public string ToJson() => JsonSerializer.Serialize(this, ServerJsonSerializer.Options);
}
