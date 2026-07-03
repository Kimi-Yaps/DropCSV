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

    public class TempUploadData
    {
        public string Filename { get; set; } = "";
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();
        public string UploadMode { get; set; } = "merge";
    }

    public class CompareViewModel
    {
        public List<string> Headers { get; set; } = new();
        public string UploadMode { get; set; } = "merge";
        public List<string> AvailableColumns { get; set; } = new();
        public List<string> SelectedCompareColumns { get; set; } = new();
        public List<List<string>> NonDuplicates { get; set; } = new();
        public List<DuplicateGroup> DuplicateGroups { get; set; } = new();
    }

    public class DuplicateGroup
    {
        public string Key { get; set; } = "";
        public List<DuplicateRowItem> Items { get; set; } = new();
    }

    public class DuplicateRowItem
    {
        public string Source { get; set; } = ""; // "Upload" or "Database"
        public int OriginalIndex { get; set; }
        public List<string> RowData { get; set; } = new();
    }

    public class ResolvePayload
    {
        public List<KeepItem> KeepItems { get; set; } = new();
    }

    public class KeepItem
    {
        public string Source { get; set; } = ""; // "Upload" or "Database"
        public int OriginalIndex { get; set; }
    }

    public class UpdateRowPayload
    {
        public int RowIndex { get; set; }
        public List<string> RowData { get; set; } = new();
    }

    public class DeleteRowPayload
    {
        public int RowIndex { get; set; }
    }

    public class AddRowPayload
    {
        public List<string> RowData { get; set; } = new();
    }

    public class DeleteRowsPayload
    {
        public List<int> RowIndices { get; set; } = new();
    }
}
