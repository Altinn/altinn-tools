FROM mcr.microsoft.com/dotnet/sdk:9.0-noble@sha256:802e64ab9e113bdfa5d476ae00c7b40ab5c42da1792731c41a47f43bc4e74bdb AS build
WORKDIR /source
ENV CI=true

COPY ./Directory.Build.props ./
COPY ./.csharpierrc.yaml ./
COPY ./.csharpierignore ./
COPY ./.editorconfig ./
COPY ./src/Altinn.Apps.Monitoring/Altinn.Apps.Monitoring.csproj ./src/Altinn.Apps.Monitoring/Altinn.Apps.Monitoring.csproj
WORKDIR /source/src/Altinn.Apps.Monitoring/
RUN dotnet restore

COPY src/Altinn.Apps.Monitoring/. ./
RUN dotnet publish -c Release -o /app --no-restore
RUN echo "{\"LogDirectory\":\"/telemetry\",\"FileSize\":32768,\"LogLevel\":\"Warning\"}" > /app/OTEL_DIAGNOSTICS.json

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled-extra@sha256:7546ed4a06eec883137cb2012cdcfa2de74b4e17613cb2297bd8d2d68eeaa980
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app ./
USER $APP_UID
ENTRYPOINT ["./Altinn.Apps.Monitoring"]
