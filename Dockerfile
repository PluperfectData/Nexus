# syntax=docker/dockerfile:1

# Two images from one file: --target api is the service, and --target web serves its pages over HTTPS, and the service
# behind them. compose.yaml runs both, with MySQL beside them. The shared libraries come in as the jwt and mail build
# contexts, placed as they sit on disk, since the project references count up to them.

# The pages, built to call the service at localhost:6163, where the web image serves it.
FROM node:24-trixie-slim AS client
ENV VITE_API_URL=https://localhost:6163
WORKDIR /src/Pluperfect.Jwt/client
COPY --from=jwt client/package.json client/package-lock.json client/tsconfig.json ./
RUN npm ci
COPY --from=jwt client/src/ src/
RUN npm run build
WORKDIR /src/Pluperfect.Data/Nexus/Client
COPY Client/package.json Client/package-lock.json ./
RUN npm ci
COPY Client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src
COPY --from=jwt Directory.Build.props Directory.Packages.props global.json Pluperfect.Jwt/
COPY --from=jwt src/ Pluperfect.Jwt/src/
COPY --from=mail Directory.Build.props Directory.Packages.props global.json Pluperfect.Mail/
COPY --from=mail src/ Pluperfect.Mail/src/
# A build on the computer the libraries came from leaves its own bin and obj beside their source.
RUN find Pluperfect.Jwt Pluperfect.Mail -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
COPY Server/ Pluperfect.Data/Nexus/Server/
RUN dotnet publish Pluperfect.Data/Nexus/Server/src/Nexus.Service/Nexus.Service.csproj -c Release -o /app -p:RestoreLockedMode=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=server /app ./
RUN mkdir /data && chown "$APP_UID" /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=6163 \
    Nexus__DataDirectory=/data
VOLUME /data
EXPOSE 6163
# The first start brings a new database up to date, which can take several minutes.
HEALTHCHECK --interval=10s --timeout=5s --start-period=10m \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/6163 && printf 'GET /health HTTP/1.0\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "Nexus.Service.dll"]

FROM caddy:2-alpine AS web
COPY deploy/docker/Caddyfile /etc/caddy/Caddyfile
COPY --from=client /src/Pluperfect.Data/Nexus/Client/dist /srv
EXPOSE 6165 6163 6164
# Healthy once Caddy has made the authority its certificates come from, which the launcher asks the computer to trust.
HEALTHCHECK --interval=2s --timeout=5s --start-period=1m \
    CMD ["test", "-f", "/data/caddy/pki/authorities/local/root.crt"]
