using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 browser coverage for the deal-review unit context (plantry-o95j). The fixture serves the real production
/// hint module over HTTP and supplies the same compact checklist/deck hooks emitted by the Razor/JS surfaces.
/// Server rendering and hydration are covered by DealReviewPageTests; this journey owns browser interaction.
/// </summary>
[Trait("Category", "E2E")]
public sealed class DealReviewUnitHintJourneyTests : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact(DisplayName = "Deal review unit hint supports keyboard, pointer and viewport-safe dismissal")]
    public async Task Unit_Hint_Works_Across_Review_Surfaces()
    {
        var modulePath = FindModulePath();
        await using var server = await StaticFixtureServer.StartAsync(modulePath);
        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 320, Height = 640 },
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)TimeSpan.FromSeconds(15).TotalMilliseconds);
        await page.GotoAsync(server.Url);

        var popover = page.Locator("#deal-unit-popover");
        var checklistHint = page.Locator(".check-row .unit-hint");
        await Assertions.Expect(checklistHint).ToHaveCountAsync(1);
        await checklistHint.FocusAsync();
        await AssertOpenAndContainedAsync(checklistHint, popover);
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(popover).ToBeHiddenAsync();

        var deckHint = page.Locator(".focus-card .unit-hint");
        await Assertions.Expect(deckHint).ToHaveCountAsync(1);
        await deckHint.HoverAsync();
        await AssertOpenAndContainedAsync(deckHint, popover);
        await popover.HoverAsync();
        await page.WaitForTimeoutAsync(220);
        await Assertions.Expect(popover).ToBeVisibleAsync();

        await page.Mouse.ClickAsync(4, 4);
        await Assertions.Expect(popover).ToBeHiddenAsync();
    }

    private static async Task AssertOpenAndContainedAsync(ILocator hint, ILocator popover)
    {
        await Assertions.Expect(popover).ToBeVisibleAsync();
        await Assertions.Expect(popover).ToContainTextAsync("priced per kg");

        var box = await popover.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.X >= 0 && box.Y >= 0);
        Assert.True(box.X + box.Width <= 320 && box.Y + box.Height <= 640);
        Assert.Equal("true", await hint.GetAttributeAsync("aria-expanded"));
    }

    private static string FindModulePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Plantry.Web", "wwwroot", "js", "islands", "deal-unit-hint.js");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent!;
        }

        throw new FileNotFoundException("Could not locate the production deal unit hint module.");
    }

    private sealed class StaticFixtureServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly string _modulePath;
        private readonly Task _serverTask;

        private StaticFixtureServer(TcpListener listener, string modulePath)
        {
            _listener = listener;
            _modulePath = modulePath;
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            _serverTask = ServeAsync();
        }

        public string Url { get; }

        public static Task<StaticFixtureServer> StartAsync(string modulePath)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new StaticFixtureServer(listener, modulePath));
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = HandleAsync(client);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using var connection = client;
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? string.Empty;
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            var path = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
            var (status, contentType, body) = path switch
            {
                "/" => ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Html)),
                "/deal-unit-hint.js" => ("200 OK", "text/javascript; charset=utf-8", await File.ReadAllBytesAsync(_modulePath)),
                _ => ("404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found")),
            };

            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _serverTask;
            _stop.Dispose();
        }

        private const string Html = """
            <!doctype html>
            <html><head><style>
              * { box-sizing: border-box; }
              body { margin: 0; min-height: 640px; font: 14px sans-serif; }
              .check-row, .focus-card { padding: 12px; }
              .focus-card { margin-top: 480px; }
              .unit-hint { width: 28px; height: 28px; }
              .unit-popover { position: fixed; z-index: 100; width: 280px; max-width: calc(100vw - 24px); padding: 14px 16px; background: white; border: 1px solid black; }
              .unit-popover[hidden] { display: none; }
            </style></head><body>
              <div id="review-region">
                <div class="check-row">
                  <button type="button" class="unit-hint" data-unit-hint="This deal is priced per kg. You stock Whole Milk by g. Check how many are in a kg before comparing prices or confirming the match." aria-label="Why do the units differ?" aria-expanded="false" aria-controls="deal-unit-popover"></button>
                </div>
                <div class="focus-card">
                  <button type="button" class="unit-hint" data-unit-hint="This deal is priced per kg. You stock Whole Milk by g. Check how many are in a kg before comparing prices or confirming the match." aria-label="Why do the units differ?" aria-expanded="false" aria-controls="deal-unit-popover"></button>
                </div>
              </div>
              <div id="deal-unit-popover" class="unit-popover" role="tooltip" hidden><strong>Different units</strong><p data-unit-popover-copy></p></div>
              <script type="module">import { mountDealUnitHints } from '/deal-unit-hint.js'; mountDealUnitHints();</script>
            </body></html>
            """;
    }
}
