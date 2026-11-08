namespace PrimeTime.Server;

/// <summary>Settings read from environment variables (PRIMETIME_*) or appsettings.json.</summary>
public sealed class PrimeTimeOptions
{
    public required string DataDir { get; init; }

    public string DatabasePath => Path.Combine(DataDir, "primetime.db");

    public static PrimeTimeOptions FromConfiguration(IConfiguration configuration)
    {
        var dataDir = configuration["PRIMETIME_DATA_DIR"];
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            // Local `dotnet run` default; the container sets PRIMETIME_DATA_DIR=/data.
            dataDir = Path.Combine(Environment.CurrentDirectory, "local-data");
        }

        return new PrimeTimeOptions { DataDir = Path.GetFullPath(dataDir) };
    }
}
