FROM mcr.microsoft.com/dotnet/sdk:9.0-noble@sha256:b9f738e7bfa1581ae38a91365f26e21354fe09c6b9a4f2327f6cec588486d9e2 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled-extra@sha256:f7cc2dc08ebf1e191a3c3d0e723c07e66e31026a4ed78f934bbb59a0842d70cb
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app ./
USER $APP_UID
ENTRYPOINT ["./Altinn.Apps.Monitoring"]
