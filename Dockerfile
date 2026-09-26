# The MCP server image — the artifact that ships (contract-005 · G-1).
#
# Built from the repository root, which .dockerignore narrows to source. Two stages: the SDK
# publishes the server, and the runtime stage carries only what was published. Nothing test-related
# is copied: this is the image the end-to-end suite runs and the image that ships, the same bytes.
#
# Both base images are pinned by the digest of their multi-arch index, so a tag moving on the
# registry cannot change what this file builds, on any architecture:
#   sdk:10.0.101-noble — the SDK global.json names, so the image compiles with the analyser set CI
#     uses; a floating 10.0 tag fails global.json's resolution
#   aspnet:10.0-noble-chiseled — no shell and no package manager, non-root by default
# Resolved 2026-09-26 with `docker buildx imagetools inspect <image>:<tag>`.
#
# No BuildKit-only syntax: the end-to-end harness builds through Testcontainers, which uses the
# engine's classic builder. Further hardening (read-only root, diagnostics off) is Phase 6's.

FROM mcr.microsoft.com/dotnet/sdk:10.0.101-noble@sha256:5504edd1267dd4deab3443f960cfab219249c8bd935fbcc358f1c24aeae23fe0 AS build
WORKDIR /src

# Restore first, from the lock file, so the restore layer is reused until a dependency changes.
COPY global.json Directory.Build.props .editorconfig ./
COPY McpServerTemplate/McpServerTemplate.csproj McpServerTemplate/packages.lock.json McpServerTemplate/
RUN dotnet restore McpServerTemplate/McpServerTemplate.csproj --locked-mode

COPY McpServerTemplate/ McpServerTemplate/
RUN dotnet publish McpServerTemplate/McpServerTemplate.csproj -c Release --no-restore -o /app -p:UseAppHost=false

# The runtime image has no shell to create a directory with, so the log directory is made here and
# copied across owned by the app user. The Production file sink writes logs/ relative to the working
# directory; without a directory it may write to, the sink writes nothing and says nothing.
RUN mkdir -p /runtime/logs

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02 AS final

# The content root: appsettings.{Environment}.json is found from here, and so is logs/.
WORKDIR /app
COPY --from=build /app ./
COPY --from=build --chown=1654:1654 /runtime/logs ./logs

# The default port (HttpTransport:Port). Binding and every other setting come from the environment.
EXPOSE 3001

# The chiseled image's app user, stated rather than inherited.
USER 1654

ENTRYPOINT ["dotnet", "McpServerTemplate.dll"]
