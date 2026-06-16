using PiSharp.Abstractions.Sessions;

namespace PiSharp.Tui.Interactive.SessionSelection;

public enum SessionSelectorScope
{
    Current,
    All
}

public sealed record SessionSelectorRow(
    JsonlSessionMetadata Session,
    string TreePrefix,
    string DisplayText,
    string RightText,
    int Depth,
    bool IsLast,
    IReadOnlyList<bool> AncestorContinues);

public static class SessionSelectorModel
{
    public static IReadOnlyList<JsonlSessionMetadata> FilterScope(
        IReadOnlyList<JsonlSessionMetadata> sessions,
        string currentCwd,
        SessionSelectorScope scope)
        => scope == SessionSelectorScope.All
            ? sessions
            : sessions.Where(session => string.Equals(NormalizePath(session.Cwd), NormalizePath(currentCwd), StringComparison.OrdinalIgnoreCase)).ToArray();

    public static IReadOnlyList<SessionSelectorRow> BuildRows(
        IReadOnlyList<JsonlSessionMetadata> sessions,
        string query = "",
        bool showCwd = false,
        SessionSelectorSortMode sortMode = SessionSelectorSortMode.Threaded,
        SessionSelectorNameFilter nameFilter = SessionSelectorNameFilter.All,
        DateTimeOffset now = default)
    {
        now = now == default ? DateTimeOffset.UtcNow : now;
        var trimmed = query.Trim();
        var nameFiltered = nameFilter == SessionSelectorNameFilter.All
            ? sessions
            : sessions.Where(session => !string.IsNullOrWhiteSpace(session.Name)).ToArray();

        if (sortMode == SessionSelectorSortMode.Threaded && trimmed.Length == 0)
        {
            return Flatten(BuildTree(nameFiltered))
                .Select(node => ToRow(node, showCwd, now))
                .ToArray();
        }

        return SessionSelectorSearch.FilterAndSort(nameFiltered, query, sortMode, SessionSelectorNameFilter.All)
            .Select(session => ToRow(new FlatSessionNode(session, 0, true, []), showCwd, now))
            .ToArray();
    }

    private static IReadOnlyList<SessionTreeNode> BuildTree(IEnumerable<JsonlSessionMetadata> sessions)
    {
        var nodes = sessions
            .Select(session => new SessionTreeNode(session))
            .ToDictionary(node => NormalizePath(node.Session.Path), StringComparer.OrdinalIgnoreCase);
        var roots = new List<SessionTreeNode>();

        foreach (var node in nodes.Values)
        {
            var parentPath = node.Session.ParentSessionPath is null ? null : NormalizePath(node.Session.ParentSessionPath);
            if (parentPath is not null && nodes.TryGetValue(parentPath, out var parent)) parent.Children.Add(node);
            else roots.Add(node);
        }

        SortNodes(roots);
        return roots;
    }

    private static void SortNodes(List<SessionTreeNode> nodes)
    {
        nodes.Sort((left, right) => right.Session.ModifiedAt.CompareTo(left.Session.ModifiedAt));
        foreach (var node in nodes) SortNodes(node.Children);
    }

    private static IReadOnlyList<FlatSessionNode> Flatten(IReadOnlyList<SessionTreeNode> roots)
    {
        var rows = new List<FlatSessionNode>();
        for (var i = 0; i < roots.Count; i++) Walk(roots[i], 0, [], i == roots.Count - 1, rows);
        return rows;
    }

    private static void Walk(SessionTreeNode node, int depth, IReadOnlyList<bool> ancestorContinues, bool isLast, List<FlatSessionNode> rows)
    {
        rows.Add(new FlatSessionNode(node.Session, depth, isLast, ancestorContinues));
        for (var i = 0; i < node.Children.Count; i++)
        {
            var childIsLast = i == node.Children.Count - 1;
            var continues = depth > 0 && !isLast;
            var nextAncestors = depth > 0 ? [.. ancestorContinues, continues] : ancestorContinues;
            Walk(node.Children[i], depth + 1, nextAncestors, childIsLast, rows);
        }
    }

    private static SessionSelectorRow ToRow(FlatSessionNode node, bool showCwd, DateTimeOffset now)
    {
        var rightText = $"{node.Session.MessageCount} {FormatAge(node.Session.ModifiedAt, now)}";
        if (showCwd && !string.IsNullOrWhiteSpace(node.Session.Cwd)) rightText = $"{ShortenPath(node.Session.Cwd)} {rightText}";
        return new SessionSelectorRow(
            node.Session,
            TreePrefix(node),
            NormalizeDisplayText(string.IsNullOrWhiteSpace(node.Session.Name) ? node.Session.FirstMessage : node.Session.Name!),
            rightText,
            node.Depth,
            node.IsLast,
            node.AncestorContinues);
    }

    private static string TreePrefix(FlatSessionNode node)
    {
        if (node.Depth == 0) return string.Empty;
        var ancestors = string.Concat(node.AncestorContinues.Select(continues => continues ? "│  " : "   "));
        return ancestors + (node.IsLast ? "└─ " : "├─ ");
    }

    private static string NormalizeDisplayText(string text)
        => new(text.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());

    private static string FormatAge(DateTimeOffset modifiedAt, DateTimeOffset now)
    {
        var diff = now - modifiedAt;
        if (diff.TotalMinutes < 1) return "now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)}w";
        if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)}mo";
        return $"{(int)(diff.TotalDays / 365)}y";
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        var normalized = path.Replace('\\', '/');
        return home.Length > 0 && normalized.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? $"~{normalized[home.Length..]}"
            : path;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimEnd('/');

    private sealed class SessionTreeNode(JsonlSessionMetadata session)
    {
        public JsonlSessionMetadata Session { get; } = session;
        public List<SessionTreeNode> Children { get; } = [];
    }

    private sealed record FlatSessionNode(JsonlSessionMetadata Session, int Depth, bool IsLast, IReadOnlyList<bool> AncestorContinues);
}
