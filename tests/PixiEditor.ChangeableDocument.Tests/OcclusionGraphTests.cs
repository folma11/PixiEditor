using PixiEditor.ChangeableDocument.Changeables;
using PixiEditor.ChangeableDocument.Changeables.Animations;
using PixiEditor.ChangeableDocument.Changeables.Graph;
using PixiEditor.ChangeableDocument.Changeables.Graph.Interfaces;
using PixiEditor.ChangeableDocument.Changeables.Graph.Nodes;
using PixiEditor.ChangeableDocument.Changes.NodeGraph;
using PixiEditor.ChangeableDocument.ChangeInfos.NodeGraph;
using PixiEditor.ChangeableDocument.ChangeInfos.Structure;
using PixiEditor.ChangeableDocument.Rendering;
using PixiEditor.Tests;
using ChunkyImageLib;
using Drawie.Backend.Core;
using Drawie.Backend.Core.Numerics;
using Drawie.Backend.Core.Surfaces;
using Drawie.Numerics;
using BlendModeEnum = PixiEditor.ChangeableDocument.Enums.BlendMode;

namespace PixiEditor.ChangeableDocument.Tests;

public class OcclusionGraphTests : PixiEditorTest
{
    [Fact]
    public void RelationsCanBeAddedAndRemoved()
    {
        OcclusionGraph graph = new();
        Guid front = Guid.NewGuid();
        Guid back = Guid.NewGuid();

        Assert.True(graph.SetRelation(front, back, true));
        Assert.True(graph.HasRelation(front, back));
        Assert.False(graph.SetRelation(front, back, true));

        Assert.True(graph.SetRelation(front, back, false));
        Assert.False(graph.HasRelation(front, back));
    }

    [Fact]
    public void ReciprocalRelationsAreReportedAsConflicts()
    {
        OcclusionGraph graph = new();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        graph.SetRelation(first, second, true);
        graph.SetRelation(second, first, true);

        Assert.Equal(1, graph.ReciprocalConflictCount);
        Assert.True(graph.IsReciprocalConflict(new OcclusionRelation(first, second)));
    }

    [Fact]
    public void CyclesAreAllowedAndClonesAreIndependent()
    {
        OcclusionGraph graph = new();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid third = Guid.NewGuid();

        graph.SetRelation(first, second, true);
        graph.SetRelation(second, third, true);
        graph.SetRelation(third, first, true);
        graph.SetEnabled(true);

        OcclusionGraph clone = graph.Clone();

        Assert.Equal(3, clone.Relations.Count);
        Assert.True(clone.IsEnabled);
        clone.SetRelation(first, second, false);
        Assert.True(graph.HasRelation(first, second));
    }

    [Fact]
    public void SelfRelationsAreIgnored()
    {
        OcclusionGraph graph = new();
        Guid layer = Guid.NewGuid();

        Assert.False(graph.SetRelation(layer, layer, true));
        Assert.Empty(graph.Relations);
    }

    [Fact]
    public void RelationsPreserveInsertionPriority()
    {
        OcclusionGraph graph = new();
        OcclusionRelation first = new(Guid.NewGuid(), Guid.NewGuid());
        OcclusionRelation second = new(Guid.NewGuid(), Guid.NewGuid());

        graph.SetRelation(first.FrontLayerId, first.BackLayerId, true);
        graph.SetRelation(second.FrontLayerId, second.BackLayerId, true);

        Assert.Equal(first, graph.Relations[0]);
        Assert.Equal(second, graph.Relations[1]);
    }

    [Fact]
    public void RelationNodeReadsConnectedLayerOutputs()
    {
        TestLayerNode frontLayer = new();
        TestLayerNode backLayer = new();
        OcclusionRelationNode relationNode = new();

        frontLayer.Output.ConnectTo(relationNode.Front);
        backLayer.Output.ConnectTo(relationNode.Back);

        Assert.True(relationNode.TryGetRelation(out OcclusionRelation relation));
        Assert.Equal(frontLayer.Id, relation.FrontLayerId);
        Assert.Equal(backLayer.Id, relation.BackLayerId);

        CreateNode_ChangeInfo serialized = CreateNode_ChangeInfo.CreateFromNode(relationNode);
        Assert.Equal(OcclusionRelationNode.SerializedUniqueName, serialized.InternalName);
        Assert.Contains(serialized.Inputs, x => x.PropertyName == OcclusionRelationNode.FrontPropertyName);
        Assert.Contains(serialized.Inputs, x => x.PropertyName == OcclusionRelationNode.BackPropertyName);
        Assert.True(NodeOperations.TryGetNodeType(OcclusionRelationNode.SerializedUniqueName, out Type? registeredType));
        Assert.Equal(typeof(OcclusionRelationNode), registeredType);

        relationNode.Enabled.NonOverridenValue = false;
        Assert.False(relationNode.TryGetRelation(out _));

        relationNode.Dispose();
        frontLayer.Dispose();
        backLayer.Dispose();
    }

    [Fact]
    public void RelationNodeRejectsSelfRelation()
    {
        TestLayerNode layer = new();
        OcclusionRelationNode relationNode = new();

        layer.Output.ConnectTo(relationNode.Front);
        layer.Output.ConnectTo(relationNode.Back);

        Assert.False(relationNode.TryGetRelation(out _));

        relationNode.Dispose();
        layer.Dispose();
    }

    [Fact]
    public void LayerNodeUsesLocalBackRelationWithoutEnteringRenderQueue()
    {
        VectorLayerNode backLayer = new();
        VectorLayerNode ownerLayer = new();
        OutputNode outputNode = new();
        NodeGraph graph = new();

        backLayer.Output.ConnectTo(ownerLayer.LocalBack);
        ownerLayer.Output.ConnectTo(outputNode.Input);

        Assert.True(ownerLayer.TryGetLocalOcclusionRelation(out OcclusionRelation relation));
        Assert.Equal(ownerLayer.Id, relation.FrontLayerId);
        Assert.Equal(backLayer.Id, relation.BackLayerId);
        Assert.DoesNotContain(ownerLayer.InputProperties,
            x => x.InternalPropertyName == "LocalFront");
        Assert.False(ownerLayer.LocalBack.IsRenderDependency);

        CreateLayer_ChangeInfo serialized = CreateLayer_ChangeInfo.FromLayer(ownerLayer);
        Assert.Contains(
            serialized.InputProperties,
            x => x.PropertyName == LayerNode.LocalBackPropertyName && !x.IsRenderDependency);

        graph.AddNode(backLayer);
        graph.AddNode(ownerLayer);
        graph.AddNode(outputNode);

        IReadOnlyList<IReadOnlyNode> executionQueue = graph.CalculateExecutionQueue(outputNode).ToArray();
        Assert.Contains(ownerLayer, executionQueue);
        Assert.Contains(outputNode, executionQueue);
        Assert.DoesNotContain(backLayer, executionQueue);

        ownerLayer.LocalOcclusionEnabled.NonOverridenValue = false;
        Assert.False(ownerLayer.TryGetLocalOcclusionRelation(out _));

        graph.Dispose();
    }

    private sealed class TestLayerNode : Node, IReadOnlyLayerNode
    {
        public InputProperty<float> Opacity { get; }
        public InputProperty<bool> IsVisible { get; }
        public bool ClipToPreviousMember { get; }
        public InputProperty<BlendModeEnum> BlendMode { get; }
        public RenderInputProperty CustomMask { get; }
        public InputProperty<bool> MaskIsVisible { get; }
        public string MemberName { get; set; } = "Test Layer";
        public ChunkyImage? EmbeddedMask => null;
        public OutputProperty<Painter> Output { get; }

        public TestLayerNode()
        {
            Output = CreateOutput<Painter>("Output", "OUTPUT", null!);
            Opacity = CreateInput("Opacity", "OPACITY", 1f);
            IsVisible = CreateInput("IsVisible", "IS_VISIBLE", true);
            BlendMode = CreateInput("BlendMode", "BLEND_MODE", BlendModeEnum.Normal);
            CustomMask = CreateRenderInput("Mask", "MASK");
            MaskIsVisible = CreateInput("MaskIsVisible", "MASK_IS_VISIBLE", true);
        }

        protected override void OnExecute(RenderContext context)
        {
        }

        public override Node CreateCopy() => new TestLayerNode();

        public VecD GetScenePosition(KeyFrameTime atTime) => VecD.Zero;
        public VecD GetSceneSize(KeyFrameTime atTime) => new VecD(1, 1);
        public RectD? GetTightBounds(KeyFrameTime frameTime) => new RectD(0, 0, 1, 1);
        public ShapeCorners GetTransformationCorners(KeyFrameTime frameTime) => new(new RectD(0, 0, 1, 1));
        public void Render(SceneObjectRenderContext context)
        {
        }

        public void RenderForOutput(RenderContext context, Canvas renderTarget, RenderOutputProperty output)
        {
        }
    }
}
