namespace Kushat.Core;

public sealed record MenuGroup(string Id, string Name, string? ParentId = null);
public enum MenuBranchKind { All, Group, Unassigned }

public sealed class MenuBranch
{
    internal List<MenuBranch> ChildList { get; } = [];
    public IReadOnlyList<MenuBranch> Children => ChildList;
    public string? GroupId { get; }
    public string Name { get; }
    public MenuBranchKind Kind { get; }
    public int ProductCount { get; internal set; }
    public string Title => $"{Name} ({ProductCount})";
    public string Path
    {
        get
        {
            var names = new List<string>();
            for (MenuBranch? current = this; current is not null && current.Kind != MenuBranchKind.All; current = current.Parent)
                names.Add(current.Name);
            names.Reverse(); return names.Count == 0 ? Name : string.Join(" / ", names);
        }
    }
    internal MenuBranch? Parent { get; set; }
    internal MenuBranch(string? groupId, string name, MenuBranchKind kind) { GroupId = groupId; Name = name; Kind = kind; }
}

// Filtering never owns or replaces ProductRow objects, so selections and quantities survive branch/search changes.
public sealed class MenuBrowser
{
    private readonly Dictionary<string, MenuBranch> groups;
    private readonly MenuBranch unassigned = new(null, "Без доступной группы", MenuBranchKind.Unassigned);
    private HashSet<string> selectedIds = [];
    public MenuBranch Root { get; } = new(null, "Все товары", MenuBranchKind.All);
    public MenuBranch SelectedBranch { get; private set; }
    public bool HasIncompleteHierarchy { get; }

    public MenuBrowser(IEnumerable<MenuGroup> menuGroups, IReadOnlyList<Product> products)
    {
        var source = menuGroups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        groups = source.ToDictionary(g => g.Key, g => new MenuBranch(g.Key, g.Value.Name, MenuBranchKind.Group), StringComparer.Ordinal);
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        var incomplete = false;
        foreach (var group in source.Values)
        {
            var parent = string.IsNullOrWhiteSpace(group.ParentId) || group.ParentId == "0" ? null : group.ParentId;
            if (parent is not null && (parent == group.Id || !groups.ContainsKey(parent))) { parent = null; incomplete = true; }
            parents[group.Id] = parent;
        }
        // Break cycles deterministically before exposing the tree to WPF; use iteration for deep menus.
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in groups.Keys.Order(StringComparer.Ordinal))
        {
            if (done.Contains(id)) continue;
            var path = new HashSet<string>(StringComparer.Ordinal);
            for (string? current = id; current is not null && !done.Contains(current); current = parents[current])
            {
                if (path.Add(current)) continue;
                parents[current] = null; incomplete = true; break;
            }
            done.UnionWith(path);
        }
        foreach (var group in groups.Values)
        {
            var parent = parents[group.GroupId!] is { } parentId ? groups[parentId] : Root;
            group.Parent = parent; parent.ChildList.Add(group);
        }
        foreach (var product in products)
        {
            if (product.MenuGroupId is { } groupId && groups.TryGetValue(groupId, out var branch)) branch.ProductCount++;
            else unassigned.ProductCount++;
        }
        if (unassigned.ProductCount != 0) { unassigned.Parent = Root; Root.ChildList.Add(unassigned); }
        var stack = new Stack<(MenuBranch Branch, bool Visited)>(); stack.Push((Root, false));
        while (stack.TryPop(out var entry))
        {
            if (entry.Visited)
            { entry.Branch.ProductCount += entry.Branch.ChildList.Sum(b => b.ProductCount); continue; }
            entry.Branch.ChildList.Sort((a, b) =>
            {
                var order = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
                return order != 0 ? order : StringComparer.Ordinal.Compare(a.GroupId, b.GroupId);
            });
            stack.Push((entry.Branch, true));
            foreach (var child in entry.Branch.ChildList) stack.Push((child, false));
        }
        HasIncompleteHierarchy = incomplete; SelectedBranch = Root;
    }

    public MenuBranch? FindGroup(string id) => groups.GetValueOrDefault(id);

    public void Select(MenuBranch branch)
    {
        if (!ReferenceEquals(branch, Root) && !ReferenceEquals(branch, unassigned) &&
            (branch.GroupId is null || !groups.TryGetValue(branch.GroupId, out var own) || !ReferenceEquals(own, branch)))
            throw new ArgumentException("Ветка не относится к текущему меню.");
        SelectedBranch = branch; selectedIds = new(StringComparer.Ordinal);
        if (branch.Kind != MenuBranchKind.Group) return;
        var pending = new Stack<MenuBranch>(); pending.Push(branch);
        while (pending.TryPop(out var node))
        {
            if (node.GroupId is { } id) selectedIds.Add(id);
            foreach (var child in node.Children) pending.Push(child);
        }
    }

    public bool Matches(Product product, string? search)
    {
        var inBranch = SelectedBranch.Kind switch
        {
            MenuBranchKind.All => true,
            MenuBranchKind.Unassigned => product.MenuGroupId is null || !groups.ContainsKey(product.MenuGroupId),
            _ => product.MenuGroupId is { } id && selectedIds.Contains(id)
        };
        var query = search?.Trim() ?? "";
        return inBranch && (product.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || product.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
