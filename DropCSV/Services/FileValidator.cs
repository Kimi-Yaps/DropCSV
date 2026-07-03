using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DropCSV.Services
{
    public class FileValidator : IFileValidator
    {
        private static readonly byte[] PngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] JpegHeader = new byte[] { 0xFF, 0xD8, 0xFF };
        private static readonly byte[] GifHeader = new byte[] { 0x47, 0x49, 0x46, 0x38 }; // "GIF8"
        private static readonly byte[] BmpHeader = new byte[] { 0x42, 0x4D }; // "BM"
        private static readonly byte[] PdfHeader = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // "%PDF"

        private const long MaxFileSizeInBytes = 35 * 1024 * 1024; // 35 MB

        public async Task<(bool IsValid, string ErrorMessage)> ValidateCsvAsync(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return (false, "File is empty.");
            }

            // Size limit check
            if (file.Length > MaxFileSizeInBytes)
            {
                return (false, "File size exceeds the 35 MB limit.");
            }

            // 1. Extension Check
            var extension = Path.GetExtension(file.FileName);
            if (string.IsNullOrEmpty(extension) || !extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Invalid file extension. Only .csv files are allowed.");
            }

            // 2. MIME Type Blacklist Check
            var contentType = file.ContentType?.ToLowerInvariant() ?? string.Empty;
            if (contentType.Contains("image/") || contentType.Contains("svg") || contentType == "application/xml" || contentType == "text/xml")
            {
                return (false, "Security validation failed: Image and SVG file formats are blacklisted.");
            }

            // Read the start of the file for binary signatures and XML/SVG check
            byte[] buffer = new byte[2048];
            int bytesRead;
            using (var stream = file.OpenReadStream())
            {
                bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            }

            if (bytesRead == 0)
            {
                return (false, "File is empty.");
            }

            // 3. Binary Magic Number Check
            if (MatchHeader(buffer, PngHeader) || 
                MatchHeader(buffer, JpegHeader) || 
                MatchHeader(buffer, GifHeader) || 
                MatchHeader(buffer, BmpHeader) || 
                MatchHeader(buffer, PdfHeader))
            {
                return (false, "Security validation failed: File signature matches a blacklisted binary format (Image/PDF).");
            }

            // Check for WebP signature: "RIFF" at offset 0, and "WEBP" at offset 8
            if (bytesRead >= 12 &&
                buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46 && // RIFF
                buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50) // WEBP
            {
                return (false, "Security validation failed: File signature matches WebP image format.");
            }

            // 4. Null Byte Check (Binary Detection)
            // A standard plain CSV file should not contain null bytes.
            int nullByteCheckLimit = Math.Min(bytesRead, 1024);
            for (int i = 0; i < nullByteCheckLimit; i++)
            {
                if (buffer[i] == 0x00)
                {
                    return (false, "Security validation failed: Binary content detected in CSV file.");
                }
            }

            // 5. XML / SVG content validation
            string headerText = GetCleanStringFromBuffer(buffer, bytesRead);
            string headerLower = headerText.ToLowerInvariant();

            // Detect SVG hallmarks
            if (headerLower.Contains("<svg") || 
                headerLower.Contains("xmlns=\"http://www.w3.org/2000/svg\"") || 
                headerLower.Contains("xmlns='http://www.w3.org/2000/svg'") ||
                headerLower.Contains("<!doctype svg") ||
                headerLower.StartsWith("<?xml"))
            {
                return (false, "Security validation failed: SVG/XML content detected. SVG files are blacklisted.");
            }

            return (true, string.Empty);
        }

        private bool MatchHeader(byte[] buffer, byte[] signature)
        {
            if (buffer.Length < signature.Length) return false;
            for (int i = 0; i < signature.Length; i++)
            {
                if (buffer[i] != signature[i]) return false;
            }
            return true;
        }

        private string GetCleanStringFromBuffer(byte[] buffer, int length)
        {
            // Skip BOMs if present
            int offset = 0;
            if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                offset = 3; // UTF-8 BOM
            }
            else if (length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
            {
                offset = 2; // UTF-16 BE BOM
            }
            else if (length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
            {
                offset = 2; // UTF-16 LE BOM
            }

            int count = length - offset;
            if (count <= 0) return string.Empty;

            try
            {
                // Fallback decode, usually UTF-8
                string text = Encoding.UTF8.GetString(buffer, offset, count);
                return text.Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
