using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PixiEditor.ViewModels.Dock;

namespace PixiEditor.Views.Dock;

public partial class OcclusionGraphDockView : UserControl
{
    private OcclusionGraphNodeViewModel? draggedNode;
    private Control? draggedControl;
    private Point dragStartPoint;
    private double dragStartX;
    private double dragStartY;

    public OcclusionGraphDockView()
    {
        InitializeComponent();
    }

    private void GraphNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            sender is not Control { DataContext: OcclusionGraphNodeViewModel node } control)
        {
            return;
        }

        draggedNode = node;
        draggedControl = control;
        dragStartPoint = e.GetPosition(GraphCanvas);
        dragStartX = node.X;
        dragStartY = node.Y;
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void GraphNodePointerMoved(object? sender, PointerEventArgs e)
    {
        if (draggedNode is null || draggedControl is null || DataContext is not OcclusionGraphDockViewModel viewModel)
            return;

        Point currentPoint = e.GetPosition(GraphCanvas);
        double zoom = Math.Max(viewModel.GraphZoom, 0.01);
        viewModel.UpdateGraphNodePosition(
            draggedNode.Id,
            dragStartX + (currentPoint.X - dragStartPoint.X) / zoom,
            dragStartY + (currentPoint.Y - dragStartPoint.Y) / zoom);
        e.Handled = true;
    }

    private void GraphNodePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (draggedNode is null)
            return;

        e.Pointer.Capture(null);
        draggedNode = null;
        draggedControl = null;
        e.Handled = true;
    }
}
