using System.Collections.Generic;
using System.Threading.Tasks;
using DropCSV.Models;

namespace DropCSV.Services
{
    public interface ICsvStorageService
    {
        Task InitializeSchemaAsync();
        Task SaveCsvAsync(string filename, List<string> headers, List<List<string>> rows);
        Task<SavedDataViewModel?> GetLatestSavedCsvAsync();
        Task UpdateRowsAsync(List<List<string>> rows);
        Task ClearAllSavedAsync();
    }
}
