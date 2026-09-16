using PixiEditor.ChangeableDocument.Changeables.Graph.Interfaces;
using PixiEditor.ChangeableDocument.ChangeInfos.Occlusion;

namespace PixiEditor.ChangeableDocument.Changes.Occlusion;

internal class SetOcclusionRelation_Change : Change
{
    private readonly bool enabled;
    private readonly Guid frontLayerId;
    private readonly Guid backLayerId;
    private bool originalEnabled;

    [GenerateMakeChangeAction]
    public SetOcclusionRelation_Change(bool enabled, Guid frontLayerId, Guid backLayerId)
    {
        this.enabled = enabled;
        this.frontLayerId = frontLayerId;
        this.backLayerId = backLayerId;
    }

    public override bool InitializeAndValidate(Document target)
    {
        if (frontLayerId == backLayerId)
            return false;

        if (enabled &&
            (!target.TryFindMember<IReadOnlyLayerNode>(frontLayerId, out _) ||
             !target.TryFindMember<IReadOnlyLayerNode>(backLayerId, out _)))
        {
            return false;
        }

        originalEnabled = target.OcclusionGraph.HasRelation(frontLayerId, backLayerId);
        return originalEnabled != enabled;
    }

    public override OneOf<None, IChangeInfo, List<IChangeInfo>> Apply(
        Document target, bool firstApply, out bool ignoreInUndo)
    {
        target.OcclusionGraph.SetRelation(frontLayerId, backLayerId, enabled);
        ignoreInUndo = false;
        return new OcclusionRelation_ChangeInfo(frontLayerId, backLayerId, enabled);
    }

    public override OneOf<None, IChangeInfo, List<IChangeInfo>> Revert(Document target)
    {
        target.OcclusionGraph.SetRelation(frontLayerId, backLayerId, originalEnabled);
        return new OcclusionRelation_ChangeInfo(frontLayerId, backLayerId, originalEnabled);
    }
}
