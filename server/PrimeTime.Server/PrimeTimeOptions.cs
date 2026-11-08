namespace PrimeTime.Server;

/// <summary>Settings read from environment variables (PRIMETIME_*) or appsettings.json.</summary>
public sealed class PrimeTimeOptions
{
    public required string DataDir { get; init; }

    /// <summary>Where people reach this server, without a trailing slash. Null if not set.</summary>
    public string? PublicUrl { get; init; }

    public string DatabasePath => Path.Combine(DataDir, "primetime.db");

    public static PrimeTimeOptions FromConfiguration(IConfiguration configuration)
    {
        var dataDir = configuration["PRIMETIME_DATA_DIR"];
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            // Local `dotnet run` default; the container sets PRIMETIME_DATA_DIR=/data.
            dataDir = Path.Combine(Environment.CurrentDirectory, "local-data");
        }

        return new PrimeTimeOptions
        {
            DataDir = Path.GetFullPath(dataDir),
            PublicUrl = ParsePublicUrl(configuration["PRIMETIME_PUBLIC_URL"]),
        };
    }

    // A wrong address would send every QR code somewhere else, so stop at startup rather than run with it.
    private static string? ParsePublicUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"PRIMETIME_PUBLIC_URL must look like https://primetime.example.com, but it's: {text}");
        }

        return text.TrimEnd('/');
    }
}
