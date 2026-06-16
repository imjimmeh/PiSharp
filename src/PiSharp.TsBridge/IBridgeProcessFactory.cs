using System.Diagnostics;

namespace PiSharp.TsBridge;

internal interface IBridgeProcessFactory
{
    IBridgeProcess Start(ProcessStartInfo startInfo);
}
