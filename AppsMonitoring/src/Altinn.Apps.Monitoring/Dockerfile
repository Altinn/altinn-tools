FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build
WORKDIR /source

COPY *.csproj ./
RUN dotnet restore

COPY * ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Altinn.Apps.Monitoring.dll"]
