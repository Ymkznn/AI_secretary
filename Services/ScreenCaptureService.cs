using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using AiSecretary.Desktop.Agent;
using Forms = System.Windows.Forms;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Captures the current screen or a specific open window, and lists open windows.
/// </summary>
public sealed class ScreenCaptureService
{
    private readonly SandboxService _sandbox;

    public ScreenCaptureService(SandboxService sandbox)
    {
        _sandbox = sandbox;
    }

    public IReadOnlyList<(string ProcessName, string Title)> ListOpenWindows()
    {
        var list = new List<(string, string)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    list.Add((p.ProcessName, p.MainWindowTitle));
            }
            catch { }
        }
        return list;
    }

    public (bool Ok, string Message, string? FilePath) Capture(string? windowTitleKeyword = null)
    {
        Rectangle bounds = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;

        if (!string.IsNullOrWhiteSpace(windowTitleKeyword))
        {
            var rect = FindWindowRectByKeyword(windowTitleKeyword.Trim());
            if (rect.HasValue && rect.Value.Width > 50 && rect.Value.Height > 50)
            {
                bounds = rect.Value;
            }
        }

        using var rawBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        var app = System.Windows.Application.Current;
        System.Windows.Window? mainWin = null;
        double prevOpacity = 1.0;
        bool hidSelf = false;
        try
        {
            if (app is not null)
            {
                app.Dispatcher.Invoke(() =>
                {
                    mainWin = app.MainWindow;
                    if (mainWin is not null && mainWin.IsVisible && mainWin.Opacity > 0)
                    {
                        prevOpacity = mainWin.Opacity;
                        mainWin.Opacity = 0;
                        hidSelf = true;
                    }
                });
                if (hidSelf) Thread.Sleep(60);
            }

            using (var g = Graphics.FromImage(rawBmp))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
        }
        catch (Exception ex)
        {
            return (false, $"Screen capture unavailable in current desktop session: {ex.Message}", null);
        }
        finally
        {
            if (hidSelf && mainWin is not null)
            {
                mainWin.Dispatcher.BeginInvoke(() => mainWin.Opacity = prevOpacity);
            }
        }

        // Downscale only if wider than 1920px so single-monitor 1080p text stays sharp
        Bitmap toSave = rawBmp;
        Bitmap? resized = null;
        if (rawBmp.Width > 1920)
        {
            var ratio = 1920.0 / rawBmp.Width;
            int w = 1920;
            int h = Math.Max(1, (int)(rawBmp.Height * ratio));
            resized = new Bitmap(w, h);
            using var g2 = Graphics.FromImage(resized);
            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g2.DrawImage(rawBmp, 0, 0, w, h);
            toSave = resized;
        }

        try
        {
            var fileName = $"screen_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
            var path = Path.Combine(_sandbox.RootPath, fileName);
            var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            toSave.Save(path, jpegEncoder, encParams);
            return (true, $"Captured screen ({toSave.Width}x{toSave.Height})", path);
        }
        finally
        {
            resized?.Dispose();
        }
    }

    private static Rectangle? FindWindowRectByKeyword(string keyword)
    {
        Rectangle? found = null;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var len = GetWindowTextLength(hWnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase) && GetWindowRect(hWnd, out var r))
            {
                found = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
