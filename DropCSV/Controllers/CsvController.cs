using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using DropCSV.Models;

namespace DropCSV.Controllers
{
    public class CsvController : Controller
    {
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

            if (!model.CsvFile.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError("CsvFile", "Only CSV files are allowed.");
                return View(model);
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

            model.Headers = headers;
            model.Rows = rows;
            return View(model);
        }

        // ── Save selected rows to session ───────────────────
        [HttpPost]
        public IActionResult SavePicked([FromBody] SavePayload payload)
        {
            if (payload == null || !payload.Headers.Any())
                return BadRequest(new { error = "No data provided." });

            var vm = new SavedDataViewModel
            {
                Headers = payload.Headers,
                Rows = payload.Rows,
                SavedAt = DateTime.Now
            };

            HttpContext.Session.SetString("SavedData", JsonSerializer.Serialize(vm));
            return Ok(new { redirect = Url.Action("Saved", "Csv") });
        }

        // ── Saved / picked data page ─────────────────────────
        [HttpGet]
        public IActionResult Saved()
        {
            var json = HttpContext.Session.GetString("SavedData");
            if (string.IsNullOrEmpty(json))
                return View(new SavedDataViewModel());

            var vm = JsonSerializer.Deserialize<SavedDataViewModel>(json) ?? new SavedDataViewModel();
            return View(vm);
        }

        // ── Clear saved data ─────────────────────────────────
        [HttpPost]
        public IActionResult ClearSaved()
        {
            HttpContext.Session.Remove("SavedData");
            return RedirectToAction("Saved");
        }
    }
}