using System.Security;
using Avalonia.Media;
using PixiEditor.Models.IO;
using PixiEditor.UI.Common.Localization;
using PixiEditor.ViewModels.Document;

namespace PixiEditor.Models.Files;

internal sealed class PsdFileType : IoFileType
{
    public static PsdFileType PsdFile { get; } = new();

    public override string[] Extensions { get; } = [".psd"];

    public override string DisplayName => new LocalizedString("PSD_FILE");

    public override FileTypeDialogDataSet.SetKind SetKind { get; } = FileTypeDialogDataSet.SetKind.Image;

    public override SolidColorBrush EditorColor { get; } = new(Color.FromRgb(49, 49, 49));

    public override Task<SaveResult> TrySaveAsync(string pathWithExtension, DocumentViewModel document,
        ExportConfig config, ExportJob? job)
    {
        return Task.FromResult(TrySave(pathWithExtension, document, config, job));
    }

    public override SaveResult TrySave(string pathWithExtension, DocumentViewModel document, ExportConfig config,
        ExportJob? job)
    {
        if (config.ExportAsSpriteSheet || config.ExportFramesToFolder || config.AnimationRenderer is not null)
        {
            return new SaveResult(SaveResultType.CustomError, "PSD_STATIC_ONLY");
        }

        try
        {
            PsdDocumentConverter.Export(pathWithExtension, document, config, job);
            return new SaveResult(SaveResultType.Success);
        }
        catch (OperationCanceledException)
        {
            return new SaveResult(SaveResultType.Cancelled);
        }
        catch (PsdUnsupportedException)
        {
            return new SaveResult(SaveResultType.CustomError, new LocalizedString("PSD_UNSUPPORTED"));
        }
        catch (PsdCorruptedException)
        {
            return new SaveResult(SaveResultType.CustomError, new LocalizedString("PSD_CORRUPTED"));
        }
        catch (NotSupportedException)
        {
            return new SaveResult(SaveResultType.CustomError, new LocalizedString("PSD_UNSUPPORTED"));
        }
        catch (UnauthorizedAccessException)
        {
            return new SaveResult(SaveResultType.SecurityError);
        }
        catch (SecurityException)
        {
            return new SaveResult(SaveResultType.SecurityError);
        }
        catch (IOException)
        {
            return new SaveResult(SaveResultType.IoError);
        }
    }
}
