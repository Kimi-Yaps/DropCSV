namespace DropCSV.Models
{
    public class SavedDataViewModel
    {
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();
        public bool HasData => Headers.Any() && Rows.Any();
        public DateTime SavedAt { get; set; } = DateTime.Now;
    }

    /// <summary>Used to receive the JSON POST from the browser.</summary>
    public class SavePayload
    {
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();
    }
}
