using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace ImmichSplash.App.Services;

/// <summary>
/// Puts a picture on the system clipboard, so it pastes into chats, documents and image editors.
/// Windows: as a bitmap (CF_DIB, understood everywhere) plus PNG. Linux: PNG through wl-copy
/// (Wayland) or xclip (X11).
/// </summary>
public static partial class ImageClipboard
{
    public static void Copy(SKBitmap bitmap, IntPtr window)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var pngBytes = png.ToArray();

        if (OperatingSystem.IsWindows())
            CopyWindows(bitmap, pngBytes, window);
        else if (OperatingSystem.IsLinux())
            CopyLinux(pngBytes);
        else
            throw new PlatformNotSupportedException();
    }

    // ---- Windows ----

    const uint CfDib = 8;
    const uint GmemMoveable = 0x0002;

    static void CopyWindows(SKBitmap bitmap, byte[] png, IntPtr window)
    {
        var dib = ToDib(bitmap);
        // Another app may hold the clipboard for a moment.
        var opened = false;
        for (var attempt = 0; attempt < 10 && !(opened = OpenClipboard(window)); attempt++)
            Thread.Sleep(50);
        if (!opened)
            throw new IOException("The clipboard is in use by another app. Try again.");
        try
        {
            EmptyClipboard();
            SetData(CfDib, dib);
            SetData(RegisterClipboardFormatW("PNG"), png);
        }
        finally
        {
            CloseClipboard();
        }
    }

    static void SetData(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GmemMoveable, (nuint)data.Length);
        if (handle == IntPtr.Zero)
            throw new OutOfMemoryException();
        var target = GlobalLock(handle);
        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) == IntPtr.Zero)
            throw new IOException("Couldn't put the picture on the clipboard.");
        // The clipboard owns the memory now.
    }

    /// <summary>A 32-bit device-independent bitmap: header, then rows bottom-up in BGRA.</summary>
    static byte[] ToDib(SKBitmap source)
    {
        using var bgra = source.ColorType == SKColorType.Bgra8888 && source.AlphaType != SKAlphaType.Unpremul
            ? source.Copy()
            : source.Copy(SKColorType.Bgra8888);
        int width = bgra.Width, height = bgra.Height, stride = width * 4;
        var result = new byte[40 + stride * height];
        using (var header = new BinaryWriter(new MemoryStream(result)))
        {
            header.Write(40);            // biSize
            header.Write(width);         // biWidth
            header.Write(height);        // biHeight (positive: bottom-up)
            header.Write((short)1);      // biPlanes
            header.Write((short)32);     // biBitCount
            header.Write(0);             // biCompression = BI_RGB
            header.Write(stride * height);
            header.Write(2835);          // 72 dpi
            header.Write(2835);
            header.Write(0);
            header.Write(0);
        }
        var pixels = bgra.Bytes;
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(pixels, row * bgra.RowBytes, result, 40 + (height - 1 - row) * stride, stride);
        return result;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetClipboardData(uint format, IntPtr memory);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    // ---- Linux ----

    /// <summary>The main window's clipboard (set by the window).</summary>
    public static Avalonia.Input.Platform.IClipboard? Clipboard { get; set; }

    static void CopyLinux(byte[] png)
    {
        if (Clipboard is { } clipboard)
        {
            try
            {
                // Called off the UI thread; the clipboard lives on it.
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var data = new Avalonia.Input.DataTransfer();
                    data.Add(Avalonia.Input.DataTransferItem.Create(Avalonia.Input.DataFormat.CreateBytesPlatformFormat("image/png"), png));
                    await clipboard.SetDataAsync(data);
                }).GetAwaiter().GetResult();
                return;
            }
            catch (Exception ex)
            {
                Core.Log.Info($"The app's clipboard didn't take the picture: {ex.Message}");
            }
        }
        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        (string File, string[] Args)[] tools = wayland
            ? [("wl-copy", ["--type", "image/png"]), ("xclip", ["-selection", "clipboard", "-t", "image/png", "-i"])]
            : [("xclip", ["-selection", "clipboard", "-t", "image/png", "-i"]), ("wl-copy", ["--type", "image/png"])];
        foreach (var (file, args) in tools)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardInput = true });
                if (process is null)
                    continue;
                process.StandardInput.BaseStream.Write(png);
                process.StandardInput.Close();
                // These tools keep running to serve the clipboard; don't wait for them to exit.
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed; try the next one.
            }
        }
        throw new InvalidOperationException(Tr.T("Copying pictures needs wl-clipboard (Wayland) or xclip (X11) installed."));
    }
}
