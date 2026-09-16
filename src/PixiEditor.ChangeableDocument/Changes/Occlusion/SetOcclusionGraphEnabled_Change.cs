using PixiEditor.ChangeableDocument.ChangeInfos.Occlusion;

namespace PixiEditor.ChangeableDocument.Changes.Occlusion;

internal class SetOcclusionGraphEnabled_Change : Change
{
    private readonly bool enabled;
    private bool originalEnabled;

    [GenerateMakeChangeAction]
    public SetOcclusionGraphEnabled_Change(bool enabled)
    {
        this.enabled = enabled;
    }

    public override bool InitializeAndValidate(Document target)
    {
        originalEnabled = target.OcclusionGraph.IsEnabled;
        return originalEnabled != enabled;
    }

    public override OneOf<None, IChangeInfo, List<IChangeInfo>> Apply(
        Document target, bool firstApply, out bool ignoreInUndo)
    {
        target.OcclusionGraph.SetEnabled(enabled);
        ignoreInUndo = false;
        return new OcclusionGraphEnabled_ChangeInfo(enabled);
    }

    public override OneOf<None, IChangeInfo, List<IChangeInfo>> Revert(Document target)
    {
        target.OcclusionGraph.SetEnabled(originalEnabled);
        return new OcclusionGraphEnabled_ChangeInfo(originalEnabled);
    }
}
