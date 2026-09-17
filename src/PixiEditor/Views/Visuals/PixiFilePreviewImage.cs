using Avalonia;
using Avalonia.Threading;
using Drawie.Backend.Core;
using Drawie.Backend.Core.Surfaces;
using Drawie.Backend.Core.Surfaces.ImageData;
using PixiEditor.Extensions.Exceptions;
using PixiEditor.Helpers;
using PixiEditor.Models;
using PixiEditor.Models.IO;
using Drawie.Numerics;
using PixiEditor.Parser;

namespace PixiEditor.Views.Visuals;

internal class PixiFilePreviewImage : TextureControl
{
    public static readonly StyledProperty<string> FilePathProperty =
        AvaloniaProperty.Register<PixiFilePreviewImage, string>(nameof(FilePath));

    public static readonly StyledProperty<VecI> ImageSizeProperty =
        AvaloniaProperty.Register<PixiFilePreviewImage, VecI>(nameof(VecI));

    public static readonly StyledProperty<bool> CorruptProperty =
        AvaloniaProperty.Register<PixiFilePreviewImage, bool>(nameof(Corrupt));

    public string FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public VecI ImageSize
    {
        get => GetValue(ImageSizeProperty);
        set => SetValue(ImageSizeProperty, value);
    }

    public bool Corrupt
    {
        get => GetValue(CorruptProperty);
        set => SetValue(CorruptProperty, value);
    }

    static PixiFilePreviewImage()
    {
        FilePathProperty.Changed.AddClassHandler<PixiFilePreviewImage>(OnFilePathChanged);
    }

    private void RunLoadImage()
    {
        var path = FilePath;

        Task.Run(() => LoadImage(path));
    }

    private async Task LoadImage(string path)
    {
        try
        {
            string fileExtension = Path.GetExtension(path);

            byte[] imageBytes;
            VecI? rawImageSize = null;

            bool isPixi = fileExtension == ".pixi";
            if (isPixi)
            {
                await using FileStream fileStream = File.OpenRead(path);
                imageBytes = await PixiParser.ReadPreviewAsync(fileStream);
            }
            else if (fileExtension.Equals(".psd", StringComparison.OrdinalIgnoreCase))
            {
                using Surface preview = Importer.GetPreviewSurface(path);
                imageBytes = preview.ToByteArray(ColorType.Rgba8888, AlphaType.Unpremul);
                rawImageSize = preview.Size;
            }
            else if (SupportedFilesHelper.IsExtensionSupported(fileExtension) &&
                     SupportedFilesHelper.IsRasterFormat(fileExtension))
            {
                imageBytes = await File.ReadAllBytesAsync(path);
            }
            else
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var surface = rawImageSize is VecI size
                        ? Texture.Load(imageBytes, ColorType.Rgba8888, size)
                        : LoadTexture(imageBytes);
                    SetImage(surface);
                }
                catch (Exception)
                {
                    SetCorrupt();
                }
            });
        }
        catch (Exception)
        {
            SetCorrupt();
        }
    }

    private void SetImage(Texture? texture)
    {
        Texture = texture!;

        if (texture != null)
        {
            ImageSize = texture.Size;
        }
    }

    private static void OnFilePathChanged(PixiFilePreviewImage previewImage, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue == null)
        {
            previewImage.Texture = null;
            return;
        }

        previewImage.RunLoadImage();
    }

    private Texture LoadTexture(byte[] textureBytes)
    {
        Texture loaded = null;

        try
        {
            loaded = Texture.Load(textureBytes);
        }
        catch (RecoverableException)
        {
            SetCorrupt();
        }

        if (loaded == null) //prevent crash
            return null;

        if (loaded.Size is { X: <= Constants.MaxPreviewWidth, Y: <= Constants.MaxPreviewHeight })
        {
            return loaded;
        }

        var downscaled = DownscaleSurface(loaded);
        loaded.Dispose();
        return downscaled;
    }

    private static Texture DownscaleSurface(Texture surface)
    {
        double factor = Math.Min(
            Constants.MaxPreviewWidth / (double)surface.Size.X,
            Constants.MaxPreviewHeight / (double)surface.Size.Y);

        var newSize = new VecI((int)(surface.Size.X * factor), (int)(surface.Size.Y * factor));

        var scaledBitmap = surface.Resize(newSize, FilterQuality.High);

        surface.Dispose();
        return scaledBitmap;
    }

    // TODO: This does not actually set the dot to gray
    void SetCorrupt()
    {
        Dispatcher.UIThread.Post(() => Corrupt = true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Texture?.Dispose();
    }
}
