namespace PiSharp.Agent.Harness;

// Hook system reserved for Phase 8; harness currently wires loop config delegates directly.
// Extension points (before_provider_request, tool_call, session_before_compact, session_before_tree)
// are available through standard .NET event/callback patterns when a hook subscriber API is added.
