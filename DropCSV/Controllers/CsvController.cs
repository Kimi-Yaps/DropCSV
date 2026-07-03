using CsvHelper;
using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using DropCSV.Models;
using DropCSV.Services;
using Microsoft.Extensions.Logging;

namespace DropCSV.Controllers
{
    public class CsvController : Controller
    {
        private readonly IFileValidator _fileValidator;
        private readonly IClamScanService _clamScanService;
        private readonly ICsvStorageService _csvStorage;
        private readonly IMalwareAlertService _malwareAlerts;
        private readonly ILogger<CsvController> _logger;

        public CsvController(
            IFileValidator fileValidator, 
            IClamScanService clamScanService, 
            ICsvStorageService csvStorage, 
            IMalwareAlertService malwareAlerts, 
            ILogger<CsvController> logger)
        {
            _fileValidator = fileValidator;
            _clamScanService = clamScanService;
            _csvStorage = csvStorage;
            _malwareAlerts = malwareAlerts;
            _logger = logger;
        }

        // ── Upload ──────────────────────────────────────────
        [HttpGet]
        public IActionResult Upload()
        {
            return View(new CsvUploadViewModel());
        }

        [HttpPost]
        public async Task<IActionResult> Upload(CsvUploadViewModel model)
        {
            if (model.CsvFile == null || model.CsvFile.Length == 0)
            {
                ModelState.AddModelError("CsvFile", "Please select a CSV file.");
                return View(model);
            }

            // 1. File Validation (extension, MIME, magic numbers, SVG/XML, binary checks, and 35MB limit)
            var validationResult = await _fileValidator.ValidateCsvAsync(model.CsvFile);
            if (!validationResult.IsValid)
            {
                ModelState.AddModelError("CsvFile", validationResult.ErrorMessage);
                return View(model);
            }

            // 2. Malware Scanning with ClamAV
            using (var scanStream = model.CsvFile.OpenReadStream())
            {
                var scanResult = await _clamScanService.ScanStreamAsync(scanStream);
                if (scanResult.Status == ClamScanStatus.VirusDetected)
                {
                    // Server-side console log
                    Console.WriteLine($"[SECURITY WARNING] Malware detected in file {model.CsvFile.FileName}: {scanResult.Message}");
                    _logger.LogWarning("Malware detected in file {FileName}: {Message}", model.CsvFile.FileName, scanResult.Message);

                    // LOG ALERT TO DATABASE
                    await _malwareAlerts.LogAlertAsync(model.CsvFile.FileName, model.CsvFile.Length, scanResult.Message);

                    // Client-side console log indicators
                    ViewBag.MalwareDetected = true;
                    ViewBag.MalwareMessage = scanResult.Message;

                    ModelState.AddModelError("CsvFile", $"Security validation failed: {scanResult.Message}");
                    return View(model);
                }
                else if (scanResult.Status == ClamScanStatus.Error || scanResult.Status == ClamScanStatus.CouldNotConnect)
                {
                    // Fail-closed
                    _logger.LogError("ClamAV scan failed: {Message}", scanResult.Message);
                    ModelState.AddModelError("CsvFile", $"Security scan service failed/unreachable: {scanResult.Message}");
                    return View(model);
                }
            }

            var headers = new List<string>();
            var rows = new List<List<string>>();

            using var stream = new StreamReader(model.CsvFile.OpenReadStream());
            using var csv = new CsvReader(stream, new CsvConfiguration(CultureInfo.InvariantCulture));

            await csv.ReadAsync();
            csv.ReadHeader();
            headers = csv.HeaderRecord?.ToList() ?? new List<string>();

            while (await csv.ReadAsync())
            {
                var row = new List<string>();
                foreach (var header in headers)
                    row.Add(csv.GetField(header) ?? "");
                rows.Add(row);
            }

            // Check for duplicates
            var dbData = await _csvStorage.GetLatestSavedCsvAsync();
            var dbHeaders = dbData?.Headers ?? new List<string>();
            var dbRows = dbData?.Rows ?? new List<List<string>>();

            var compareViewModel = DetectDuplicates(headers, rows, dbHeaders, dbRows, model.UploadMode, new List<string>());

            if (compareViewModel.DuplicateGroups.Any())
            {
                var temp = new TempUploadData
                {
                    Filename = model.CsvFile.FileName,
                    Headers = headers,
                    Rows = rows,
                    UploadMode = model.UploadMode
                };
                HttpContext.Session.SetString("TempUploadData", JsonSerializer.Serialize(temp));
                return RedirectToAction("Compare");
            }
            else
            {
                var mergedHeaders = model.UploadMode == "merge"
                    ? dbHeaders.Union(headers, StringComparer.OrdinalIgnoreCase).ToList()
                    : headers;

                var finalRows = new List<List<string>>();
                if (model.UploadMode == "merge")
                {
                    foreach (var dbRow in dbRows)
                        finalRows.Add(MapRow(dbRow, dbHeaders, mergedHeaders));
                }

                foreach (var row in rows)
                    finalRows.Add(MapRow(row, headers, mergedHeaders));

                await _csvStorage.SaveCsvAsync(model.CsvFile.FileName, mergedHeaders, finalRows);

                var savedVM = new SavedDataViewModel
                {
                    Headers = mergedHeaders,
                    Rows = finalRows,
                    SavedAt = DateTime.Now
                };
                HttpContext.Session.SetString("SavedData", JsonSerializer.Serialize(savedVM));

                return RedirectToAction("Saved");
            }
        }

        // ── Compare Duplicates Page ──────────────────────────
        [HttpGet]
        public async Task<IActionResult> Compare(string? compareCols)
        {
            var json = HttpContext.Session.GetString("TempUploadData");
            if (string.IsNullOrEmpty(json))
                return RedirectToAction("Upload");

            var tempUpload = JsonSerializer.Deserialize<TempUploadData>(json);
            if (tempUpload == null)
                return RedirectToAction("Upload");

            var dbData = await _csvStorage.GetLatestSavedCsvAsync();
            var dbHeaders = dbData?.Headers ?? new List<string>();
            var dbRows = dbData?.Rows ?? new List<List<string>>();

            var selectedCompareColumns = new List<string>();
            if (!string.IsNullOrEmpty(compareCols))
            {
                selectedCompareColumns = compareCols.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            }

            var compareModel = DetectDuplicates(
                tempUpload.Headers, 
                tempUpload.Rows, 
                dbHeaders, 
                dbRows, 
                tempUpload.UploadMode, 
                selectedCompareColumns
            );

            return View(compareModel);
        }

        // ── Save Resolved Data from Compare ──────────────────
        [HttpPost]
        public async Task<IActionResult> SaveResolved([FromBody] ResolvePayload payload)
        {
            var json = HttpContext.Session.GetString("TempUploadData");
            if (string.IsNullOrEmpty(json))
                return BadRequest(new { error = "Session expired or no upload data found. Please upload again." });

            var tempUpload = JsonSerializer.Deserialize<TempUploadData>(json);
            if (tempUpload == null)
                return BadRequest(new { error = "Invalid upload data." });

            var dbData = await _csvStorage.GetLatestSavedCsvAsync();
            var dbHeaders = dbData?.Headers ?? new List<string>();
            var dbRows = dbData?.Rows ?? new List<List<string>>();

            var mergedHeaders = tempUpload.UploadMode == "merge"
                ? dbHeaders.Union(tempUpload.Headers, StringComparer.OrdinalIgnoreCase).ToList()
                : tempUpload.Headers.ToList();

            var finalRows = new List<List<string>>();

            var compareCols = tempUpload.Headers; // Default to all headers
            var keysToUse = tempUpload.Headers;

            var uploadKeys = new Dictionary<string, List<int>>();
            for (int i = 0; i < tempUpload.Rows.Count; i++)
            {
                var key = GetRowKey(tempUpload.Rows[i], tempUpload.Headers, keysToUse);
                if (!uploadKeys.ContainsKey(key)) uploadKeys[key] = new List<int>();
                uploadKeys[key].Add(i);
            }

            var dbKeys = new Dictionary<string, List<int>>();
            if (tempUpload.UploadMode == "merge")
            {
                for (int i = 0; i < dbRows.Count; i++)
                {
                    var key = GetRowKey(dbRows[i], dbHeaders, keysToUse);
                    if (!dbKeys.ContainsKey(key)) dbKeys[key] = new List<int>();
                    dbKeys[key].Add(i);
                }
            }

            var allKeys = uploadKeys.Keys.Union(dbKeys.Keys).ToList();
            foreach (var key in allKeys)
            {
                var upIndices = uploadKeys.ContainsKey(key) ? uploadKeys[key] : new List<int>();
                var dbIndices = dbKeys.ContainsKey(key) ? dbKeys[key] : new List<int>();

                var totalCount = upIndices.Count + dbIndices.Count;
                if (totalCount == 1)
                {
                    if (upIndices.Any())
                    {
                        var row = tempUpload.Rows[upIndices.First()];
                        finalRows.Add(MapRow(row, tempUpload.Headers, mergedHeaders));
                    }
                    else if (dbIndices.Any())
                    {
                        var row = dbRows[dbIndices.First()];
                        finalRows.Add(MapRow(row, dbHeaders, mergedHeaders));
                    }
                }
            }

            if (payload.KeepItems != null)
            {
                foreach (var item in payload.KeepItems)
                {
                    if (item.Source == "Database" && tempUpload.UploadMode == "merge")
                    {
                        if (item.OriginalIndex >= 0 && item.OriginalIndex < dbRows.Count)
                        {
                            var row = dbRows[item.OriginalIndex];
                            finalRows.Add(MapRow(row, dbHeaders, mergedHeaders));
                        }
                    }
                    else if (item.Source == "Upload")
                    {
                        if (item.OriginalIndex >= 0 && item.OriginalIndex < tempUpload.Rows.Count)
                        {
                            var row = tempUpload.Rows[item.OriginalIndex];
                            finalRows.Add(MapRow(row, tempUpload.Headers, mergedHeaders));
                        }
                    }
                }
            }

            await _csvStorage.SaveCsvAsync(tempUpload.Filename, mergedHeaders, finalRows);

            var savedVM = new SavedDataViewModel
            {
                Headers = mergedHeaders,
                Rows = finalRows,
                SavedAt = DateTime.Now
            };
            HttpContext.Session.SetString("SavedData", JsonSerializer.Serialize(savedVM));
            HttpContext.Session.Remove("TempUploadData");

            return Ok(new { redirect = Url.Action("Saved", "Csv") });
        }

        // ── CRUD: Add Row ────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> AddRow([FromBody] AddRowPayload payload)
        {
            if (payload == null)
                return BadRequest(new { error = "Invalid request." });

            var dbData = await _csvStorage.GetLatestSavedCsvAsync();
            if (dbData == null)
                return BadRequest(new { error = "No active database CSV dataset found." });

            dbData.Rows.Add(payload.RowData);
            await _csvStorage.UpdateRowsAsync(dbData.Rows);

            HttpContext.Session.SetString("SavedData", JsonSerializer.Serialize(dbData));

            return Ok(new { success = true });
        }

        // ── CRUD: Update Row ─────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> UpdateRow([FromBody] UpdateRowPayload payload)
        {
            if (payload == null || payload.RowIndex < 0)
                return BadRequest(new { error = "Invalid request." });

            var dbData = await _csvStorage.GetLatestSavedCsvAsync();
            if (dbData == null || payload.RowIndex >= dbData.Rows.Count)
                return BadRequest(new { error = "Row not found." });

            dbData.Rows[payload.RowIndex] = payload.RowData;
            await _csvStorage.UpdateRowsAsync(dbData.Rows);

            HttpContext.Session.SetString("SavedData", JsonSerializer.Serialize(dbData));

            return Ok(new { success = true });
        }

        // ── CRUD: Delete Row ─────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> DeleteRow([FromBody] DeleteRowPayload payload)
        {
            if (payload == null || payload.RowIndex < 0)
                return BadRequest(new { error = "Invalid request." });

            var dbData = await _csvStorage.GetLatestSavedCsvAsync();
            if (dbData == null || payload.RowIndex >= dbData.Rows.Count)
                return BadRequest(new { error = "Row not found." });

            dbData.Rows.RemoveAt(payload.RowIndex);
            await _csvStorage.UpdateRowsAsync(dbData.Rows);

            HttpContext.Session.SetString("SavedData", JsonSerializer.Serialize(dbData));

            return Ok(new { success = true });
        }

        // ── CRUD: Bulk Delete Rows (For Duplicates Cleanup) ──
        [HttpPost]
        public async Task<IActionResult> DeleteRows([FromBody] DeleteRowsPayload payload)
        {
            if (payload == null || payload.RowIndices == null || !payload.RowIndices.Any())
                return BadRequest(new { error = "Invalid request." });

            var dbData = await _csvStorage.GetLatestSavedCsvAsync();
            if (dbData == null)
                return BadRequest(new { error = "No active database CSV dataset found." });

            var sortedIndices = payload.RowIndices.OrderByDescending(i => i).ToList();
            foreach (var idx in sortedIndices)
            {
                if (idx >= 0 && idx < dbData.Rows.Count)
                {
                    dbData.Rows.RemoveAt(idx);
                }
            }

            await _csvStorage.UpdateRowsAsync(dbData.Rows);
            HttpContext.Session.SetString("SavedData", JsonSerializer.Serialize(dbData));

            return Ok(new { success = true });
        }

        // ── Saved / picked data page ─────────────────────────
        [HttpGet]
        public async Task<IActionResult> Saved()
        {
            var vm = await _csvStorage.GetLatestSavedCsvAsync();
            if (vm == null)
            {
                var json = HttpContext.Session.GetString("SavedData");
                if (string.IsNullOrEmpty(json))
                    return View(new SavedDataViewModel());

                vm = JsonSerializer.Deserialize<SavedDataViewModel>(json) ?? new SavedDataViewModel();
            }

            return View(vm);
        }

        // ── Clear saved data ─────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> ClearSaved()
        {
            await _csvStorage.ClearAllSavedAsync();
            HttpContext.Session.Remove("SavedData");
            return RedirectToAction("Saved");
        }

        // ── Admin Dashboard ──────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Admin()
        {
            var alerts = await _malwareAlerts.GetAlertsAsync();
            return View(alerts);
        }

        // ── Clear Admin Alerts ───────────────────────────────
        [HttpPost]
        public async Task<IActionResult> ClearAlerts()
        {
            await _malwareAlerts.ClearAlertsAsync();
            return RedirectToAction("Admin");
        }

        // ── Helpers for Duplicate Detection ──────────────────
        private CompareViewModel DetectDuplicates(
            List<string> uploadHeaders, 
            List<List<string>> uploadRows, 
            List<string> dbHeaders, 
            List<List<string>> dbRows, 
            string uploadMode, 
            List<string> compareColumns)
        {
            var model = new CompareViewModel
            {
                Headers = uploadHeaders,
                UploadMode = uploadMode,
                AvailableColumns = uploadHeaders.ToList(),
                SelectedCompareColumns = compareColumns.ToList()
            };

            var keysToUse = compareColumns.Any() ? compareColumns : uploadHeaders;
            model.SelectedCompareColumns = keysToUse.ToList();

            var groups = new Dictionary<string, List<DuplicateRowItem>>();

            if (uploadMode == "merge" && dbRows != null && dbRows.Any())
            {
                for (int i = 0; i < dbRows.Count; i++)
                {
                    var dbRow = dbRows[i];
                    var mappedRow = MapRow(dbRow, dbHeaders, uploadHeaders);
                    var key = GetRowKey(dbRow, dbHeaders, keysToUse);

                    if (!groups.ContainsKey(key))
                        groups[key] = new List<DuplicateRowItem>();

                    groups[key].Add(new DuplicateRowItem
                    {
                        Source = "Database",
                        OriginalIndex = i,
                        RowData = mappedRow
                    });
                }
            }

            for (int i = 0; i < uploadRows.Count; i++)
            {
                var uploadRow = uploadRows[i];
                var key = GetRowKey(uploadRow, uploadHeaders, keysToUse);

                if (!groups.ContainsKey(key))
                    groups[key] = new List<DuplicateRowItem>();

                groups[key].Add(new DuplicateRowItem
                {
                    Source = "Upload",
                    OriginalIndex = i,
                    RowData = uploadRow.ToList()
                });
            }

            foreach (var pair in groups)
            {
                if (pair.Value.Count > 1)
                {
                    model.DuplicateGroups.Add(new DuplicateGroup
                    {
                        Key = pair.Key,
                        Items = pair.Value
                    });
                }
                else
                {
                    model.NonDuplicates.Add(pair.Value.First().RowData);
                }
            }

            return model;
        }

        private string GetRowKey(List<string> row, List<string> headers, List<string> compareColumns)
        {
            var values = new List<string>();
            foreach (var col in compareColumns)
            {
                var idx = headers.FindIndex(h => h.Equals(col, StringComparison.OrdinalIgnoreCase));
                values.Add(idx >= 0 && idx < row.Count ? row[idx].Trim().ToLowerInvariant() : "");
            }
            return string.Join("||", values);
        }

        private List<string> MapRow(List<string> row, List<string> sourceHeaders, List<string> targetHeaders)
        {
            var mapped = new List<string>();
            foreach (var th in targetHeaders)
            {
                var idx = sourceHeaders.FindIndex(sh => sh.Equals(th, StringComparison.OrdinalIgnoreCase));
                mapped.Add(idx >= 0 && idx < row.Count ? row[idx] : "");
            }
            return mapped;
        }
    }
}