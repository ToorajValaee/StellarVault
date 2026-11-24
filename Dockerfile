ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb
ARG PWA_SERVER_IMAGE=ghcr.io/nginx/nginx-unprivileged:stable-alpine@sha256:ea4fd7ab5a8dc6348c18240bd3546f355f8373cd8f112580f7d8bd8ff5a74772
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY global.json NuGet.Config ./
COPY src ./src
ARG RELEASE_REVISION=unversioned
RUN dotnet restore src/StellarVault.Pwa/StellarVault.Pwa.csproj --source https://api.nuget.org/v3/index.json
RUN dotnet publish src/StellarVault.Pwa/StellarVault.Pwa.csproj -c Release --no-restore -o /app/publish -p:DebugType=None -p:DebugSymbols=false -p:SourceRevisionId=${RELEASE_REVISION}

FROM ${PWA_SERVER_IMAGE} AS final
ARG RELEASE_REVISION=unversioned
ARG WORKTREE_DIGEST=unrecorded
ARG PREVIEW=true
LABEL org.opencontainers.image.revision=${RELEASE_REVISION} io.stellarvault.source-digest=${WORKTREE_DIGEST} io.stellarvault.preview=${PREVIEW}
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 CMD wget -q --spider http://127.0.0.1:8080/health/ready || exit 1
