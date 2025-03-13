FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build
WORKDIR /source

COPY ./Directory.Build.props ./
COPY ./.csharpierrc.yaml ./
COPY ./.editorconfig ./
COPY ./src/Altinn.Apps.Monitoring/Altinn.Apps.Monitoring.csproj ./src/Altinn.Apps.Monitoring/Altinn.Apps.Monitoring.csproj
WORKDIR /source/src/Altinn.Apps.Monitoring/
RUN dotnet restore

COPY src/Altinn.Apps.Monitoring/. ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app ./
USER $APP_UID
ENTRYPOINT ["./Altinn.Apps.Monitoring"]
