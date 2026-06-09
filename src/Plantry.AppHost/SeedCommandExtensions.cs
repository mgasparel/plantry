using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Adds "Seed demo data" and "Reset &amp; re-seed" commands to a resource in the Aspire dashboard.
/// The commands POST to the dev-only /Dev/Seed and /Dev/Reset endpoints in the web app.
/// </summary>
public static class SeedCommandExtensions
{
    public static IResourceBuilder<ProjectResource> WithSeedCommands(
        this IResourceBuilder<ProjectResource> builder)
    {
        builder.WithCommand(
            name: "seed-data",
            displayName: "Seed demo data",
            executeCommand: ctx => InvokeAsync(builder, ctx, "/Dev/Seed"),
            commandOptions: new CommandOptions
            {
                UpdateState = ctx => IsRunning(ctx),
                Description = $"Seed a demo household (demo@plantry.dev / demo1234) and two random households. Idempotent — no-ops if already seeded.",
                IconName = "DatabaseArrowDown",
                IsHighlighted = true
            });

        builder.WithCommand(
            name: "reset-seed",
            displayName: "Reset & re-seed",
            executeCommand: ctx => InvokeAsync(builder, ctx, "/Dev/Reset"),
            commandOptions: new CommandOptions
            {
                UpdateState = ctx => IsRunning(ctx),
                Description = "Delete all households, users, and catalog data, then re-seed from scratch.",
                ConfirmationMessage = "This will delete ALL households, users, and catalog data in the dev database. Re-seed afterwards. Continue?",
                IconName = "DatabaseArrowUp"
            });

        return builder;
    }

    private static ResourceCommandState IsRunning(UpdateCommandStateContext ctx) =>
        ctx.ResourceSnapshot.State?.Text == "Running"
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;

    private static async Task<ExecuteCommandResult> InvokeAsync(
        IResourceBuilder<ProjectResource> builder,
        ExecuteCommandContext ctx,
        string path)
    {
        try
        {
            var endpoints = builder.Resource;
            var url = endpoints.GetEndpoint("https").Url;

            // Skip TLS validation — the dev cert may not be in the AppHost process trust store
            // on every machine. This code path is development-only.
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };

            var response = await http.PostAsync($"{url}{path}", content: null, ctx.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ctx.CancellationToken);
                return new ExecuteCommandResult { Success = false, Message = $"HTTP {(int)response.StatusCode}: {body}" };
            }

            return new ExecuteCommandResult { Success = true };
        }
        catch (Exception ex)
        {
            return new ExecuteCommandResult { Success = false, Message = ex.Message };
        }
    }
}
