using Microsoft.Extensions.Hosting;

// Worktree verification gate (plantry-q9zr.14) — resolved BEFORE the builder so the endpoint rebinds below
// reach configuration. `aspire start --isolated` randomizes SERVICE endpoints and isolates user-secrets, but
// it still honours the AppHost's OWN dashboard / OTLP / resource-service endpoints pinned in
// launchSettings.json (17250 / 21137 / 22192) — proven by the first live isolated-mode trial, which died
// binding 22192 while the primary AppHost held it. In a verify run, rebind those three to port 0 (OS-assigned)
// so a second instance never collides with the primary's fixed ports. This MUST run before
// DistributedApplication.CreateBuilder reads configuration (the dashboard/resource-service host binds them at
// build time), and it overrides the launchSettings-injected process env vars, which win over an ambient shell
// override.
var isVerifyRun = Environment.GetEnvironmentVariable("PLANTRY_VERIFY") is "true" or "1";
if (isVerifyRun)
{
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "https://127.0.0.1:0");
    Environment.SetEnvironmentVariable("DOTNET_DASHBOARD_OTLP_ENDPOINT_URL", "https://127.0.0.1:0");
    Environment.SetEnvironmentVariable("DOTNET_RESOURCE_SERVICE_ENDPOINT_URL", "https://127.0.0.1:0");
}

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("docker-compose");

// Pinned so the password stays stable across runs and matches the data volume.
var postgresPassword = builder.AddParameter("postgres-password", secret: true);

// Worktree verification gate (plantry-q9zr.14). `aspire start --isolated` natively randomizes ports and
// isolates user-secrets so a second full stack can boot alongside the developer's primary one — but it does
// NOT isolate a NAMED Docker volume. Two Postgres containers mounting `plantrydb-data` on the same data
// directory risks corruption, so in a verify run we run Postgres EPHEMERALLY (no named volume — an empty DB
// the FakeDataSeeder fixture fills deterministically) and skip pgAdmin (which pins its own container +
// well-known host port and would collide with the primary stack). Keyed off an explicit PLANTRY_VERIFY
// switch: Aspire 13 exposes no public isolated-mode flag to auto-detect at build time, so the env var is the
// reliable gate (resolved into `isVerifyRun` above). The recipe lives in docs/Engineering/worktree-verification.md.
var postgres = builder.AddPostgres("postgres", password: postgresPassword);
if (!isVerifyRun)
{
    postgres.WithDataVolume("plantrydb-data");
    if (builder.Environment.IsDevelopment())
        postgres.WithPgAdmin();
}

var plantryDb = postgres.AddDatabase("plantrydb");

var migrator = builder.AddProject<Projects.Plantry_Migrator>("migrator")
    .WithReference(plantryDb)
    .WaitFor(plantryDb);

var plantryWeb = builder.AddProject<Projects.Plantry_Web>("plantry-web")
    .WithReference(plantryDb)
    .WaitForCompletion(migrator)
    .WithSeedCommands();

builder.Build().Run();
