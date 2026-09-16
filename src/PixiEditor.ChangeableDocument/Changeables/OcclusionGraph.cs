namespace PixiEditor.ChangeableDocument.Changeables;

/// <summary>
/// A local front/back relationship between two drawable layers.
///
/// The graph intentionally allows cycles. A cycle is a useful state while
/// experimenting with the feature; when a later relation creates a cycle at
/// a shared visible pixel, the renderer discards that later relation.
/// Unsupported rendering cases still fall back to the normal stack.
/// </summary>
public readonly record struct OcclusionRelation(Guid FrontLayerId, Guid BackLayerId);

public sealed class OcclusionGraph
{
    private readonly List<OcclusionRelation> relations = new();

    public bool IsEnabled { get; private set; }

    public IReadOnlyList<OcclusionRelation> Relations => relations;

    public event EventHandler? Changed;

    public int ReciprocalConflictCount =>
        relations.Count(relation => HasRelation(relation.BackLayerId, relation.FrontLayerId)) / 2;

    public bool HasRelation(Guid frontLayerId, Guid backLayerId)
    {
        return relations.Contains(new OcclusionRelation(frontLayerId, backLayerId));
    }

    public bool IsReciprocalConflict(OcclusionRelation relation)
    {
        return HasRelation(relation.FrontLayerId, relation.BackLayerId) &&
               HasRelation(relation.BackLayerId, relation.FrontLayerId);
    }

    public bool SetRelation(Guid frontLayerId, Guid backLayerId, bool enabled)
    {
        if (frontLayerId == backLayerId)
            return false;

        OcclusionRelation relation = new(frontLayerId, backLayerId);
        bool changed = enabled ? !relations.Contains(relation) : relations.Remove(relation);
        if (!changed)
            return false;

        if (enabled)
            relations.Add(relation);

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool SetEnabled(bool enabled)
    {
        if (IsEnabled == enabled)
            return false;

        IsEnabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public int RemoveRelationsForLayer(Guid layerId)
    {
        int removed = relations.RemoveAll(x => x.FrontLayerId == layerId || x.BackLayerId == layerId);
        if (removed > 0)
            Changed?.Invoke(this, EventArgs.Empty);

        return removed;
    }

    public OcclusionGraph Clone()
    {
        OcclusionGraph clone = new() { IsEnabled = IsEnabled };
        clone.relations.AddRange(relations);
        return clone;
    }
}
