# The MCP server image — the artifact that ships (contract-005 · G-1).
#
# Built from the repository root, which .dockerignore narrows to source. Two stages: the SDK
# publishes the server, and the runtime stage carries only what was published. Nothing test-related
# is copied: this is the image the end-to-end suite runs and the image that ships.
#
# contract-005 · G-17 round 3 (CON-R3-01) — the same image whichever builder builds it. Every owner and
# mode the image relies on is set here, not left to a builder's defaults: WORKDIR makes a missing
# directory as the user in force, which the classic builder takes to be root and BuildKit the base
# image's user, 1654, so under BuildKit (docker build, docker compose build: how an operator builds
# it) the server's own user owned /app, and could replace its settings files and assemblies. Now:
# /app and everything published in it root's, directories 0755 and files 0644; /app/logs alone the
# app user's, 0755; the process runs as the app user.
#
# Both base images are pinned by the digest of their multi-arch index, so a tag moving on the
# registry cannot change what this file builds, on any architecture:
#   sdk:10.0.101-noble — the SDK global.json names, so the image compiles with the analyser set CI
#     uses; a floating 10.0 tag fails global.json's resolution
#   aspnet:10.0-noble-chiseled — no shell and no package manager, non-root by default
# Resolved 2026-09-26 with `docker buildx imagetools inspect <image>:<tag>`.
#
# No BuildKit-only syntax (COPY --chmod among it): the image is built by the engine's classic builder
# as well as by BuildKit, and must come out the same from each. Further hardening (read-only root,
# diagnostics off) is Phase 6's.

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
#
# contract-005 · G-17 round 3 (CON-R3-01) — and every mode is set here, where there is a shell, and
# carried across by COPY, which keeps a file's mode under either builder: nothing is left to a umask.
RUN mkdir -p /runtime/logs \
    && chmod -R u=rwX,go=rX /app \
    && chmod 0755 /app /runtime/logs

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02 AS final

# contract-005 · G-17 round 3 (CON-R3-01) — root while the image's own tree is laid out, so WORKDIR makes
# /app root's under either builder, and every COPY names its owner. The app user comes back last.
USER 0:0

# The content root: appsettings.{Environment}.json is found from here, and so is logs/.
WORKDIR /app
COPY --from=build --chown=0:0 /app ./
COPY --from=build --chown=1654:1654 /runtime/logs ./logs

# The default port (HttpTransport:Port). Binding and every other setting come from the environment.
EXPOSE 3001

# contract-005 · G-17 round 2 — the aspnet base image sets ASPNETCORE_HTTP_PORTS=8080, ASP.NET Core's own port setting,
# which this server would ignore: it listens where HttpTransport:BindAddress and HttpTransport:Port say, and refuses at
# startup a listening setting it would ignore (HostBuilders.RefuseListeningSettings). Set empty here, which asks for
# nothing and is not refused, so the image carries no such request and the start-up warning it caused
# ("Overriding HTTP_PORTS '8080' ...") is gone.
ENV ASPNETCORE_HTTP_PORTS=

# The chiseled image's app user, stated rather than inherited.
USER 1654

ENTRYPOINT ["dotnet", "McpServerTemplate.dll"]
