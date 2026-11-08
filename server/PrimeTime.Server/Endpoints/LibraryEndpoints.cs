using PrimeTime.Server.Data;
using PrimeTime.Server.Jellyfin;
using PrimeTime.Server.Library;
using PrimeTime.Server.Tasks;

namespace PrimeTime.Server.Endpoints;

public sealed record LibrarySelectionRequest(List<string>? LibraryIds);

public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/libraries").RequireAuthorization();

        api.MapGet("/", async (LibraryService service, CancellationToken ct) =>
            Results.Ok(await service.GetOverviewAsync(ct)));

        // Saves the selection and starts a scan in the background.
        api.MapPut("/selection", async (
            LibrarySelectionRequest body, LibraryService service, LibraryStore libraries,
            TaskRunner runner, CancellationToken ct) =>
        {
            var trigger = libraries.AnySelected() ? "selection" : "initial";
            try
            {
                await service.ApplySelectionAsync(body.LibraryIds ?? [], ct);
            }
            catch (Exception ex) when (ex is LibrarySelectionException or JellyfinException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            // "Busy" here means the scheduler got in first, and that scan uses the new selection too.
            runner.TryStart(LibraryScanTask.TaskKey, trigger);
            return Results.Accepted();
        });
    }
}
