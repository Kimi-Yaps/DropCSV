using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DropCSV.Services
{
    public interface IClamScanService
    {
        Task<ClamScanResult> ScanStreamAsync(Stream stream, CancellationToken cancellationToken = default);
    }

    public class ClamScanResult
    {
        public ClamScanStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public string RawResponse { get; set; } = string.Empty;
    }

    public enum ClamScanStatus
    {
        Clean,
        VirusDetected,
        Error,
        CouldNotConnect
    }
}
