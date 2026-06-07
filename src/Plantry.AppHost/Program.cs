using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");

if (builder.Environment.IsDevelopment())
{
    postgres.WithPgAdmin();
}

var plantryDb = postgres.AddDatabase("plantrydb");

builder.AddProject<Projects.Plantry_Web>("plantry-web")
    .WithReference(plantryDb)
    .WaitFor(plantryDb);

builder.Build().Run();
