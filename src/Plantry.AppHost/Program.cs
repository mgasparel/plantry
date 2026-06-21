using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("docker-compose");

// Pinned so the password stays stable across runs and matches the data volume.
var postgresPassword = builder.AddParameter("postgres-password", secret: true);

var postgres = builder.AddPostgres("postgres", password: postgresPassword)
    .WithDataVolume("plantrydb-data");

if (builder.Environment.IsDevelopment())
{
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
