FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy client project first
COPY ["BlazorChat.Client/BlazorChat.Client.csproj", "BlazorChat.Client/"]
RUN dotnet restore "BlazorChat.Client/BlazorChat.Client.csproj"

# Copy host project
COPY ["BlazorChat.Client.Serve/BlazorChat.Client.Serve.csproj", "BlazorChat.Client.Serve/"]
RUN dotnet restore "BlazorChat.Client.Serve/BlazorChat.Client.Serve.csproj"

# Copy everything else
COPY BlazorChat.Client/ BlazorChat.Client/
COPY BlazorChat.Client.Serve/ BlazorChat.Client.Serve/

# Build host
WORKDIR "/src/BlazorChat.Client.Serve"
RUN dotnet build "BlazorChat.Client.Serve.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "BlazorChat.Client.Serve.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BlazorChat.Client.Serve.dll"]