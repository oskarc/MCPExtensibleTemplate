# The test host image — test only (contract-005 · G-10).
#
# Built by the end-to-end harness from the repository root, so the root .dockerignore applies, and tagged, like the
# harness's other images, with the hash of that build context. It is named mcp-e2e-testhost, labelled test-only, and
# never pushed; the product's Dockerfile knows nothing of it and copies nothing of it.
#
# It compiles the product from the same sources and lock file as the shipped image, with the test host beside it,
# and runs the way the shipped image runs: the same digest-pinned base images, the same working directory and log
# directory, the same non-root user, the product's own settings files (published with it from the product project).
# No BuildKit-only syntax, as the root Dockerfile: the harness builds through the engine's classic builder.

FROM mcr.microsoft.com/dotnet/sdk:10.0.101-noble@sha256:5504edd1267dd4deab3443f960cfab219249c8bd935fbcc358f1c24aeae23fe0 AS build
WORKDIR /src

COPY global.json Directory.Build.props .editorconfig ./
COPY McpServerTemplate/McpServerTemplate.csproj McpServerTemplate/packages.lock.json McpServerTemplate/
COPY McpServerTemplate.E2EHost/McpServerTemplate.E2EHost.csproj McpServerTemplate.E2EHost/packages.lock.json McpServerTemplate.E2EHost/
RUN dotnet restore McpServerTemplate.E2EHost/McpServerTemplate.E2EHost.csproj --locked-mode

COPY McpServerTemplate/ McpServerTemplate/
COPY McpServerTemplate.E2EHost/ McpServerTemplate.E2EHost/
RUN dotnet publish McpServerTemplate.E2EHost/McpServerTemplate.E2EHost.csproj -c Release --no-restore -o /app -p:UseAppHost=false

# The log directory the Production file sink writes to, owned by the app user (the root Dockerfile explains why).
RUN mkdir -p /runtime/logs

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02 AS final
WORKDIR /app
COPY --from=build /app ./
COPY --from=build --chown=1654:1654 /runtime/logs ./logs
EXPOSE 3001
USER 1654
LABEL org.opencontainers.image.title="McpServerTemplate end-to-end test host" \
      org.mcp-server-template.test-only="true"
ENTRYPOINT ["dotnet", "McpServerTemplate.E2EHost.dll"]
