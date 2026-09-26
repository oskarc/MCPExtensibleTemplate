# The test issuer image — test only (contract-005 · G-6).
#
# Built by the end-to-end harness from the repository root, so the root .dockerignore applies. It is
# tagged mcp-e2e-testissuer, labelled test-only, and never pushed. The product's Dockerfile knows
# nothing of it and copies nothing of it.
#
# The same digest-pinned base images as the server image (see the root Dockerfile).

FROM mcr.microsoft.com/dotnet/sdk:10.0.101-noble@sha256:5504edd1267dd4deab3443f960cfab219249c8bd935fbcc358f1c24aeae23fe0 AS build
WORKDIR /src

COPY global.json Directory.Build.props .editorconfig ./
COPY McpServerTemplate.Testing/McpServerTemplate.Testing.csproj McpServerTemplate.Testing/packages.lock.json McpServerTemplate.Testing/
COPY McpServerTemplate.TestIssuer/McpServerTemplate.TestIssuer.csproj McpServerTemplate.TestIssuer/packages.lock.json McpServerTemplate.TestIssuer/
RUN dotnet restore McpServerTemplate.TestIssuer/McpServerTemplate.TestIssuer.csproj --locked-mode

COPY McpServerTemplate.Testing/ McpServerTemplate.Testing/
COPY McpServerTemplate.TestIssuer/ McpServerTemplate.TestIssuer/
RUN dotnet publish McpServerTemplate.TestIssuer/McpServerTemplate.TestIssuer.csproj -c Release --no-restore -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02
WORKDIR /app
COPY --from=build /app ./
USER 1654
LABEL org.opencontainers.image.title="McpServerTemplate test issuer" \
      org.mcp-server-template.test-only="true"
ENTRYPOINT ["dotnet", "McpServerTemplate.TestIssuer.dll"]
