using MySqlConnector;
using DropCSV.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel and Form upload limits to 35 MB
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 36700160; // 35 MB
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 36700160; // 35 MB
});

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Register file validation, malware scanning, and storage services
builder.Services.AddScoped<IFileValidator, FileValidator>();
builder.Services.AddScoped<IClamScanService, ClamScanService>();
builder.Services.AddScoped<ICsvStorageService, CsvStorageService>();
builder.Services.AddScoped<IMalwareAlertService, MalwareAlertService>();

var app = builder.Build();

// Auto-create database and tables on startup
using (var scope = app.Services.CreateScope())
{
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var connString = config.GetConnectionString("DefaultConnection") 
        ?? "Server=localhost;Port=3306;Database=dropcsv_db;Uid=root;Pwd=root;";

    try
    {
        // Connect to server without database first to ensure it exists
        var builderStr = new MySqlConnectionStringBuilder(connString)
        {
            Database = ""
        };

        logger.LogInformation("Connecting to MySQL server to check/create database...");
        using (var connection = new MySqlConnection(builderStr.ConnectionString))
        {
            await connection.OpenAsync();
            using (var command = new MySqlCommand("CREATE DATABASE IF NOT EXISTS dropcsv_db;", connection))
            {
                await command.ExecuteNonQueryAsync();
            }
        }

        // Initialize table schemas
        var csvStorage = scope.ServiceProvider.GetRequiredService<ICsvStorageService>();
        var alertService = scope.ServiceProvider.GetRequiredService<IMalwareAlertService>();

        await csvStorage.InitializeSchemaAsync();
        await alertService.InitializeSchemaAsync();
        logger.LogInformation("Database auto-initialization completed successfully.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred during database auto-initialization.");
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseSession();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
