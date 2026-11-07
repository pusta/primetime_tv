namespace PrimeTime.Server;

/// <summary>Tiny HTTP probe used by the container HEALTHCHECK.</summary>
public static class HealthCheckCommand
{
    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0] ?? "8080";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
