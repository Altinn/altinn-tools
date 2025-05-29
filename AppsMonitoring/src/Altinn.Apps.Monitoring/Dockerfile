FROM mcr.microsoft.com/dotnet/sdk:9.0-noble@sha256:c84968764a7d265a29cc840096750816d82655369d6ad03bcdf65f790684fd21 AS build
WORKDIR /source

COPY ./Directory.Build.props ./
COPY ./.csharpierrc.yaml ./
COPY ./.editorconfig ./
COPY ./src/Altinn.Apps.Monitoring/Altinn.Apps.Monitoring.csproj ./src/Altinn.Apps.Monitoring/Altinn.Apps.Monitoring.csproj
WORKDIR /source/src/Altinn.Apps.Monitoring/
RUN dotnet restore

COPY src/Altinn.Apps.Monitoring/. ./
RUN dotnet publish -c Release -o /app --no-restore
RUN echo "{\"LogDirectory\":\"/telemetry\",\"FileSize\":32768,\"LogLevel\":\"Warning\"}" > /app/OTEL_DIAGNOSTICS.json

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled-extra@sha256:7cffcc82529a669bcf1673dd25ad8916ae3b797fb66e52893e6f1c141b9ce860
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app ./
USER $APP_UID
ENTRYPOINT ["./Altinn.Apps.Monitoring"]
