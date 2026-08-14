namespace PiSharp.Git;

/// <summary>
/// Partition validation + dependency ordering for commit groups.
///
/// Validation rejects: duplicate files across groups, files absent from the inventory,
/// rename pairs split across groups, unknown <c>dependsOn</c> ids, empty groups, and
/// dependency cycles (reported as a concrete path). Ordering is Kahn's topological sort
/// with a deterministic tie-break: higher group score first, then input index.
/// </summary>
public sealed class CommitGraph
{
    public sealed record GroupNode(string Id, string Message, int Index, IReadOnlyList<string> Files, IReadOnlyList<string> DependsOn);

    public sealed record GraphResult(
        bool IsValid,
        string? Error,
        IReadOnlyList<string>? Cycle,
        IReadOnlyList<GroupNode>? Ordered);

    public GraphResult BuildAndOrder(
        IReadOnlyList<CommitGroupInput> groups,
        IReadOnlyList<ChangeItem> inventory)
    {
        if (groups.Count == 0)
        {
            return Invalid("At least one commit group is required.");
        }

        var nodes = new List<GroupNode>(groups.Count);
        var idToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var id = string.IsNullOrWhiteSpace(group.Id) ? i.ToString() : group.Id;
            if (!seenIds.Add(id))
            {
                return Invalid($"Duplicate group id '{id}'. Group ids must be unique (omit Id to use positional ids).");
            }

            idToIndex[id] = i;
            nodes.Add(new GroupNode(id, group.Message, i, group.Files, group.DependsOn ?? []));
        }

        // Files must exist in the inventory; no file may appear in two groups.
        var inventoryPaths = new Dictionary<string, ChangeItem>(StringComparer.Ordinal);
        foreach (var item in inventory)
        {
            inventoryPaths[item.Path] = item;
        }

        var ownerByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.Files.Count == 0)
            {
                return Invalid($"Group '{node.Id}' has no files.");
            }

            foreach (var file in node.Files)
            {
                if (!inventoryPaths.TryGetValue(file, out var item))
                {
                    var renameSource = FindRenameSource(inventory, file);
                    if (renameSource is not null)
                    {
                        return Invalid(
                            $"Rename pair split across groups: '{file}' is the source of a rename to '{renameSource}' " +
                            $"and must stay in the same group as its destination.");
                    }

                    return Invalid($"Group '{node.Id}' references '{file}' which is not in the change inventory " +
                                   "(stale or absent status — re-run the inventory call).");
                }

                if (ownerByPath.TryGetValue(file, out var otherId))
                {
                    return Invalid($"File '{file}' appears in groups '{otherId}' and '{node.Id}'. " +
                                   "Every file must be in exactly one group.");
                }

                ownerByPath[file] = node.Id;
            }
        }

        // Partition coverage: every inventory change must be in exactly one group.
        var uncovered = inventoryPaths.Keys.FirstOrDefault(path => !ownerByPath.ContainsKey(path));
        if (uncovered is not null)
        {
            return Invalid($"Inventory file '{uncovered}' is not covered by any group. " +
                           "Every change must be in exactly one group.");
        }

        // Dependency edges + cycle detection via Kahn's algorithm.
        var inDegree = new int[nodes.Count];
        var dependents = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            dependents[i] = [];
        }

        foreach (var node in nodes)
        {
            var seenDeps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dep in node.DependsOn)
            {
                if (!seenDeps.Add(dep))
                {
                    continue; // duplicate dependency edge — count once
                }

                if (!idToIndex.TryGetValue(dep, out var depIndex))
                {
                    return Invalid($"Group '{node.Id}' depends on unknown group id '{dep}'.");
                }

                if (depIndex == node.Index)
                {
                    return Invalid($"Group '{node.Id}' depends on itself.");
                }

                dependents[depIndex].Add(node.Index);
                inDegree[node.Index]++;
            }
        }

        var ready = new PriorityQueue<int, (int Score, int Index)>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (inDegree[i] == 0)
            {
                ready.Enqueue(i, (ScoreOf(nodes[i], inventoryPaths), i));
            }
        }

        var order = new List<GroupNode>(nodes.Count);
        var processed = new bool[nodes.Count];
        while (ready.Count > 0)
        {
            var index = ready.Dequeue();
            if (processed[index])
            {
                continue;
            }

            processed[index] = true;
            order.Add(nodes[index]);
            foreach (var dependent in dependents[index])
            {
                if (--inDegree[dependent] == 0)
                {
                    ready.Enqueue(dependent, (ScoreOf(nodes[dependent], inventoryPaths), dependent));
                }
            }
        }

        if (order.Count != nodes.Count)
        {
            return new GraphResult(false, "Commit group dependency cycle detected.", ExtractCycle(nodes, inDegree), null);
        }

        return new GraphResult(true, null, null, order);
    }

    private static GraphResult Invalid(string error) => new(false, error, null, null);

    private static int ScoreOf(GroupNode node, Dictionary<string, ChangeItem> inventoryPaths)
    {
        var max = 0;
        foreach (var file in node.Files)
        {
            if (inventoryPaths.TryGetValue(file, out var item))
            {
                max = Math.Max(max, item.Score);
            }
        }

        return -max; // PriorityQueue dequeues the SMALLEST priority; negate so higher score first.
    }

    private static string? FindRenameSource(IReadOnlyList<ChangeItem> inventory, string path)
        => inventory.FirstOrDefault(item => item.IsRename && item.RenameSource == path)?.Path;

    private static IReadOnlyList<string> ExtractCycle(List<GroupNode> nodes, int[] inDegree)
    {
        // Every node left unprocessed has in-degree > 0 within the remaining subgraph,
        // so walking backwards through smallest-index in-neighbors must revisit a node.
        var remaining = Enumerable.Range(0, nodes.Count).Where(i => inDegree[i] > 0).ToArray();
        if (remaining.Length == 0)
        {
            return [];
        }

        var inNeighbors = new Dictionary<int, List<int>>();
        foreach (var index in remaining)
        {
            inNeighbors[index] = [];
        }

        foreach (var node in nodes)
        {
            foreach (var dep in node.DependsOn)
            {
                var depIndex = nodes.FindIndex(n => n.Id == dep);
                if (depIndex >= 0 && inDegree[depIndex] > 0 && inNeighbors.ContainsKey(node.Index))
                {
                    inNeighbors[depIndex].Add(node.Index);
                }
            }
        }

        var start = remaining.Min();
        var visited = new Dictionary<int, int>();
        var path = new List<int>();
        var current = start;
        while (!visited.ContainsKey(current))
        {
            visited[current] = path.Count;
            path.Add(current);
            var candidates = inNeighbors[current];
            current = candidates.Count > 0 ? candidates.Min() : start;
        }

        var cycleStart = visited[current];
        var cycleIds = path.Skip(cycleStart).Append(current).Select(i => nodes[i].Id).ToList();
        return cycleIds;
    }
}
