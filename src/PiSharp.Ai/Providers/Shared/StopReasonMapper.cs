namespace PiSharp.Ai.Providers.Shared;

public static class StopReasonMapper
{
    public static string? Map(string? providerReason)
    {
        if (string.IsNullOrWhiteSpace(providerReason)) return providerReason;

        return providerReason.Trim() switch
        {
            "end_turn" or "stop" or "stop_sequence" or "STOP" or "COMPLETE" => "stop",
            "max_tokens" or "length" or "MAX_TOKENS" or "model_length" => "max_tokens",
            "tool_use" or "tool_calls" or "function_call" => "tool_use",
            "content_filter" or "safety" or "SAFETY" => "content_filter",
            "error" or "ERROR" => "error",
            var reason => reason
        };
    }
}
