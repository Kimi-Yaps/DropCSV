using Microsoft.AspNetCore.Http;

namespace DropCSV.Services
{
    public interface IFileValidator
    {
        Task<(bool IsValid, string ErrorMessage)> ValidateCsvAsync(IFormFile file);
    }
}
