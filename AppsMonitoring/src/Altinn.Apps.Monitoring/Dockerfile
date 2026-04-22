FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:d1823fecac3689a2eb959e02ee3bfe1c2142392808240039097ad70644566190 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:b4855d6d1c557c19a7c7165b354a138b47c17b29669ad0b3b22fb046a1b84fd8
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app ./
USER $APP_UID
ENTRYPOINT ["./Altinn.Apps.Monitoring"]
