using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DropCSV.Services
{
    public class ClamScanService : IClamScanService
    {
        private readonly string _host;
        private readonly int _port;
        private readonly ILogger<ClamScanService> _logger;

        public ClamScanService(IConfiguration configuration, ILogger<ClamScanService> logger)
        {
            _host = configuration["ClamAV:Host"] ?? "localhost";
            _port = int.TryParse(configuration["ClamAV:Port"], out var port) ? port : 3310;
            _logger = logger;
        }

        public async Task<ClamScanResult> ScanStreamAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Connecting to ClamAV at {Host}:{Port}...", _host, _port);
                using var tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(_host, _port, cancellationToken);
                using var networkStream = tcpClient.GetStream();

                // Send INSTREAM command
                // zINSTREAM\0 is null-terminated, which is preferred for ClamAV's stream commands
                byte[] command = Encoding.ASCII.GetBytes("zINSTREAM\0");
                await networkStream.WriteAsync(command, 0, command.Length, cancellationToken);

                byte[] buffer = new byte[8192];
                int bytesRead;

                // Stream chunks
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    // Chunk size: 4 bytes, big endian (network byte order)
                    byte[] chunkSize = BitConverter.GetBytes(bytesRead);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(chunkSize);
                    }

                    await networkStream.WriteAsync(chunkSize, 0, chunkSize.Length, cancellationToken);
                    await networkStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                }

                // End stream indicator: 4-byte 0 length chunk
                byte[] endStream = new byte[] { 0, 0, 0, 0 };
                await networkStream.WriteAsync(endStream, 0, endStream.Length, cancellationToken);
                await networkStream.FlushAsync(cancellationToken);

                // Read response
                using var reader = new StreamReader(networkStream, Encoding.ASCII);
                string response = await reader.ReadToEndAsync(cancellationToken);
                response = response.Trim('\0', '\r', '\n');

                _logger.LogInformation("ClamAV scan response: '{Response}'", response);

                if (response.Contains("OK", StringComparison.OrdinalIgnoreCase))
                {
                    return new ClamScanResult 
                    { 
                        Status = ClamScanStatus.Clean, 
                        Message = "File is clean." 
                    };
                }
                else if (response.Contains("FOUND", StringComparison.OrdinalIgnoreCase))
                {
                    // Format is usually: "stream: <threat name> FOUND"
                    string threatName = response.Replace("stream:", "", StringComparison.OrdinalIgnoreCase)
                                                .Replace("FOUND", "", StringComparison.OrdinalIgnoreCase)
                                                .Trim();
                    return new ClamScanResult 
                    { 
                        Status = ClamScanStatus.VirusDetected, 
                        Message = $"Threat detected: {threatName}", 
                        RawResponse = response 
                    };
                }
                else
                {
                    return new ClamScanResult 
                    { 
                        Status = ClamScanStatus.Error, 
                        Message = $"ClamAV reported error/unknown status: {response}", 
                        RawResponse = response 
                    };
                }
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "Failed to connect to ClamAV at {Host}:{Port}", _host, _port);
                return new ClamScanResult 
                { 
                    Status = ClamScanStatus.CouldNotConnect, 
                    Message = "Could not connect to the malware scanner service." 
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during ClamAV scan.");
                return new ClamScanResult 
                { 
                    Status = ClamScanStatus.Error, 
                    Message = $"Scanning error: {ex.Message}" 
                };
            }
        }
    }
}
