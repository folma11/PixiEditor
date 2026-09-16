using PixiEditor.ChangeableDocument.Changeables.Graph.Interfaces;
using PixiEditor.ChangeableDocument.Rendering;

namespace PixiEditor.ChangeableDocument.Changeables.Graph.Nodes;

/// <summary>
/// Describes one local front/back relation in the standard node graph.
///
/// This is intentionally a configuration node. Its inputs reference existing
/// layer outputs, while the document renderer applies the relation after the
/// normal graph has produced the document image. This keeps the relation graph
/// separate from the render dependency graph, so cycles such as A &gt; B &gt; C &gt; A
/// remain valid.
/// </summary>
[NodeInfo(UniqueName)]
public sealed class OcclusionRelationNode : Node
{
    public const string UniqueName = "OcclusionRelation";
    public const string SerializedUniqueName = "PixiEditor." + UniqueName;
    public const string FrontPropertyName = "Front";
    public const string BackPropertyName = "Back";
    public const string EnabledPropertyName = "Enabled";

    public RenderInputProperty Front { get; }
    public RenderInputProperty Back { get; }
    public InputProperty<bool> Enabled { get; }

    public OcclusionRelationNode()
    {
        Front = CreateRenderInput(FrontPropertyName, "FRONT_OF_LAYER", false);
        Back = CreateRenderInput(BackPropertyName, "BACK_OF_LAYER", false);
        Enabled = CreateInput(EnabledPropertyName, "ENABLED", true);
    }

    /// <summary>
    /// Resolves the two directly connected layer nodes into a local relation.
    /// Non-layer connections are rejected instead of being guessed.
    /// </summary>
    public bool TryGetRelation(out OcclusionRelation relation)
    {
        relation = default;

        if (!Enabled.Value ||
            Front.Connection?.Node is not IReadOnlyLayerNode frontLayer ||
            Back.Connection?.Node is not IReadOnlyLayerNode backLayer ||
            frontLayer.Id == backLayer.Id)
        {
            return false;
        }

        relation = new OcclusionRelation(frontLayer.Id, backLayer.Id);
        return true;
    }

    protected override void OnExecute(RenderContext context)
    {
        // This node is a relation declaration, not a render dependency. The
        // renderer reads it directly from the document node graph.
    }

    public override Node CreateCopy() => new OcclusionRelationNode();
}
