using PixiEditor.ChangeableDocument.Changeables.Graph.Nodes;

namespace PixiEditor.ChangeableDocument.Changeables.Graph;

public class RenderInputProperty : InputProperty<Painter?>
{
    public override bool IsRenderDependency { get; }

    internal RenderInputProperty(Node node, string internalName, string displayName, Painter? defaultValue,
        bool isRenderDependency = true) : base(node, internalName, displayName, defaultValue)
    {
        IsRenderDependency = isRenderDependency;
    }
}
