using System.Buffers.Binary;
using System.Drawing.PSD;
using System.Text;
using bzPSD;
using ChunkyImageLib;
using Drawie.Backend.Core;
using Drawie.Backend.Core.Bridge;
using Drawie.Backend.Core.Numerics;
using Drawie.Backend.Core.Surfaces.ImageData;
using Drawie.Backend.Core.Surfaces.PaintImpl;
using Drawie.Numerics;
using PixiEditor.ChangeableDocument.Changeables.Graph.Interfaces;
using PixiEditor.ChangeableDocument.Changeables.Interfaces;
using PixiEditor.ChangeableDocument.Changeables.Graph.Nodes;
using PixiEditor.ChangeableDocument.Rendering;
using PixiEditor.ChangeableDocument.Enums;
using PixiEditor.Helpers;
using PixiEditor.Parser.Graph;
using PixiEditor.ViewModels.Document;
using ChunkResolution = ChunkyImageLib.DataHolders.ChunkResolution;
using BlendMode = PixiEditor.ChangeableDocument.Enums.BlendMode;
using DrawingBlendMode = Drawie.Backend.Core.Surfaces.BlendMode;
using DrawingRectangle = System.Drawing.Rectangle;
using PsdLayer = System.Drawing.PSD.Layer;

namespace PixiEditor.Models.IO;

internal sealed class PsdUnsupportedException : Exception
{
    public PsdUnsupportedException(string message) : base(message)
    {
    }

    public PsdUnsupportedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

internal sealed class PsdCorruptedException : Exception
{
    public PsdCorruptedException(string message) : base(message)
    {
    }

    public PsdCorruptedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Converts the PSD v1 RGB/8-bit representation used by bzPSD to PixiEditor's
/// document graph and back again. The adapter deliberately keeps unsupported PSD
/// layer kinds as raster/transparent placeholders instead of flattening the whole
/// document, so the editable layer hierarchy and metadata remain available.
/// </summary>
internal static class PsdDocumentConverter
{
    private const int PsdHeaderLength = 26;
    private const int MaximumDimension = 30000;
    private const string UnicodeLayerNameKey = "luni";
    private const string SectionDividerKey = "lsct";
    private const string NestedSectionDividerKey = "lsdk";

    public static DocumentViewModel Import(string path, bool associatePath)
    {
        if (!File.Exists(path))
        {
            throw new PixiEditor.Exceptions.MissingFileException();
        }

        PsdFile psd = Load(path);
        DocumentViewModel document = BuildDocument(psd);
        if (associatePath)
        {
            document.FullFilePath = path;
        }

        return document;
    }

    public static Surface LoadPreview(string path)
    {
        if (!File.Exists(path))
        {
            throw new PixiEditor.Exceptions.MissingFileException();
        }

        return CreateCompositeSurface(Load(path));
    }

    public static void Export(string path, DocumentViewModel document, ExportConfig config, ExportJob? job)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        VecI size = document.SizeBindable;
        if (size.X <= 0 || size.Y <= 0)
        {
            throw new PsdCorruptedException("The document has an invalid canvas size.");
        }

        job?.Report(0, "Preparing PSD");
        PsdFile psd = PsdFile.Create(size.X, size.Y, ColorMode.RGB, 8);
        psd.Channels = 4;
        psd.ImageCompression = ImageCompression.Rle;
        psd.ImageData = new byte[4][];

        job?.CancellationTokenSource.Token.ThrowIfCancellationRequested();
        using (Surface composite = RenderDocument(document, size, config.ExportOutput))
        {
            psd.ImageData = ToPlanarChannels(composite, size);
        }

        IReadOnlyDocument readOnlyDocument = document.AccessInternalReadOnlyDocument();
        IReadOnlyNode? topNode = readOnlyDocument.NodeGraph.OutputNode
            .GetInputProperty(OutputNode.InputPropertyName)?.Connection?.Node;
        List<IReadOnlyStructureNode> topLevelNodes = ReadStructureChain(topNode).ToList();

        int totalNodes = CountExportNodes(topLevelNodes);
        int completedNodes = 0;
        // Photoshop stores the flat layer records from bottom to top, while
        // ReadStructureChain returns Pixi's top-to-bottom stack order.
        foreach (IReadOnlyStructureNode node in topLevelNodes.AsEnumerable().Reverse())
        {
            ExportNode(psd, node, document, size, job, ref completedNodes, totalNodes);
        }

        job?.CancellationTokenSource.Token.ThrowIfCancellationRequested();
        job?.Report(0.95, "Writing PSD");
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        psd.Save(stream);
        job?.Report(1, "Finished");
    }

    private static PsdFile Load(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateHeader(stream);
            stream.Position = 0;
            return LoadValidated(stream);
        }
        catch (PsdUnsupportedException)
        {
            throw;
        }
        catch (PsdCorruptedException)
        {
            throw;
        }
        catch (NotSupportedException e)
        {
            throw new PsdUnsupportedException("The PSD contains a feature that PixiEditor cannot decode.", e);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or
                                      ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            throw new PsdCorruptedException("The PSD file is incomplete or invalid.", e);
        }
    }

    private static PsdFile LoadValidated(Stream stream)
    {
        try
        {
            PsdFile psd = new PsdFile().Load(stream);
            ValidateLoadedFile(psd);
            return psd;
        }
        catch (PsdUnsupportedException)
        {
            throw;
        }
        catch (NotSupportedException e)
        {
            throw new PsdUnsupportedException("The PSD contains a feature that PixiEditor cannot decode.", e);
        }
        catch (Exception e)
        {
            throw new PsdCorruptedException("The PSD file is incomplete or invalid.", e);
        }
    }

    private static void ValidateHeader(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < PsdHeaderLength)
        {
            throw new PsdCorruptedException("The PSD header is incomplete.");
        }

        stream.Position = 0;
        byte[] header = new byte[PsdHeaderLength];
        int read = stream.Read(header, 0, header.Length);
        if (read != header.Length || !header.AsSpan(0, 4).SequenceEqual("8BPS"u8))
        {
            throw new PsdCorruptedException("The file is not a Photoshop PSD file.");
        }

        short version = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(4, 2));
        if (version != 1)
        {
            throw new PsdUnsupportedException("PSB and other PSD versions are not supported; use a PSD v1 file.");
        }

        short channels = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(12, 2));
        int rows = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(14, 4));
        int columns = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(18, 4));
        short depth = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(22, 2));
        short colorMode = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(24, 2));

        ValidateDimensions(columns, rows);
        if (channels is < 3 or > 4)
        {
            throw new PsdUnsupportedException("Only RGB PSD files with three or four channels are supported.");
        }

        if (depth != 8)
        {
            throw new PsdUnsupportedException("Only 8-bit PSD files are supported at this time.");
        }

        if (colorMode != (short)ColorMode.RGB)
        {
            throw new PsdUnsupportedException("Only RGB PSD files are supported at this time.");
        }
    }

    private static void ValidateLoadedFile(PsdFile psd)
    {
        ValidateDimensions(psd.Columns, psd.Rows);
        if (psd.Version != 1)
        {
            throw new PsdUnsupportedException("PSB and other PSD versions are not supported; use a PSD v1 file.");
        }

        if (psd.Channels is < 3 or > 4 || psd.Depth != 8 || psd.ColorMode != ColorMode.RGB)
        {
            throw new PsdUnsupportedException("Only RGB PSD files with 8-bit channels are supported.");
        }

        if (psd.ImageData is null || psd.ImageData.Length < 3)
        {
            throw new PsdCorruptedException("The PSD does not contain a complete composite image.");
        }
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension)
        {
            throw new PsdUnsupportedException(
                $"PSD canvas dimensions must be between 1 and {MaximumDimension} pixels.");
        }

        long pixelBytes = (long)width * height * 4;
        if (pixelBytes > int.MaxValue)
        {
            throw new PsdUnsupportedException("The PSD canvas is too large to import safely.");
        }
    }

    private static DocumentViewModel BuildDocument(PsdFile psd)
    {
        List<PsdEntry> entries = BuildEntries(psd.Layers);
        return DocumentViewModel.Build(builder =>
        {
            builder.WithSize(psd.Columns, psd.Rows)
                .WithGraph(graph =>
                {
                    int? previousId = null;
                    foreach (PsdEntry entry in entries)
                    {
                        previousId = AddEntry(graph, entry, previousId, psd.Columns, psd.Rows);
                    }

                    if (previousId is null)
                    {
                        Surface composite = CreateCompositeSurface(psd);
                        graph.WithImageLayerNode("PSD Composite", composite, ColorSpace.CreateSrgbLinear(),
                            out int compositeId);
                        previousId = compositeId;
                    }

                    graph.WithOutputNode(previousId, "Output");
                });
        });
    }

    private static int AddEntry(NodeGraphBuilder graph, PsdEntry entry, int? previousId, int width, int height)
    {
        if (entry.IsGroup)
        {
            int? childTopId = null;
            foreach (PsdEntry child in entry.Children)
            {
                childTopId = AddEntry(graph, child, childTopId, width, height);
            }

            NodeGraphBuilder.NodeBuilder nodeBuilder = graph.WithNodeOfType<FolderNode>(out int id)
                .WithName(GetLayerName(entry.EffectiveLayer, "Group"))
                .WithInputValues(GetInputValues(entry.EffectiveLayer));

            Dictionary<string, object> additionalData = GetAdditionalData(entry.EffectiveLayer, width, height);
            List<PropertyConnection> connections = new();
            if (previousId is not null)
            {
                connections.Add(new PropertyConnection
                {
                    InputPropertyName = "Background",
                    OutputPropertyName = "Output",
                    OutputNodeId = previousId.Value
                });
            }

            if (childTopId is not null)
            {
                connections.Add(new PropertyConnection
                {
                    InputPropertyName = FolderNode.ContentInternalName,
                    OutputPropertyName = "Output",
                    OutputNodeId = childTopId.Value
                });
            }

            nodeBuilder.WithAdditionalData(additionalData);
            if (connections.Count > 0)
            {
                nodeBuilder.WithConnections(connections.ToArray());
            }

            return id;
        }

        Surface layerSurface = CreateLayerSurface(entry.Layer, width, height);
        graph.WithImageLayerNode(
            GetLayerName(entry.Layer, "Layer"), layerSurface, ColorSpace.CreateSrgbLinear(), out int layerId);
        NodeGraphBuilder.NodeBuilder imageBuilder = graph.AllNodes[^1]
            .WithInputValues(GetInputValues(entry.Layer));

        imageBuilder.WithAdditionalData(GetAdditionalData(entry.Layer, width, height));
        if (previousId is not null)
        {
            imageBuilder.WithConnections([
                new PropertyConnection
                {
                    InputPropertyName = "Background",
                    OutputPropertyName = "Output",
                    OutputNodeId = previousId.Value
                }
            ]);
        }

        return layerId;
    }

    private static Dictionary<string, object> GetInputValues(PsdLayer layer)
    {
        return new Dictionary<string, object>
        {
            [StructureNode.IsVisiblePropertyName] = layer.Visible,
            [StructureNode.OpacityPropertyName] = layer.Opacity / 255f,
            [StructureNode.BlendModePropertyName] = (int)MapBlendModeFromPsd(layer.BlendModeKey),
            [StructureNode.MaskIsVisiblePropertyName] = !layer.MaskData.Disabled
        };
    }

    private static Dictionary<string, object> GetAdditionalData(PsdLayer layer, int width, int height)
    {
        Dictionary<string, object> additionalData = new();
        if (layer.Clipping)
        {
            additionalData["clipToPreviousMember"] = true;
        }

        if (TryCreateMask(layer, width, height, out ChunkyImage? mask))
        {
            additionalData["embeddedMask"] = mask;
        }

        return additionalData;
    }

    private static List<PsdEntry> BuildEntries(IEnumerable<PsdLayer> layers)
    {
        List<PsdEntry> roots = new();
        Stack<List<PsdEntry>> childLists = new();
        Stack<PsdEntry> groups = new();
        childLists.Push(roots);

        // PSD layer records are stored in reverse stacking order. Reverse the
        // complete flat record list before interpreting section dividers so
        // group opening/closing markers are paired in the same order that
        // Photoshop presents them.
        foreach (PsdLayer layer in layers.Reverse())
        {
            int? sectionType = GetSectionType(layer);
            switch (sectionType)
            {
                case 3:
                {
                    PsdEntry group = PsdEntry.CreateGroup(layer);
                    childLists.Peek().Add(group);
                    groups.Push(group);
                    childLists.Push(group.Children);
                    break;
                }
                case 1:
                case 2:
                    if (groups.Count > 0)
                    {
                        PsdEntry group = groups.Pop();
                        group.EndLayer = layer;
                        childLists.Pop();
                    }

                    break;
                default:
                    childLists.Peek().Add(PsdEntry.CreateLayer(layer));
                    break;
            }
        }

        // A malformed-but-readable PSD can omit a closing divider. Keep the
        // already-created hierarchy rather than dropping the open groups.
        while (childLists.Count > 1)
        {
            childLists.Pop();
        }

        // The parser above now has Photoshop's top-to-bottom logical order.
        // Pixi's Background connections are built from the bottom layer toward
        // the top layer, so reverse each sibling list before constructing the
        // document graph.
        NormalizeEntryOrder(roots);
        return roots;
    }

    private static void NormalizeEntryOrder(List<PsdEntry> entries)
    {
        entries.Reverse();
        foreach (PsdEntry entry in entries)
        {
            NormalizeEntryOrder(entry.Children);
        }
    }

    private static int? GetSectionType(PsdLayer layer)
    {
        PsdLayer.AdjusmentLayerInfo? info = layer.AdjustmentInfo.FirstOrDefault(x =>
            x.Key is SectionDividerKey or NestedSectionDividerKey);
        if (info?.Data is not { Length: >= 4 } data)
        {
            return null;
        }

        return BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
    }

    private static string GetLayerName(PsdLayer layer, string fallback)
    {
        PsdLayer.AdjusmentLayerInfo? info = layer.AdjustmentInfo.FirstOrDefault(x => x.Key == UnicodeLayerNameKey);
        if (info?.Data is { Length: >= 4 } data)
        {
            int characterCount = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
            int byteCount = Math.Min(characterCount * 2, data.Length - 4);
            if (characterCount >= 0 && byteCount > 0 && byteCount % 2 == 0)
            {
                string unicodeName = Encoding.BigEndianUnicode.GetString(data, 4, byteCount).TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(unicodeName))
                {
                    return unicodeName;
                }
            }
        }

        return string.IsNullOrWhiteSpace(layer.Name) ? fallback : layer.Name;
    }

    private static Surface CreateCompositeSurface(PsdFile psd)
    {
        byte[] rgba = FromPlanarChannels(psd.ImageData, psd.Columns, psd.Rows, psd.Channels);
        Surface surface = CreateRgbaSurface(new VecI(psd.Columns, psd.Rows));
        surface.DrawBytes(surface.Size, rgba, ColorType.Rgba8888, AlphaType.Unpremul);
        return surface;
    }

    private static Surface CreateLayerSurface(PsdLayer layer, int width, int height)
    {
        Surface target = CreateRgbaSurface(new VecI(width, height));
        int layerWidth = layer.Rect.Width;
        int layerHeight = layer.Rect.Height;
        if (layerWidth <= 0 || layerHeight <= 0)
        {
            return target;
        }

        long pixelCount = (long)layerWidth * layerHeight;
        if (pixelCount > int.MaxValue / 4)
        {
            target.Dispose();
            throw new PsdUnsupportedException("A PSD layer is too large to import safely.");
        }

        byte[] rgba = new byte[(int)pixelCount * 4];
        byte[]? red = GetChannelData(layer, 0, (int)pixelCount);
        byte[]? green = GetChannelData(layer, 1, (int)pixelCount);
        byte[]? blue = GetChannelData(layer, 2, (int)pixelCount);
        if (red is null || green is null || blue is null)
        {
            // Text, adjustment, smart-object and vector records can have no
            // directly readable RGB channels in bzPSD. The node and its PSD
            // metadata are still preserved; the transparent surface is the
            // safe editable placeholder for such a record.
            return target;
        }

        byte[]? alpha = GetChannelData(layer, -1, (int)pixelCount);
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            rgba[offset] = red[i];
            rgba[offset + 1] = green[i];
            rgba[offset + 2] = blue[i];
            rgba[offset + 3] = alpha?[i] ?? byte.MaxValue;
        }

        using Surface partial = CreateRgbaSurface(new VecI(layerWidth, layerHeight));
        partial.DrawBytes(partial.Size, rgba, ColorType.Rgba8888, AlphaType.Unpremul);
        using Paint paint = new() { BlendMode = DrawingBlendMode.Src };
        target.DrawingSurface.Canvas.DrawSurface(partial.DrawingSurface, layer.Rect.X, layer.Rect.Y, paint);
        return target;
    }

    private static byte[]? GetChannelData(PsdLayer layer, short id, int expectedLength)
    {
        if (!layer.SortedChannels.TryGetValue(id, out PsdLayer.Channel? channel) ||
            channel.ImageData is null)
        {
            return null;
        }

        if (channel.ImageData.Length < expectedLength)
        {
            throw new PsdCorruptedException("A PSD layer channel is shorter than its declared bounds.");
        }

        return channel.ImageData;
    }

    private static bool TryCreateMask(PsdLayer layer, int width, int height, out ChunkyImage? mask)
    {
        mask = null;
        PsdLayer.Mask maskData = layer.MaskData;
        if (maskData.Rect.Width <= 0 || maskData.Rect.Height <= 0 ||
            !layer.SortedChannels.ContainsKey(-2) || maskData.ImageData is null)
        {
            return false;
        }

        long maskPixels = (long)maskData.Rect.Width * maskData.Rect.Height;
        if (maskPixels > int.MaxValue)
        {
            throw new PsdUnsupportedException("A PSD layer mask is too large to import safely.");
        }

        byte[] rgba = new byte[checked(width * height * 4)];
        Array.Fill(rgba, byte.MaxValue);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int maskX;
                int maskY;
                if (maskData.PositionIsRelative)
                {
                    maskX = x - layer.Rect.X - maskData.Rect.X;
                    maskY = y - layer.Rect.Y - maskData.Rect.Y;
                }
                else
                {
                    maskX = x - maskData.Rect.X;
                    maskY = y - maskData.Rect.Y;
                }

                byte value = maskData.DefaultColor;
                if (maskX >= 0 && maskX < maskData.Rect.Width && maskY >= 0 && maskY < maskData.Rect.Height)
                {
                    int sourceIndex = maskY * maskData.Rect.Width + maskX;
                    if (sourceIndex < maskData.ImageData.Length)
                    {
                        value = maskData.ImageData[sourceIndex];
                    }
                }

                if (maskData.InvertOnBlendBit)
                {
                    value = (byte)(byte.MaxValue - value);
                }

                int targetIndex = (y * width + x) * 4;
                rgba[targetIndex] = value;
                rgba[targetIndex + 1] = value;
                rgba[targetIndex + 2] = value;
                rgba[targetIndex + 3] = byte.MaxValue;
            }
        }

        using Surface maskSurface = CreateRgbaSurface(new VecI(width, height));
        maskSurface.DrawBytes(maskSurface.Size, rgba, ColorType.Rgba8888, AlphaType.Unpremul);
        mask = new ChunkyImage(maskSurface);
        return true;
    }

    private static Surface RenderDocument(DocumentViewModel document, VecI size, string? outputName)
    {
        var rendered = document.TryRenderWholeImage(0, size, outputName);
        if (rendered.IsT0)
        {
            throw new PsdCorruptedException("PixiEditor could not render the document for PSD export.");
        }

        return rendered.AsT1;
    }

    private static int CountExportNodes(IEnumerable<IReadOnlyStructureNode> nodes)
    {
        int count = 0;
        foreach (IReadOnlyStructureNode node in nodes)
        {
            count++;
            if (node is IReadOnlyFolderNode folder)
            {
                count += CountExportNodes(ReadStructureChain(folder.Content.Connection?.Node));
            }
        }

        return Math.Max(count, 1);
    }

    private static void ExportNode(PsdFile psd, IReadOnlyStructureNode node, DocumentViewModel document, VecI size,
        ExportJob? job, ref int completedNodes, int totalNodes)
    {
        job?.CancellationTokenSource.Token.ThrowIfCancellationRequested();
        if (node is IReadOnlyFolderNode folder)
        {
            List<IReadOnlyStructureNode> children = ReadStructureChain(folder.Content.Connection?.Node).ToList();
            PsdLayer closing = CreateLayerMarker(psd, node, size, 1, group: true);
            psd.AddLayer(closing);

            // PSD layer records are written bottom-to-top, so the closing
            // marker comes first and children are emitted in reverse of the
            // top-to-bottom Pixi stack.
            for (int i = children.Count - 1; i >= 0; i--)
            {
                ExportNode(psd, children[i], document, size, job, ref completedNodes, totalNodes);
            }

            PsdLayer opening = CreateLayerMarker(psd, node, size, 3, group: true);
            psd.AddLayer(opening);
        }
        else
        {
            using Surface layerSurface = RenderLayer(document, node, size);
            PsdLayer layer = PsdLayer.Create(psd,
                new DrawingRectangle(0, 0, size.X, size.Y),
                GetSafeLayerName(node.MemberName, "Layer"),
                ToPsdOpacity(node.Opacity.Value),
                node.IsVisible.Value,
                MapBlendModeToPsd(node.BlendMode.Value, group: false),
                node.ClipToPreviousMember);

            byte[][] channels = ToPlanarChannels(layerSurface, size);
            layer.AddChannel(0, channels[0]);
            layer.AddChannel(1, channels[1]);
            layer.AddChannel(2, channels[2]);
            layer.AddChannel(-1, channels[3]);
            AddMask(psd, layer, node, size);
            AddUnicodeName(layer, GetSafeLayerName(node.MemberName, "Layer"));
            psd.AddLayer(layer);
        }

        completedNodes++;
        job?.Report(Math.Min(0.9, completedNodes / (double)totalNodes), "Exporting PSD layers");
    }

    private static PsdLayer CreateLayerMarker(PsdFile psd, IReadOnlyStructureNode node, VecI size, int sectionType,
        bool group)
    {
        string name = GetSafeLayerName(node.MemberName, "Group");
        PsdLayer marker = PsdLayer.Create(psd,
            new DrawingRectangle(0, 0, 0, 0), name,
            ToPsdOpacity(node.Opacity.Value), node.IsVisible.Value,
            MapBlendModeToPsd(node.BlendMode.Value, group), node.ClipToPreviousMember);
        AddSectionDivider(marker, sectionType, MapBlendModeToPsd(node.BlendMode.Value, group));
        AddUnicodeName(marker, name);
        return marker;
    }

    private static Surface RenderLayer(DocumentViewModel document, IReadOnlyStructureNode node, VecI size)
    {
        if (node is IReadOnlyImageNode imageNode)
        {
            Surface target = CreateRgbaSurface(size);
            IReadOnlyChunkyImage image = imageNode.GetLayerImageAtFrame(0);
            using Paint paint = new() { BlendMode = DrawingBlendMode.Src };
            image.DrawCommittedRegionOn(new RectI(VecI.Zero, image.CommittedSize), ChunkResolution.Full,
                target.DrawingSurface.Canvas, VecI.Zero, paint);
            return target;
        }

        Surface rendered = CreateRgbaSurface(size);
        DrawingBackendApi.Current.RenderingDispatcher.Invoke(() =>
        {
            document.Renderer.RenderLayer(rendered.DrawingSurface, node.Id, ChunkResolution.Full, 0, size);
        });
        return rendered;
    }

    private static void AddMask(PsdFile psd, PsdLayer layer, IReadOnlyStructureNode node, VecI size)
    {
        if (node.EmbeddedMask is null)
        {
            return;
        }

        IReadOnlyChunkyImage mask = node.EmbeddedMask;
        using Surface source = CreateRgbaSurface(mask.CommittedSize);
        using Paint paint = new() { BlendMode = DrawingBlendMode.Src };
        mask.DrawCommittedRegionOn(new RectI(VecI.Zero, mask.CommittedSize), ChunkResolution.Full,
            source.DrawingSurface.Canvas, VecI.Zero, paint);
        byte[] sourceBytes = source.ToByteArray(ColorType.Rgba8888, AlphaType.Unpremul);
        byte[] alpha = new byte[size.X * size.Y];
        for (int y = 0; y < size.Y; y++)
        {
            for (int x = 0; x < size.X; x++)
            {
                int destinationIndex = y * size.X + x;
                if (x < mask.CommittedSize.X && y < mask.CommittedSize.Y)
                {
                    alpha[destinationIndex] = sourceBytes[(destinationIndex * 4)];
                }
            }
        }

        layer.AddMask(new DrawingRectangle(0, 0, size.X, size.Y), alpha,
            ImageCompression.Rle, defaultColor: 0, disabled: !node.MaskIsVisible.Value);
    }

    private static void AddUnicodeName(PsdLayer layer, string name)
    {
        byte[] nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
        byte[] data = new byte[4 + nameBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), name.Length);
        nameBytes.CopyTo(data, 4);
        new PsdLayer.AdjusmentLayerInfo(UnicodeLayerNameKey, layer) { Data = data };
    }

    private static void AddSectionDivider(PsdLayer layer, int sectionType, string blendModeKey)
    {
        byte[] data = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), sectionType);
        Encoding.ASCII.GetBytes("8BIM").CopyTo(data, 4);
        Encoding.ASCII.GetBytes(blendModeKey).CopyTo(data, 8);
        new PsdLayer.AdjusmentLayerInfo(SectionDividerKey, layer) { Data = data };
    }

    private static byte[][] ToPlanarChannels(Surface surface, VecI size)
    {
        byte[] rgba = surface.ToByteArray(ColorType.Rgba8888, AlphaType.Unpremul);
        byte[][] result =
        [
            new byte[size.X * size.Y],
            new byte[size.X * size.Y],
            new byte[size.X * size.Y],
            new byte[size.X * size.Y]
        ];

        for (int i = 0; i < result[0].Length; i++)
        {
            int sourceIndex = i * 4;
            result[0][i] = rgba[sourceIndex];
            result[1][i] = rgba[sourceIndex + 1];
            result[2][i] = rgba[sourceIndex + 2];
            result[3][i] = rgba[sourceIndex + 3];
        }

        return result;
    }

    private static byte[] FromPlanarChannels(byte[][] channels, int width, int height, int channelCount)
    {
        int pixelCount = checked(width * height);
        if (channels.Length < 3)
        {
            throw new PsdCorruptedException("The PSD composite is missing RGB channels.");
        }

        byte[] rgba = new byte[checked(pixelCount * 4)];
        for (int i = 0; i < pixelCount; i++)
        {
            int targetIndex = i * 4;
            rgba[targetIndex] = ReadPlanarByte(channels[0], i);
            rgba[targetIndex + 1] = ReadPlanarByte(channels[1], i);
            rgba[targetIndex + 2] = ReadPlanarByte(channels[2], i);
            rgba[targetIndex + 3] = channelCount > 3 && channels.Length > 3
                ? ReadPlanarByte(channels[3], i)
                : byte.MaxValue;
        }

        return rgba;
    }

    private static byte ReadPlanarByte(byte[] channel, int index)
    {
        if (channel is null || index >= channel.Length)
        {
            throw new PsdCorruptedException("The PSD composite channel is shorter than the canvas.");
        }

        return channel[index];
    }

    private static Surface CreateRgbaSurface(VecI size)
    {
        return new Surface(new ImageInfo(size.X, size.Y, ColorType.Rgba8888, AlphaType.Unpremul,
            ColorSpace.CreateSrgb()));
    }

    private static List<IReadOnlyStructureNode> ReadStructureChain(IReadOnlyNode? top)
    {
        List<IReadOnlyStructureNode> result = new();
        HashSet<Guid> visited = new();
        IReadOnlyNode? current = top;
        while (current is IReadOnlyStructureNode structure && visited.Add(structure.Id))
        {
            result.Add(structure);
            current = structure.GetInputProperty("Background")?.Connection?.Node;
        }

        return result;
    }

    private static byte ToPsdOpacity(float opacity)
    {
        return (byte)Math.Clamp(Math.Round(opacity * 255f), 0, 255);
    }

    private static string GetSafeLayerName(string? name, string fallback)
    {
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static BlendMode MapBlendModeFromPsd(string key)
    {
        return key switch
        {
            "norm" or "pass" => BlendMode.Normal,
            "dark" => BlendMode.Darken,
            "mul " => BlendMode.Multiply,
            "idiv" => BlendMode.ColorBurn,
            "lite" => BlendMode.Lighten,
            "scrn" => BlendMode.Screen,
            "div " => BlendMode.ColorDodge,
            "lddg" => BlendMode.LinearDodge,
            "over" => BlendMode.Overlay,
            "sLit" => BlendMode.SoftLight,
            "hLit" => BlendMode.HardLight,
            "diff" => BlendMode.Difference,
            "smud" => BlendMode.Exclusion,
            "hue " => BlendMode.Hue,
            "sat " => BlendMode.Saturation,
            "lum " => BlendMode.Luminosity,
            "colr" => BlendMode.Color,
            "eraz" => BlendMode.Erase,
            _ => BlendMode.Normal
        };
    }

    private static string MapBlendModeToPsd(BlendMode mode, bool group)
    {
        if (group && mode == BlendMode.Normal)
        {
            return "pass";
        }

        return mode switch
        {
            BlendMode.Normal => "norm",
            BlendMode.Darken => "dark",
            BlendMode.Multiply => "mul ",
            BlendMode.ColorBurn => "idiv",
            BlendMode.Lighten => "lite",
            BlendMode.Screen => "scrn",
            BlendMode.ColorDodge => "div ",
            BlendMode.LinearDodge => "lddg",
            BlendMode.Overlay => "over",
            BlendMode.SoftLight => "sLit",
            BlendMode.HardLight => "hLit",
            BlendMode.Difference => "diff",
            BlendMode.Exclusion => "smud",
            BlendMode.Hue => "hue ",
            BlendMode.Saturation => "sat ",
            BlendMode.Luminosity => "lum ",
            BlendMode.Color => "colr",
            BlendMode.Erase => "eraz",
            _ => "norm"
        };
    }

    private sealed class PsdEntry
    {
        private PsdEntry(PsdLayer layer, bool isGroup)
        {
            Layer = layer;
            IsGroup = isGroup;
        }

        public PsdLayer Layer { get; }
        public PsdLayer? EndLayer { get; set; }
        public bool IsGroup { get; }
        public List<PsdEntry> Children { get; } = new();
        public PsdLayer EffectiveLayer => EndLayer ?? Layer;

        public static PsdEntry CreateGroup(PsdLayer layer) => new(layer, true);
        public static PsdEntry CreateLayer(PsdLayer layer) => new(layer, false);
    }
}
