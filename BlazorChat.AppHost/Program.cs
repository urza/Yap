var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.BlazorChat_Server>("server");


builder.AddProject<Projects.BlazorChat_Client_Serve>("blazorchat")
    .WithReference(server);

builder.Build().Run();
