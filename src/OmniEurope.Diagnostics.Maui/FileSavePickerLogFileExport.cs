// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Diagnostics.Logs;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OmniEurope.Diagnostics.Maui;

/// <summary>
/// Offers an exported log file through the Windows save dialog (<see cref="FileSavePicker"/>), initialized with the
/// handle of the application's current window as an unpackaged desktop application requires. Cancelling the dialog
/// ends the export without writing anything. Not covered by unit tests, which cannot drive a system dialog: the
/// sample application exercises it.
/// </summary>
internal sealed class FileSavePickerLogFileExport : ILogFileExport
{
    /// <summary>The extension a file name without one is saved under.</summary>
    internal const string FallbackExtension = ".txt";

    /// <inheritdoc />
    public async Task ExportAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var extension = ExtensionOf(fileName);
        var file = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(fileName)
            };
            picker.FileTypeChoices.Add($"{extension.TrimStart('.').ToUpperInvariant()} (*{extension})", [extension]);
            InitializeWithWindow.Initialize(picker, CurrentWindowHandle());
            return await picker.PickSaveFileAsync().AsTask(cancellationToken);
        });
        if (file is null) return;

        await using var target = await file.OpenStreamForWriteAsync();
        target.SetLength(0);
        await content.CopyToAsync(target, cancellationToken);
    }

    /// <summary>The extension, dot included, a file is saved under: its own, or <see cref="FallbackExtension"/>.</summary>
    internal static string ExtensionOf(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 1 ? extension : FallbackExtension;
    }

    /// <summary>The native handle of the application's current window, which the picker is parented to.</summary>
    private static nint CurrentWindowHandle()
    {
        var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window
            ?? throw new InvalidOperationException("No application window is open: the save dialog needs one to attach to.");
        return WindowNative.GetWindowHandle(window);
    }
}
