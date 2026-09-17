using System.Buffers.Binary;
using System.Drawing;
using Avalonia.Headless.XUnit;
using bzPSD;
using Drawie.Backend.Core;
using Drawie.Backend.Core.Bridge;
using Drawie.Backend.Core.Surfaces.ImageData;
using Drawie.Numerics;
using PixiEditor.ChangeableDocument.Changeables.Graph.Interfaces;
using PixiEditor.ChangeableDocument.Changeables.Interfaces;
using PixiEditor.ChangeableDocument.Changeables.Graph.Nodes;
using PixiEditor.ChangeableDocument.Enums;
using PixiEditor.Models.IO;
using PixiEditor.ViewModels.Document;
using PsdLayer = System.Drawing.PSD.Layer;

namespace PixiEditor.Tests;

public sealed class PsdImportExportTests : FullPixiEditorTest
{
    [AvaloniaFact]
    public void ImportsPsdLayerHierarchyAndMetadata()
    {
        string sourcePath = CreateFixture();
        try
        {
            using DocumentViewModel document = Importer.ImportPsdDocument(sourcePath);
            Assert.Equal(new VecI(4, 3), document.SizeBindable);

            IReadOnlyDocument readOnlyDocument = document.AccessInternalReadOnlyDocument();
            IReadOnlyNode groupNode = readOnlyDocument.NodeGraph.OutputNode
                .GetInputProperty(OutputNode.InputPropertyName)!.Connection!.Node;
            IReadOnlyFolderNode group = Assert.IsAssignableFrom<IReadOnlyFolderNode>(groupNode);
            Assert.Equal("グループ", group.MemberName);
            Assert.Equal(1f, group.Opacity.Value);

            IReadOnlyNode topChild = group.Content.Connection!.Node;
            IReadOnlyStructureNode topLayer = Assert.IsAssignableFrom<IReadOnlyStructureNode>(topChild);
            Assert.Equal("前景", topLayer.MemberName);
            Assert.Equal(BlendMode.Multiply, topLayer.BlendMode.Value);
            Assert.Equal(128 / 255f, topLayer.Opacity.Value, 3);
            Assert.True(topLayer.ClipToPreviousMember);
            Assert.NotNull(topLayer.EmbeddedMask);

            IReadOnlyNode bottomChild = topLayer.GetInputProperty("Background")!.Connection!.Node;
            IReadOnlyStructureNode bottomLayer = Assert.IsAssignableFrom<IReadOnlyStructureNode>(bottomChild);
            Assert.Equal("背景", bottomLayer.MemberName);
            Assert.True(bottomLayer.IsVisible.Value);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [AvaloniaFact]
    public void ExportsAnEditablePsdThatCanBeLoadedAgain()
    {
        string sourcePath = CreateFixture();
        string exportedPath = Path.Combine(Path.GetTempPath(), $"pixieditor-psd-roundtrip-{Guid.NewGuid():N}.psd");
        try
        {
            using DocumentViewModel document = Importer.ImportPsdDocument(sourcePath);
            ExportConfig config = new(new VecI(4, 3)) { ExportOutput = "DEFAULT" };

            PsdDocumentConverter.Export(exportedPath, document, config, null);

            PsdFile exported = new PsdFile().Load(exportedPath);
            Assert.Equal(1, exported.Version);
            Assert.Equal(4, exported.Columns);
            Assert.Equal(3, exported.Rows);
            Assert.Equal(4, exported.Channels);

            PsdLayer[] layers = exported.Layers.ToArray();
            Assert.Equal(["グループ", "背景", "前景", "グループ"], layers.Select(GetUnicodeName).ToArray());
            Assert.Contains(layers, layer => GetUnicodeName(layer) == "背景");
            Assert.Contains(layers, layer => GetUnicodeName(layer) == "前景");
            Assert.Contains(layers, layer => GetUnicodeName(layer) == "グループ");
            Assert.Contains(layers, layer => layer.AdjustmentInfo.Any(info => info.Key == "lsct"));
            PsdLayer exportedForeground = Assert.Single(layers.Where(layer => GetUnicodeName(layer) == "前景"));
            Assert.True(exportedForeground.Clipping);
            Assert.Contains((short)-2, exportedForeground.SortedChannels.Keys);

            using DocumentViewModel reimported = Importer.ImportPsdDocument(exportedPath, false);
            IReadOnlyDocument readOnlyDocument = reimported.AccessInternalReadOnlyDocument();
            Assert.Equal(new VecI(4, 3), reimported.SizeBindable);
            IReadOnlyFolderNode reimportedGroup = Assert.IsAssignableFrom<IReadOnlyFolderNode>(
                readOnlyDocument.NodeGraph.OutputNode.GetInputProperty(OutputNode.InputPropertyName)!.Connection!.Node);
            IReadOnlyStructureNode reimportedTopLayer = Assert.IsAssignableFrom<IReadOnlyStructureNode>(
                reimportedGroup.Content.Connection!.Node);
            Assert.Equal("前景", reimportedTopLayer.MemberName);
            IReadOnlyStructureNode reimportedBottomLayer = Assert.IsAssignableFrom<IReadOnlyStructureNode>(
                reimportedTopLayer.GetInputProperty("Background")!.Connection!.Node);
            Assert.Equal("背景", reimportedBottomLayer.MemberName);
            IReadOnlyStructureNode reimportedForeground = Assert.Single(
                readOnlyDocument.GetStructureTreeInOrder().Where(node => node.MemberName == "前景"));
            Assert.NotNull(reimportedForeground.EmbeddedMask);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(exportedPath);
        }
    }

    private static string CreateFixture()
    {
        const int width = 4;
        const int height = 3;
        int pixelCount = width * height;

        PsdFile psd = PsdFile.Create(width, height, ColorMode.RGB, 8);
        psd.Channels = 4;
        psd.ImageCompression = ImageCompression.Rle;
        psd.ImageData =
        [
            Solid(pixelCount, 255),
            Solid(pixelCount, 0),
            Solid(pixelCount, 0),
            Solid(pixelCount, 255)
        ];

        PsdLayer opening = PsdLayer.Create(psd, Rectangle.Empty, "グループ", blendModeKey: "pass");
        AddSectionDivider(opening, 3, "pass");
        AddUnicodeName(opening, "グループ");

        PsdLayer background = PsdLayer.Create(psd, new Rectangle(0, 0, width, height), "背景");
        background.AddChannel(0, Solid(pixelCount, 0));
        background.AddChannel(1, Solid(pixelCount, 0));
        background.AddChannel(2, Solid(pixelCount, 255));
        background.AddChannel(-1, Solid(pixelCount, 255));
        AddUnicodeName(background, "背景");

        PsdLayer foreground = PsdLayer.Create(psd, new Rectangle(0, 0, width, height), "前景",
            opacity: 128, blendModeKey: "mul ", clipping: true);
        foreground.AddChannel(0, Solid(pixelCount, 255));
        foreground.AddChannel(1, Solid(pixelCount, 0));
        foreground.AddChannel(2, Solid(pixelCount, 0));
        foreground.AddChannel(-1, Solid(pixelCount, 255));
        foreground.AddMask(new Rectangle(0, 0, width, height), Solid(pixelCount, 160));
        AddUnicodeName(foreground, "前景");

        PsdLayer closing = PsdLayer.Create(psd, Rectangle.Empty, "グループ", blendModeKey: "pass");
        AddSectionDivider(closing, 1, "pass");
        AddUnicodeName(closing, "グループ");

        // PSD layer records are stored from bottom to top. The logical stack
        // shown by Photoshop is: group > 前景 > 背景.
        psd.AddLayer(closing);
        psd.AddLayer(background);
        psd.AddLayer(foreground);
        psd.AddLayer(opening);

        string path = Path.Combine(Path.GetTempPath(), $"pixieditor-psd-fixture-{Guid.NewGuid():N}.psd");
        psd.Save(path);
        return path;
    }

    private static byte[] Solid(int length, byte value)
    {
        byte[] data = new byte[length];
        Array.Fill(data, value);
        return data;
    }

    private static void AddUnicodeName(PsdLayer layer, string name)
    {
        byte[] nameBytes = System.Text.Encoding.BigEndianUnicode.GetBytes(name);
        byte[] data = new byte[4 + nameBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), name.Length);
        nameBytes.CopyTo(data, 4);
        new PsdLayer.AdjusmentLayerInfo("luni", layer) { Data = data };
    }

    private static void AddSectionDivider(PsdLayer layer, int sectionType, string blendModeKey)
    {
        byte[] data = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), sectionType);
        System.Text.Encoding.ASCII.GetBytes("8BIM").CopyTo(data, 4);
        System.Text.Encoding.ASCII.GetBytes(blendModeKey).CopyTo(data, 8);
        new PsdLayer.AdjusmentLayerInfo("lsct", layer) { Data = data };
    }

    private static string? GetUnicodeName(PsdLayer layer)
    {
        PsdLayer.AdjusmentLayerInfo? info = layer.AdjustmentInfo.FirstOrDefault(x => x.Key == "luni");
        if (info?.Data is not { Length: >= 4 } data)
        {
            return null;
        }

        int characterCount = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
        int byteCount = Math.Min(characterCount * 2, data.Length - 4);
        return characterCount >= 0 && byteCount >= 0 && byteCount % 2 == 0
            ? System.Text.Encoding.BigEndianUnicode.GetString(data, 4, byteCount).TrimEnd('\0')
            : null;
    }
}
