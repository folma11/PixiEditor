using System.Runtime.InteropServices;
using PixiEditor.ChangeableDocument.Changeables;
using PixiEditor.ChangeableDocument.Changeables.Animations;
using PixiEditor.ChangeableDocument.Changeables.Graph.Interfaces;
using PixiEditor.ChangeableDocument.Changeables.Graph.Nodes;
using PixiEditor.ChangeableDocument.Changeables.Interfaces;
using Drawie.Backend.Core;
using Drawie.Backend.Core.Numerics;
using Drawie.Backend.Core.Surfaces;
using Drawie.Backend.Core.Surfaces.PaintImpl;
using Drawie.Numerics;

namespace PixiEditor.ChangeableDocument.Rendering;

/// <summary>
/// Applies local occlusion relations to an already rendered document image.
/// The normal node graph remains the source of truth; this compositor only
/// swaps the two isolated layer contributions where a relation contradicts the
/// stack order. Standard relation declarations (standalone
/// OcclusionRelationNodes or layer-owned local relations) are ordered by
/// their X position, from left to right. The legacy OcclusionGraph is
/// retained as a fallback and keeps its insertion order.
/// </summary>
public static class OcclusionGraphRenderer
{
    public static void Apply(Texture output, IReadOnlyDocument document, Matrix3X3 renderMatrix, VecI renderSize,
        KeyFrameTime frameTime, RenderContext baseContext)
    {
        IReadOnlyStructureNode[] orderedMembers = document.GetStructureTreeInOrder();
        Dictionary<Guid, (IReadOnlyLayerNode Layer, int Order)> layers = orderedMembers
            .Select((member, index) => (member, index))
            .Where(x => x.member is IReadOnlyLayerNode)
            .ToDictionary(x => x.member.Id,
                x => ((IReadOnlyLayerNode)x.member, x.index));

        List<OcclusionRelation> relations = GetRelations(document)
            .Where(x => layers.ContainsKey(x.FrontLayerId) && layers.ContainsKey(x.BackLayerId))
            .ToList();
        if (relations.Count == 0)
            return;

        Dictionary<Guid, Texture> isolatedLayers = new();
        Surface? correctedOutput = null;
        Pixmap? correctedOutputPixmap = null;
        Span<Half> correctedOutputPixels = default;
        int outputSave = output.DrawingSurface.Canvas.Save();

        try
        {
            output.DrawingSurface.Canvas.SetMatrix(Matrix3X3.Identity);

            HashSet<int> ignoredRelationIndexes;
            try
            {
                ignoredRelationIndexes = FindIgnoredRelations(relations, layers, frameTime, document,
                    isolatedLayers, renderMatrix, renderSize, baseContext);
            }
            catch (Exception)
            {
                // Bounds or pixel inspection are optional for some procedural
                // layers. If conflict inspection cannot evaluate them, retain
                // the normal stack as the safe fallback.
                return;
            }

            for (int relationIndex = 0; relationIndex < relations.Count; relationIndex++)
            {
                OcclusionRelation relation = relations[relationIndex];
                // Once a relation creates a same-point conflict, the relation
                // itself is discarded. It must not affect any other overlap.
                if (ignoredRelationIndexes.Contains(relationIndex))
                    continue;

                // The structure traversal is bottom-to-top. A relation that
                // already agrees with that order needs no correction.
                if (layers[relation.FrontLayerId].Order > layers[relation.BackLayerId].Order)
                {
                    continue;
                }

                try
                {
                    if (!TryGetCommonRenderBounds(
                            new[] { relation.FrontLayerId, relation.BackLayerId }, layers, frameTime,
                            renderMatrix, renderSize, out RectI overlapBounds))
                    {
                        continue;
                    }

                    Texture front = GetOrRenderIsolatedLayer(
                        relation.FrontLayerId, document, layers, isolatedLayers,
                        renderMatrix, renderSize, baseContext);
                    Texture back = GetOrRenderIsolatedLayer(
                        relation.BackLayerId, document, layers, isolatedLayers,
                        renderMatrix, renderSize, baseContext);

                    if (correctedOutput == null)
                    {
                        correctedOutput = Surface.ForProcessing(renderSize, document.ProcessingColorSpace);
                        using Paint copyPaint = new() { BlendMode = BlendMode.Src };
                        correctedOutput.DrawingSurface.Canvas.DrawSurface(output.DrawingSurface, 0, 0, copyPaint);
                        correctedOutputPixmap = correctedOutput.PeekPixels();
                        correctedOutputPixels = MemoryMarshal.Cast<ulong, Half>(
                            correctedOutputPixmap.GetPixelSpan<ulong>());
                    }

                    ApplyLocalRelationSwap(correctedOutputPixels, front, back, overlapBounds);
                }
                catch (Exception)
                {
                    // Unsupported layer/effect: keep the normal stack as the
                    // safe fallback for this relation.
                    return;
                }
            }

            if (correctedOutput != null)
            {
                correctedOutput.DrawingSurface.Canvas.Flush();
                using Paint outputPaint = new() { BlendMode = BlendMode.Src };
                output.DrawingSurface.Canvas.DrawSurface(correctedOutput.DrawingSurface, 0, 0, outputPaint);
            }
        }
        finally
        {
            output.DrawingSurface.Canvas.RestoreToCount(outputSave);
            correctedOutputPixmap?.Dispose();
            correctedOutput?.Dispose();
            foreach (Texture texture in isolatedLayers.Values)
                texture.Dispose();
        }
    }

    private static List<OcclusionRelation> GetRelations(IReadOnlyDocument document)
    {
        LayerNode[] localRelationLayers = document.NodeGraph.AllNodes
            .OfType<LayerNode>()
            .Where(x => x.HasLocalOcclusionConfiguration)
            .ToArray();

        List<OcclusionRelationNode> relationNodes = document.NodeGraph.AllNodes
            .OfType<OcclusionRelationNode>()
            .ToList();

        // A configured layer or a standard relation node switches the
        // document to node-based configuration. Unconnected/disabled entries
        // therefore intentionally mean "no local correction", rather than
        // falling back to a second, independent configuration store.
        if (relationNodes.Count > 0 || localRelationLayers.Length > 0)
        {
            List<(OcclusionRelation Relation, VecD Position, Guid OwnerId)> candidates =
                localRelationLayers
                    .Select(x =>
                    {
                        bool isValid = x.TryGetLocalOcclusionRelation(out OcclusionRelation relation);
                        return (Relation: relation, IsValid: isValid, Position: x.Position, OwnerId: x.Id);
                    })
                    .Where(x => x.IsValid)
                    .Select(x => (x.Relation, x.Position, x.OwnerId))
                    .ToList();

            candidates.AddRange(relationNodes
                .Select(x =>
                {
                    bool isValid = x.TryGetRelation(out OcclusionRelation relation);
                    return (Relation: relation, IsValid: isValid, Position: x.Position, OwnerId: x.Id);
                })
                .Where(x => x.IsValid)
                .Select(x => (x.Relation, x.Position, x.OwnerId)));

            return candidates
                .OrderBy(x => NormalizePriority(x.Position.X))
                .ThenBy(x => NormalizePriority(x.Position.Y))
                .ThenBy(x => x.OwnerId)
                .Select(x => x.Relation)
                .ToList();
        }

        OcclusionGraph occlusionGraph = document.OcclusionGraph;
        return occlusionGraph.IsEnabled
            ? occlusionGraph.Relations.ToList()
            : new List<OcclusionRelation>();
    }

    private static double NormalizePriority(double value) =>
        double.IsFinite(value) ? value : double.MaxValue;

    private static void ApplyLocalRelationSwap(Span<Half> outputPixels, Texture front, Texture back,
        RectI overlapBounds)
    {
        using Pixmap frontPixmap = front.PeekPixels();
        using Pixmap backPixmap = back.PeekPixels();
        Span<Half> frontPixels = MemoryMarshal.Cast<ulong, Half>(frontPixmap.GetPixelSpan<ulong>());
        Span<Half> backPixels = MemoryMarshal.Cast<ulong, Half>(backPixmap.GetPixelSpan<ulong>());

        int requiredPixels = checked(frontPixmap.Width * frontPixmap.Height * 4);
        if (frontPixmap.Width != backPixmap.Width || frontPixmap.Height != backPixmap.Height ||
            frontPixels.Length < requiredPixels || backPixels.Length < requiredPixels ||
            outputPixels.Length < requiredPixels || overlapBounds.Right > frontPixmap.Width ||
            overlapBounds.Bottom > frontPixmap.Height)
        {
            throw new InvalidOperationException("Occlusion texture pixels do not match the render target.");
        }

        int rowStride = checked(frontPixmap.Width * 4);
        for (int y = overlapBounds.Top; y < overlapBounds.Bottom; y++)
        {
            int pixelIndex = checked(y * rowStride + overlapBounds.Left * 4);
            for (int x = overlapBounds.Left; x < overlapBounds.Right; x++, pixelIndex += 4)
            {
                float frontAlpha = (float)frontPixels[pixelIndex + 3];
                float backAlpha = (float)backPixels[pixelIndex + 3];
                if (frontAlpha <= 0 || backAlpha <= 0)
                {
                    continue;
                }

                // The normal stack currently has back over front. For premultiplied
                // colors, swapping those two layers changes the result by this
                // amount while leaving the resulting alpha unchanged. Applying the
                // delta avoids adding a second antialiased outline over the output.
                for (int channel = 0; channel < 3; channel++)
                {
                    float corrected = (float)outputPixels[pixelIndex + channel]
                                      + (float)frontPixels[pixelIndex + channel] * backAlpha
                                      - (float)backPixels[pixelIndex + channel] * frontAlpha;
                    outputPixels[pixelIndex + channel] = (Half)MathF.Max(0, corrected);
                }
            }
        }
    }

    private static Texture GetOrRenderIsolatedLayer(Guid layerId, IReadOnlyDocument document,
        IReadOnlyDictionary<Guid, (IReadOnlyLayerNode Layer, int Order)> layers,
        Dictionary<Guid, Texture> isolatedLayers, Matrix3X3 renderMatrix, VecI renderSize,
        RenderContext baseContext)
    {
        if (isolatedLayers.TryGetValue(layerId, out Texture? existing))
            return existing;

        Texture texture = Texture.ForProcessing(renderSize, document.ProcessingColorSpace);
        try
        {
            int save = texture.DrawingSurface.Canvas.Save();
            texture.DrawingSurface.Canvas.SetMatrix(renderMatrix);
            texture.DrawingSurface.Canvas.Clear();

            RenderContext isolatedContext = baseContext.Clone();
            isolatedContext.RenderSurface = texture.DrawingSurface.Canvas;
            isolatedContext.TargetOutput = null;
            isolatedContext.PreviewTextures = null;
            isolatedContext.FullRerender = true;
            isolatedContext.IterativeRender = false;
            isolatedContext.State = new Dictionary<string, object>();

            layers[layerId].Layer.RenderForOutput(isolatedContext, texture.DrawingSurface.Canvas, null!);
            texture.DrawingSurface.Canvas.RestoreToCount(save);

            isolatedLayers.Add(layerId, texture);
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private static HashSet<int> FindIgnoredRelations(
        IReadOnlyList<OcclusionRelation> relations,
        IReadOnlyDictionary<Guid, (IReadOnlyLayerNode Layer, int Order)> layers,
        KeyFrameTime frameTime,
        IReadOnlyDocument document,
        Dictionary<Guid, Texture> isolatedLayers,
        Matrix3X3 renderMatrix,
        VecI renderSize,
        RenderContext baseContext)
    {
        HashSet<int> ignoredRelationIndexes = new();
        List<OcclusionRelation> acceptedRelations = new();

        for (int relationIndex = 0; relationIndex < relations.Count; relationIndex++)
        {
            OcclusionRelation relation = relations[relationIndex];
            if (acceptedRelations.Contains(relation))
            {
                // A duplicate relation would apply the same pixel delta twice.
                // Keep the leftmost instance and ignore the later one.
                ignoredRelationIndexes.Add(relationIndex);
                continue;
            }

            if (!TryFindRelationPath(relation.BackLayerId, relation.FrontLayerId, acceptedRelations, relation,
                    new HashSet<Guid> { relation.BackLayerId }, out List<OcclusionRelation> path) ||
                path.Count < 1)
            {
                acceptedRelations.Add(relation);
                continue;
            }

            List<OcclusionRelation> cycleRelations = new(path.Count + 1) { relation };
            cycleRelations.AddRange(path);

            if (!HaveCommonVisiblePixels(
                    cycleRelations.SelectMany(x => new[] { x.FrontLayerId, x.BackLayerId }).Distinct(),
                    layers, frameTime, document, isolatedLayers, renderMatrix, renderSize, baseContext))
            {
                // A graph cycle without a common visible pixel is not a
                // rendering conflict, so this relation remains available for
                // later relations and for pair-only intersections.
                acceptedRelations.Add(relation);
                continue;
            }

            // The already accepted path is kept. This newly processed relation
            // is the one that causes the conflict and is discarded entirely.
            ignoredRelationIndexes.Add(relationIndex);
        }

        return ignoredRelationIndexes;
    }

    private static bool TryFindRelationPath(Guid current, Guid target,
        IReadOnlyList<OcclusionRelation> relations, OcclusionRelation ignored,
        HashSet<Guid> visited, out List<OcclusionRelation> path)
    {
        if (current == target)
        {
            path = new List<OcclusionRelation>();
            return true;
        }

        foreach (OcclusionRelation relation in relations)
        {
            if (relation == ignored || relation.FrontLayerId != current || !visited.Add(relation.BackLayerId))
                continue;

            if (TryFindRelationPath(relation.BackLayerId, target, relations, ignored, visited, out path))
            {
                path.Insert(0, relation);
                return true;
            }

            visited.Remove(relation.BackLayerId);
        }

        path = new List<OcclusionRelation>();
        return false;
    }

    private static bool HaveCommonVisiblePixels(IEnumerable<Guid> layerIds,
        IReadOnlyDictionary<Guid, (IReadOnlyLayerNode Layer, int Order)> layers,
        KeyFrameTime frameTime,
        IReadOnlyDocument document,
        Dictionary<Guid, Texture> isolatedLayers,
        Matrix3X3 renderMatrix,
        VecI renderSize,
        RenderContext baseContext)
    {
        if (!TryGetCommonRenderBounds(layerIds, layers, frameTime, renderMatrix, renderSize,
                out RectI scanBounds))
        {
            return false;
        }

        int scanWidth = scanBounds.Width;
        int scanHeight = scanBounds.Height;
        byte[] commonVisible = new byte[checked(scanWidth * scanHeight)];
        commonVisible.AsSpan().Fill(1);

        foreach (Guid layerId in layerIds)
        {
            if (!layers.TryGetValue(layerId, out var entry))
                throw new InvalidOperationException("Occlusion relation references a missing layer.");

            Texture texture = GetOrRenderIsolatedLayer(layerId, document, layers, isolatedLayers,
                renderMatrix, renderSize, baseContext);
            using Pixmap pixmap = texture.PeekPixels();
            // RgbaF16 exposes one 8-byte value per pixel. Cast the readback
            // span after obtaining that pixel-sized view so the alpha channel
            // can be inspected without a native call for every pixel.
            Span<ulong> pixelWords = pixmap.GetPixelSpan<ulong>();
            Span<Half> pixels = MemoryMarshal.Cast<ulong, Half>(pixelWords);

            int requiredPixels = checked(pixmap.Width * pixmap.Height * 4);
            if (pixels.Length < requiredPixels || scanBounds.Left < 0 || scanBounds.Top < 0 ||
                scanBounds.Right > pixmap.Width || scanBounds.Bottom > pixmap.Height)
                throw new InvalidOperationException("Occlusion texture pixels do not match the render target.");

            bool hasCommonVisiblePixel = false;
            int pixelRowStride = checked(pixmap.Width * 4);
            for (int y = scanBounds.Top; y < scanBounds.Bottom; y++)
            {
                int commonRow = (y - scanBounds.Top) * scanWidth;
                int pixelIndex = checked(y * pixelRowStride + scanBounds.Left * 4 + 3);
                for (int x = 0; x < scanWidth; x++, pixelIndex += 4)
                {
                    int commonIndex = commonRow + x;
                    if (commonVisible[commonIndex] == 0)
                        continue;

                    if (!((float)pixels[pixelIndex] > 0f))
                    {
                        commonVisible[commonIndex] = 0;
                    }
                    else
                    {
                        hasCommonVisiblePixel = true;
                    }
                }
            }

            if (!hasCommonVisiblePixel)
                return false;
        }

        return true;
    }

    private static bool TryGetCommonRenderBounds(IEnumerable<Guid> layerIds,
        IReadOnlyDictionary<Guid, (IReadOnlyLayerNode Layer, int Order)> layers,
        KeyFrameTime frameTime,
        Matrix3X3 renderMatrix,
        VecI renderSize,
        out RectI scanBounds)
    {
        scanBounds = RectI.Empty;
        if (renderSize.X <= 0 || renderSize.Y <= 0)
            return false;

        RectD commonBounds = new(0, 0, renderSize.X, renderSize.Y);
        foreach (Guid layerId in layerIds)
        {
            if (!layers.TryGetValue(layerId, out var entry))
                return false;

            RectD? layerBounds = entry.Layer.GetTightBounds(frameTime);
            if (!layerBounds.HasValue)
                return false;

            RectD renderBounds = renderMatrix.TransformRect(layerBounds.Value);
            if (renderBounds.HasNaNOrInfinity)
                throw new InvalidOperationException("Occlusion layer bounds are invalid.");

            commonBounds = commonBounds.Intersect(renderBounds);
            if (commonBounds.IsZeroOrNegativeArea)
                return false;
        }

        scanBounds = ((RectI)commonBounds.RoundOutwards())
            .Intersect(new RectI(0, 0, renderSize.X, renderSize.Y));
        return !scanBounds.IsZeroOrNegativeArea;
    }
}
