using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;

namespace PiSharp.Extensions.Testing;

public sealed record CapturedMessage(
    AgentMessage Message,
    ExtensionMessageDelivery Delivery,
    bool TriggerTurn,
    DateTimeOffset CapturedAt);
