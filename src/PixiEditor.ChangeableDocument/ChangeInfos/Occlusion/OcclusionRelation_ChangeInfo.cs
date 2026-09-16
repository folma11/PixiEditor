namespace PixiEditor.ChangeableDocument.ChangeInfos.Occlusion;

public record class OcclusionRelation_ChangeInfo(Guid FrontLayerId, Guid BackLayerId, bool Enabled) : IChangeInfo;
