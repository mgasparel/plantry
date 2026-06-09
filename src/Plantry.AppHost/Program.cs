using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");

if (builder.Environment.IsDevelopment())
{
    postgres.WithPgAdmin();
}

var plantryDb = postgres.AddDatabase("plantrydb");

var plantryWeb = builder.AddProject<Projects.Plantry_Web>("plantry-web")
    .WithReference(plantryDb)
    .WaitFor(plantryDb)
    .WithSeedCommands();

builder.Build().Run();
