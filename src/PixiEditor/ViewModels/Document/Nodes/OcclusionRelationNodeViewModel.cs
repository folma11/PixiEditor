using System.Collections.Specialized;
using System.ComponentModel;
using PixiEditor.ChangeableDocument.Changeables;
using PixiEditor.ChangeableDocument.Changeables.Graph.Interfaces;
using PixiEditor.ChangeableDocument.Changeables.Graph.Nodes;
using PixiEditor.ChangeableDocument.Changeables.Interfaces;
using PixiEditor.Models.Events;
using PixiEditor.Models.Handlers;
using PixiEditor.ViewModels.Nodes;

namespace PixiEditor.ViewModels.Document.Nodes;

[NodeViewModel("OCCLUSION_RELATION_NODE", "IMAGE", PixiPerfectIcons.Layers)]
internal sealed class OcclusionRelationNodeViewModel : NodeViewModel<OcclusionRelationNode>
{
    private bool isInitialized;
    private NodePropertyViewModel? enabledInput;

    public override void OnInitialized()
    {
        if (isInitialized)
            return;

        isInitialized = true;
        foreach (NodePropertyViewModel input in Inputs.OfType<NodePropertyViewModel>())
            input.ConnectedOutputChanged += Input_ConnectedOutputChanged;

        enabledInput = FindInputProperty<bool>(OcclusionRelationNode.EnabledPropertyName);
        if (enabledInput is not null)
            enabledInput.ValueChanged += EnabledInput_ValueChanged;

        PropertyChanged += Node_PropertyChanged;
        Document.NodeGraph.AllNodes.CollectionChanged += AllNodes_CollectionChanged;
        RefreshConflictIndicators(Document);
    }

    public override void Dispose()
    {
        if (!isInitialized)
            return;

        foreach (NodePropertyViewModel input in Inputs.OfType<NodePropertyViewModel>())
            input.ConnectedOutputChanged -= Input_ConnectedOutputChanged;

        if (enabledInput is not null)
            enabledInput.ValueChanged -= EnabledInput_ValueChanged;
        enabledInput = null;

        PropertyChanged -= Node_PropertyChanged;
        Document.NodeGraph.AllNodes.CollectionChanged -= AllNodes_CollectionChanged;
        isInitialized = false;
    }

    private void Input_ConnectedOutputChanged(object? sender, EventArgs e)
    {
        RefreshConflictIndicators(Document);
    }

    private void EnabledInput_ValueChanged(INodePropertyHandler property, NodePropertyValueChangedArgs args)
    {
        RefreshConflictIndicators(Document);
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PositionBindable))
            RefreshConflictIndicators(Document);
    }

    private void AllNodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshConflictIndicators(Document);
    }

    internal static void RefreshConflictIndicators(DocumentViewModel document)
    {
        IReadOnlyDocument readOnlyDocument = document.AccessInternalReadOnlyDocument();
        List<RelationCandidate> allCandidates = document.NodeGraph.AllNodes
            .OfType<NodeViewModel>()
            .Select(node => CreateCandidate(node))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        List<(RelationCandidate Candidate, OcclusionRelation Relation)> candidates = allCandidates
            .Select(candidate => (Candidate: candidate, Relation: TryGetRelation(candidate, readOnlyDocument)))
            .Where(x => x.Relation.HasValue)
            .Select(x => (x.Candidate, x.Relation!.Value))
            .OrderBy(x => NormalizePriority(x.Candidate.Owner.PositionBindable.X))
            .ThenBy(x => NormalizePriority(x.Candidate.Owner.PositionBindable.Y))
            .ThenBy(x => x.Candidate.Owner.Id)
            .ToList();

        HashSet<RelationCandidate> ignoredCandidates = new();
        List<OcclusionRelation> acceptedRelations = new();

        foreach ((RelationCandidate candidate, OcclusionRelation relation) in candidates)
        {
            if (acceptedRelations.Contains(relation) ||
                HasRelationPath(relation.BackLayerId, relation.FrontLayerId, acceptedRelations,
                    new HashSet<Guid>()))
            {
                ignoredCandidates.Add(candidate);
                continue;
            }

            acceptedRelations.Add(relation);
        }

        foreach (RelationCandidate candidate in allCandidates)
        {
            bool isIgnored = ignoredCandidates.Contains(candidate);
            SetConflictError(candidate.Front, isIgnored);
            SetConflictError(candidate.Back, isIgnored);
        }
    }

    private static RelationCandidate? CreateCandidate(NodeViewModel node)
    {
        if (node is OcclusionRelationNodeViewModel)
        {
            NodePropertyViewModel? front = node.FindInputProperty(OcclusionRelationNode.FrontPropertyName);
            NodePropertyViewModel? back = node.FindInputProperty(OcclusionRelationNode.BackPropertyName);
            NodePropertyViewModel? enabled = node.FindInputProperty(OcclusionRelationNode.EnabledPropertyName);
            return front is null || back is null
                ? null
                : new RelationCandidate(node, front, back, enabled, true);
        }

        NodePropertyViewModel? localBack = node.FindInputProperty(LayerNode.LocalBackPropertyName);
        if (localBack is null)
        {
            return null;
        }

        NodePropertyViewModel? localEnabled =
            node.FindInputProperty(LayerNode.LocalOcclusionEnabledPropertyName);
        return new RelationCandidate(node, null, localBack, localEnabled,
            localBack.ConnectedOutput is not null);
    }

    private static OcclusionRelation? TryGetRelation(RelationCandidate candidate,
        IReadOnlyDocument document)
    {
        if (!candidate.IsConfigured || candidate.Enabled?.Value is false ||
            candidate.Back.ConnectedOutput?.Node.Id is not Guid backId ||
            document.NodeGraph.TryLookupNode(backId) is not IReadOnlyLayerNode)
        {
            return null;
        }

        Guid frontId;
        if (candidate.Front is null)
        {
            frontId = candidate.Owner.Id;
            if (document.NodeGraph.TryLookupNode(frontId) is not IReadOnlyLayerNode)
                return null;
        }
        else if (candidate.Front.ConnectedOutput?.Node.Id is Guid connectedFrontId &&
                 document.NodeGraph.TryLookupNode(connectedFrontId) is IReadOnlyLayerNode)
        {
            frontId = connectedFrontId;
        }
        else
        {
            return null;
        }

        if (frontId == backId)
            return null;

        return new OcclusionRelation(frontId, backId);
    }

    private static void SetConflictError(NodePropertyViewModel? property, bool isIgnored)
    {
        if (property is not null)
            property.Errors = isIgnored ? "OCCLUSION_RELATION_CONFLICT" : null;
    }

    private static bool HasRelationPath(Guid current, Guid target,
        IReadOnlyList<OcclusionRelation> relations, HashSet<Guid> visited)
    {
        if (current == target)
            return true;

        if (!visited.Add(current))
            return false;

        foreach (OcclusionRelation relation in relations)
        {
            if (relation.FrontLayerId != current)
                continue;

            if (HasRelationPath(relation.BackLayerId, target, relations, visited))
                return true;
        }

        return false;
    }

    private static double NormalizePriority(double value) =>
        double.IsFinite(value) ? value : double.MaxValue;

    private sealed record RelationCandidate(
        NodeViewModel Owner,
        NodePropertyViewModel? Front,
        NodePropertyViewModel Back,
        NodePropertyViewModel? Enabled,
        bool IsConfigured);
}
