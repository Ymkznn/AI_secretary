using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AiSecretary.Desktop.Agent;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Narrowed, guarded COM/ROT wrapper for currently-open Excel and PowerPoint instances.
/// Never exposes raw COM objects, VBProject, or macro/SaveAs methods to agents.
/// Runs COM interactions on a dedicated STA thread and creates automatic timestamped SaveCopyAs backups before any write.
/// </summary>
public sealed class OfficeContextService
{
    private readonly GuardService _guard;
    private readonly ILogger<OfficeContextService> _logger;

    public OfficeContextService(GuardService guard, ILogger<OfficeContextService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public Task<(bool Ok, string? FilePath, string Summary)> InspectExcelSelectionAsync(CancellationToken ct = default)
        => RunStaAsync(() =>
        {
            dynamic? excel = TryGetActiveComObject("Excel.Application");
            if (excel is null)
                return (false, (string?)null, "No open Excel window detected.");

            try
            {
                dynamic? wb = excel.ActiveWorkbook;
                dynamic? ws = excel.ActiveSheet;
                if (wb is null || ws is null)
                    return (false, (string?)null, "No active Excel workbook or worksheet.");

                string fullName = SafeString(() => (string)wb.FullName);
                string sheetName = SafeString(() => (string)ws.Name);
                string selAddr = SafeString(() => (string)excel.Selection.Address[false, false]);

                var sb = new StringBuilder();
                sb.AppendLine($"Workbook: {fullName}");
                sb.AppendLine($"ActiveSheet: {sheetName}");
                sb.AppendLine($"Selection: {selAddr}");

                // Read header row (row 1, up to 15 non-empty cols) + up to 5 sample rows
                sb.AppendLine("Preview (top rows, max 12 cols):");
                for (int r = 1; r <= 6; r++)
                {
                    var rowVals = new List<string>();
                    bool any = false;
                    for (int c = 1; c <= 12; c++)
                    {
                        string v = SafeString(() => Convert.ToString(ws.Cells[r, c].Text) ?? "");
                        if (!string.IsNullOrEmpty(v)) any = true;
                        rowVals.Add(v);
                    }
                    if (!any && r > 1) break;
                    sb.AppendLine($"  Row {r}: " + string.Join(" | ", rowVals.Select(x => x.Replace('\n', ' '))));
                }

                // Also inspect active cell formula/value for debugging (#N/A, #REF!, etc.)
                try
                {
                    dynamic activeCell = excel.ActiveCell;
                    string acAddr = SafeString(() => (string)activeCell.Address[false, false]);
                    string acText = SafeString(() => Convert.ToString(activeCell.Text) ?? "");
                    string acFormula = SafeString(() => Convert.ToString(activeCell.Formula) ?? "");
                    sb.AppendLine($"ActiveCell {acAddr}: text=\"{acText}\", formula=\"{acFormula}\"");
                }
                catch { }

                return (true, fullName, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Excel inspect failed");
                return (false, (string?)null, $"Failed to read Excel: {ex.Message}");
            }
        }, ct);

    public Task<(bool Ok, string? FilePath, string Message)> WriteExcelCellsAsync(
        IReadOnlyList<(string CellAddress, string ValueOrFormula)> writes,
        CancellationToken ct = default)
        => RunStaAsync(() =>
        {
            foreach (var w in writes)
            {
                var check = _guard.CheckExcelFormula(w.ValueOrFormula);
                if (!check.Allowed)
                    return (false, (string?)null, check.Reason);
            }

            dynamic? excel = TryGetActiveComObject("Excel.Application");
            if (excel is null)
                return (false, (string?)null, "No open Excel window detected.");

            try
            {
                dynamic? wb = excel.ActiveWorkbook;
                dynamic? ws = excel.ActiveSheet;
                if (wb is null || ws is null)
                    return (false, (string?)null, "No active Excel workbook or worksheet.");

                string fullName = SafeString(() => (string)wb.FullName);
                string backupPath = CreateBackupPath(SafeString(() => (string)wb.Name), ".xlsx");
                try { wb.SaveCopyAs(backupPath); }
                catch (Exception ex)
                {
                    return (false, fullName, $"Auto-backup before write failed; write aborted: {ex.Message}");
                }

                foreach (var (addr, val) in writes)
                {
                    if (string.IsNullOrWhiteSpace(addr)) continue;
                    dynamic range = ws.Range[addr.Trim()];
                    range.Value = val;
                }

                return (true, fullName, $"Written {writes.Count} cell(s) (backup: {backupPath}).");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Excel write failed");
                return (false, (string?)null, $"Failed to write Excel: {ex.Message}");
            }
        }, ct);

    public Task<(bool Ok, string? FilePath, string Summary)> InspectPowerPointAsync(bool allSlides, CancellationToken ct = default)
        => RunStaAsync(() =>
        {
            dynamic? ppt = TryGetActiveComObject("PowerPoint.Application");
            if (ppt is null)
                return (false, (string?)null, "No open PowerPoint window detected.");

            try
            {
                dynamic? pres = ppt.ActivePresentation;
                if (pres is null)
                    return (false, (string?)null, "No active PowerPoint presentation.");

                string fullName = SafeString(() => (string)pres.FullName);
                int total = (int)pres.Slides.Count;
                int currentIdx = 1;
                try { currentIdx = (int)ppt.ActiveWindow.View.Slide.SlideIndex; } catch { }

                var sb = new StringBuilder();
                sb.AppendLine($"Presentation: {fullName} (TotalSlides={total}, CurrentSlide={currentIdx})");

                int start = allSlides ? 1 : currentIdx;
                int end = allSlides ? Math.Min(total, 25) : currentIdx;

                for (int i = start; i <= end; i++)
                {
                    dynamic slide = pres.Slides[i];
                    sb.AppendLine($"--- Slide {i} ---");
                    int shapeCount = (int)slide.Shapes.Count;
                    for (int s = 1; s <= shapeCount; s++)
                    {
                        try
                        {
                            dynamic shape = slide.Shapes[s];
                            if ((int)shape.HasTextFrame != 0 && (int)shape.TextFrame.HasText != 0)
                            {
                                string txt = SafeString(() => (string)shape.TextFrame.TextRange.Text).Replace("\r", " / ");
                                sb.AppendLine($"  [Shape {s}] {txt}");
                            }
                        }
                        catch { }
                    }

                    if (!allSlides)
                    {
                        try
                        {
                            string notes = SafeString(() => (string)slide.NotesPage.Shapes.Placeholders[2].TextFrame.TextRange.Text);
                            if (!string.IsNullOrWhiteSpace(notes))
                                sb.AppendLine($"  [Notes] {notes}");
                        }
                        catch { }
                    }
                }

                return (true, fullName, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PowerPoint inspect failed");
                return (false, (string?)null, $"Failed to read PowerPoint: {ex.Message}");
            }
        }, ct);

    public Task<(bool Ok, string? FilePath, string Message)> UpdatePowerPointSlideAsync(
        int slideIndex,
        int? shapeIndex,
        string? newText,
        string? newNotes,
        CancellationToken ct = default)
        => RunStaAsync(() =>
        {
            dynamic? ppt = TryGetActiveComObject("PowerPoint.Application");
            if (ppt is null)
                return (false, (string?)null, "No open PowerPoint window detected.");

            try
            {
                dynamic? pres = ppt.ActivePresentation;
                if (pres is null)
                    return (false, (string?)null, "No active PowerPoint presentation.");

                int total = (int)pres.Slides.Count;
                if (slideIndex < 1 || slideIndex > total)
                    return (false, (string?)null, $"Slide index out of range (1..{total}): {slideIndex}");

                string fullName = SafeString(() => (string)pres.FullName);
                string backupPath = CreateBackupPath(SafeString(() => (string)pres.Name), ".pptx");
                try { pres.SaveCopyAs(backupPath); }
                catch (Exception ex)
                {
                    return (false, fullName, $"Auto-backup before write failed; update aborted: {ex.Message}");
                }

                dynamic slide = pres.Slides[slideIndex];
                if (shapeIndex.HasValue && newText is not null)
                {
                    dynamic shape = slide.Shapes[shapeIndex.Value];
                    shape.TextFrame.TextRange.Text = newText;
                }

                if (newNotes is not null)
                {
                    slide.NotesPage.Shapes.Placeholders[2].TextFrame.TextRange.Text = newNotes;
                }

                return (true, fullName, $"Updated slide {slideIndex} (backup: {backupPath}).");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PowerPoint update failed");
                return (false, (string?)null, $"Failed to update PowerPoint: {ex.Message}");
            }
        }, ct);

    private static string CreateBackupPath(string fileName, string defaultExt)
    {
        Directory.CreateDirectory(AppPaths.OfficeBackupsDir);
        var stem = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(fileName) ? "untitled" : fileName);
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = defaultExt;
        return Path.Combine(AppPaths.OfficeBackupsDir, $"{stem}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}{ext}");
    }

    private static string SafeString(Func<string?> getter)
    {
        try { return getter() ?? ""; }
        catch { return ""; }
    }

    private static dynamic? TryGetActiveComObject(string progId)
    {
        try
        {
            if (CLSIDFromProgID(progId, out var clsid) != 0)
                return null;
            int hr = GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
            return hr == 0 ? obj : null;
        }
        catch
        {
            return null;
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> func, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                tcs.SetResult(func());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}
