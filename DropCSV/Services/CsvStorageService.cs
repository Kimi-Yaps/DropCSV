using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using DropCSV.Models;

namespace DropCSV.Services
{
    public class CsvStorageService : ICsvStorageService
    {
        private readonly string _connectionString;
        private readonly ILogger<CsvStorageService> _logger;

        public CsvStorageService(IConfiguration configuration, ILogger<CsvStorageService> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") 
                ?? "Server=localhost;Port=3306;Database=dropcsv_db;Uid=root;Pwd=root;";
            _logger = logger;
        }

        public async Task InitializeSchemaAsync()
        {
            try
            {
                using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    CREATE TABLE IF NOT EXISTS saved_csv (
                        id INT AUTO_INCREMENT PRIMARY KEY,
                        filename VARCHAR(255) NOT NULL,
                        headers TEXT NOT NULL,
                        `rows` LONGTEXT NOT NULL,
                        saved_at DATETIME NOT NULL
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

                using var command = new MySqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("Database table 'saved_csv' initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize table 'saved_csv' in MySQL.");
                throw;
            }
        }

        public async Task SaveCsvAsync(string filename, List<string> headers, List<List<string>> rows)
        {
            try
            {
                using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    INSERT INTO saved_csv (filename, headers, `rows`, saved_at)
                    VALUES (@filename, @headers, @rows, @saved_at);";

                using var command = new MySqlCommand(sql, connection);
                command.Parameters.AddWithValue("@filename", filename);
                command.Parameters.AddWithValue("@headers", JsonSerializer.Serialize(headers));
                command.Parameters.AddWithValue("@rows", JsonSerializer.Serialize(rows));
                command.Parameters.AddWithValue("@saved_at", DateTime.Now);

                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("CSV file '{Filename}' saved successfully to MySQL.", filename);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save CSV data to MySQL database.");
                throw;
            }
        }

        public async Task<SavedDataViewModel?> GetLatestSavedCsvAsync()
        {
            try
            {
                using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();

                var sql = "SELECT headers, `rows`, saved_at FROM saved_csv ORDER BY saved_at DESC LIMIT 1;";
                using var command = new MySqlCommand(sql, connection);
                using var reader = await command.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    var headersJson = reader.GetString(0);
                    var rowsJson = reader.GetString(1);
                    var savedAt = reader.GetDateTime(2);

                    var headers = JsonSerializer.Deserialize<List<string>>(headersJson) ?? new List<string>();
                    var rows = JsonSerializer.Deserialize<List<List<string>>>(rowsJson) ?? new List<List<string>>();

                    return new SavedDataViewModel
                    {
                        Headers = headers,
                        Rows = rows,
                        SavedAt = savedAt
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch latest saved CSV from MySQL.");
                return null;
            }
        }

        public async Task UpdateRowsAsync(List<List<string>> rows)
        {
            try
            {
                using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();

                // Find the ID of the latest saved CSV
                var idSql = "SELECT id FROM saved_csv ORDER BY saved_at DESC LIMIT 1;";
                int? latestId = null;
                using (var cmd = new MySqlCommand(idSql, connection))
                {
                    var result = await cmd.ExecuteScalarAsync();
                    if (result != null && result != DBNull.Value)
                    {
                        latestId = Convert.ToInt32(result);
                    }
                }

                if (latestId.HasValue)
                {
                    var updateSql = "UPDATE saved_csv SET `rows` = @rows, saved_at = @saved_at WHERE id = @id;";
                    using var command = new MySqlCommand(updateSql, connection);
                    command.Parameters.AddWithValue("@rows", JsonSerializer.Serialize(rows));
                    command.Parameters.AddWithValue("@saved_at", DateTime.Now);
                    command.Parameters.AddWithValue("@id", latestId.Value);
                    await command.ExecuteNonQueryAsync();
                    _logger.LogInformation("Saved CSV rows updated successfully in MySQL for ID {Id}.", latestId.Value);
                }
                else
                {
                    _logger.LogWarning("No saved CSV found to update rows.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update saved CSV rows in MySQL.");
                throw;
            }
        }

        public async Task ClearAllSavedAsync()
        {
            try
            {
                using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();

                var sql = "TRUNCATE TABLE saved_csv;";
                using var command = new MySqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("All saved CSV records cleared from MySQL.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to truncate 'saved_csv' table.");
                throw;
            }
        }
    }
}
