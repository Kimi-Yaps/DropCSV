using System.ComponentModel.DataAnnotations;

namespace DropCSV.Models
{
    // Models/CsvUploadViewModel.cs
    public class CsvUploadViewModel
    {
        [Required]
        [Display(Name = "CSV File")]
        public IFormFile CsvFile { get; set; }

        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();
        public bool HasData => Headers.Any();
    }
}
