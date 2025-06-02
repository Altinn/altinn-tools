FROM mcr.microsoft.com/dotnet/sdk:9.0-noble@sha256:9f7bd4d010026e15a57d9cf876f2f7d08c3eeed6a0ea987b8c5ba8c75e68e948 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled-extra@sha256:96b0c77d6434b543a7658f909e500395f2f9ebd36d7615dd5551297446c709eb
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app ./
USER $APP_UID
ENTRYPOINT ["./Altinn.Apps.Monitoring"]
