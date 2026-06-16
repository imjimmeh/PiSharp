using PiSharp.Agent.Core.Models;

namespace PiSharp.Ai.Models;

/// <summary>
/// Lightweight catalog entry wrapping a ModelDescriptor with provider+id identity.
/// Used by ModelRegistry for lookup, cost, and thinking-level queries.
/// </summary>
public sealed record CatalogModel(
    string Provider,
    string Id,
    ModelDescriptor Descriptor);
