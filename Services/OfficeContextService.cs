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

    public Task<(bool Ok, string? FilePath, string Summary)> ReadExcelWorkbookAsync(
        string? filePath,
        string? sheetName,
        string? rangeAddress,
        bool listSheetsOnly,
        CancellationToken ct = default)
        => RunStaAsync(() =>
        {
            dynamic? excel = null;
            dynamic? wb = null;
            bool openedByUs = false;
            bool createdApp = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    if (!File.Exists(filePath))
                        return (false, (string?)null, $"Excel file not found: {filePath}");

                    excel = TryGetActiveComObject("Excel.Application");
                    if (excel is null)
                    {
                        var t = Type.GetTypeFromProgID("Excel.Application");
                        if (t is null) return (false, (string?)null, "Microsoft Excel is not installed on this machine.");
                        excel = Activator.CreateInstance(t);
                        if (excel is null) return (false, (string?)null, "Failed to start Excel COM instance.");
                        excel.Visible = false;
                        excel.DisplayAlerts = false;
                        createdApp = true;
                    }

                    var fullTarget = Path.GetFullPath(filePath);
                    try
                    {
                        foreach (dynamic openWb in excel.Workbooks)
                        {
                            if (string.Equals(SafeString(() => (string)openWb.FullName), fullTarget, StringComparison.OrdinalIgnoreCase))
                            {
                                wb = openWb;
                                break;
                            }
                        }
                    }
                    catch { }

                    if (wb is null)
                    {
                        wb = excel.Workbooks.Open(fullTarget, ReadOnly: true);
                        openedByUs = true;
                    }
                }
                else
                {
                    excel = TryGetActiveComObject("Excel.Application");
                    if (excel is null)
                        return (false, (string?)null, "No open Excel window detected (or pass file_path to open a .xlsx file).");
                    wb = excel.ActiveWorkbook;
                }

                if (wb is null)
                    return (false, (string?)null, "No active Excel workbook.");

                string fullName = SafeString(() => (string)wb.FullName);
                var sb = new StringBuilder();
                sb.AppendLine($"Workbook: {fullName}");

                var sheetNames = new List<string>();
                int count = (int)wb.Worksheets.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic s = wb.Worksheets[i];
                    sheetNames.Add(SafeString(() => (string)s.Name));
                }
                sb.AppendLine($"Sheets ({sheetNames.Count}): {string.Join(", ", sheetNames)}");

                if (listSheetsOnly)
                    return (true, fullName, sb.ToString());

                dynamic? ws = null;
                if (!string.IsNullOrWhiteSpace(sheetName))
                {
                    for (int i = 1; i <= count; i++)
                    {
                        dynamic s = wb.Worksheets[i];
                        if (string.Equals(SafeString(() => (string)s.Name), sheetName.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            ws = s;
                            break;
                        }
                    }
                    if (ws is null)
                        return (false, fullName, $"Worksheet '{sheetName}' not found. Available sheets: {string.Join(", ", sheetNames)}");
                }
                else
                {
                    ws = wb.ActiveSheet ?? wb.Worksheets[1];
                }

                string activeSheetName = SafeString(() => (string)ws.Name);
                dynamic targetRange = !string.IsNullOrWhiteSpace(rangeAddress)
                    ? ws.Range[rangeAddress.Trim()]
                    : ws.UsedRange;

                int startRow = (int)targetRange.Row;
                int startCol = (int)targetRange.Column;
                int rowCount = Math.Min((int)targetRange.Rows.Count, 60);
                int colCount = Math.Min((int)targetRange.Columns.Count, 20);
                sb.AppendLine($"Sheet: {activeSheetName} (showing {rowCount}x{colCount} from R{startRow}C{startCol})");

                for (int r = 0; r < rowCount; r++)
                {
                    var vals = new List<string>(colCount);
                    bool any = false;
                    for (int c = 0; c < colCount; c++)
                    {
                        string v = SafeString(() => Convert.ToString(ws.Cells[startRow + r, startCol + c].Text) ?? "");
                        if (!string.IsNullOrEmpty(v)) any = true;
                        vals.Add(v.Replace('\n', ' ').Replace('\r', ' '));
                    }
                    if (!any && r > 0) continue;
                    sb.AppendLine($"  Row {startRow + r}: {string.Join(" | ", vals)}");
                }

                return (true, fullName, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Excel read failed");
                return (false, (string?)null, $"Failed to read Excel: {ex.Message}");
            }
            finally
            {
                if (openedByUs && wb is not null)
                {
                    try { wb.Close(false); } catch { }
                }
                if (createdApp && excel is not null)
                {
                    try { excel.Quit(); } catch { }
                }
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

    public Task<(bool Ok, string? FilePath, string Summary)> InspectPowerPointAsync(bool allSlides, CancellationToken ct = default, string? targetFilePath = null)
        => RunStaAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(targetFilePath) && File.Exists(targetFilePath) &&
                Path.GetExtension(targetFilePath).Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var extracted = AttachmentExtractor.ExtractPptxText(targetFilePath);
                    if (!string.IsNullOrWhiteSpace(extracted))
                        return (true, Path.GetFullPath(targetFilePath), extracted);
                }
                catch { }
            }

            dynamic? ppt = TryGetActiveComObject("PowerPoint.Application");
            if (ppt is null)
                return (false, (string?)null, "No open PowerPoint window detected.");

            try
            {
                dynamic? pres = null;
                if (!string.IsNullOrWhiteSpace(targetFilePath))
                {
                    var targetName = Path.GetFileName(targetFilePath.Trim());
                    int presCount = (int)ppt.Presentations.Count;
                    for (int p = 1; p <= presCount; p++)
                    {
                        try
                        {
                            dynamic candidate = ppt.Presentations[p];
                            string cFull = SafeString(() => (string)candidate.FullName);
                            string cName = SafeString(() => (string)candidate.Name);
                            if (cFull.Equals(targetFilePath, StringComparison.OrdinalIgnoreCase) ||
                                cName.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                                cName.Contains(targetName, StringComparison.OrdinalIgnoreCase))
                            {
                                pres = candidate;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                pres ??= ppt.ActivePresentation;
                if (pres is null)
                    return (false, (string?)null, "No active PowerPoint presentation.");

                string fullName = SafeString(() => (string)pres.FullName);
                if (File.Exists(fullName) && Path.GetExtension(fullName).Equals(".pptx", StringComparison.OrdinalIgnoreCase) && allSlides)
                {
                    try
                    {
                        var openXmlSummary = AttachmentExtractor.ExtractPptxText(fullName);
                        if (!string.IsNullOrWhiteSpace(openXmlSummary))
                            return (true, fullName, openXmlSummary);
                    }
                    catch { }
                }

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
                            AppendComShapeText(sb, shape, $"Shape {s}");
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

    private static void AppendComShapeText(StringBuilder sb, dynamic shape, string label)
    {
        try
        {
            if ((int)shape.HasTextFrame != 0 && (int)shape.TextFrame.HasText != 0)
            {
                string txt = SafeString(() => (string)shape.TextFrame.TextRange.Text).Replace("\r", " / ").Replace("\v", " / ");
                if (!string.IsNullOrWhiteSpace(txt))
                    sb.AppendLine($"  [{label}] {txt}");
            }
        }
        catch { }

        try
        {
            if ((int)shape.HasTable != 0)
            {
                dynamic tbl = shape.Table;
                int rows = (int)tbl.Rows.Count;
                int cols = (int)tbl.Columns.Count;
                sb.AppendLine($"  [{label} Table ({rows}x{cols})]");
                for (int r = 1; r <= rows; r++)
                {
                    var cells = new List<string>();
                    for (int c = 1; c <= cols; c++)
                    {
                        string cellTxt = SafeString(() => (string)tbl.Cell(r, c).Shape.TextFrame.TextRange.Text)
                            .Replace("\r", " ").Replace("\n", " ").Replace("\v", " ").Trim();
                        cells.Add(cellTxt);
                    }
                    if (cells.Any(x => !string.IsNullOrWhiteSpace(x)))
                        sb.AppendLine($"    | {string.Join(" | ", cells)} |");
                }
            }
        }
        catch { }

        try
        {
            // msoGroup == 6
            if ((int)shape.Type == 6)
            {
                int gCount = (int)shape.GroupItems.Count;
                for (int g = 1; g <= gCount; g++)
                {
                    AppendComShapeText(sb, shape.GroupItems[g], $"{label}.{g}");
                }
            }
        }
        catch { }
    }

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
