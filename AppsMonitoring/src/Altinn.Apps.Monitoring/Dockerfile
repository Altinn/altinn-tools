FROM mcr.microsoft.com/dotnet/sdk:9.0-noble@sha256:5643129f0c9f09bab23387c57bfeea1e3e42f47c7370fd9e389a5739e5ef383c AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled-extra@sha256:6357a8a7d71612cd9e989cac29b70114b5224ad537fd959d484fa6db24a9f3b6
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app ./
USER $APP_UID
ENTRYPOINT ["./Altinn.Apps.Monitoring"]
