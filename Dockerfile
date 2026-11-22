# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy everything (.dockerignore keeps bin/obj/.idea out) so project references resolve.
COPY . .

# Restore the application (the host's private NuGet.Config is not copied in).
RUN dotnet restore AiMcpContainers.csproj

# Publish the web app.
RUN dotnet publish AiMcpContainers.csproj -c Release -o /app --no-restore

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# ca-certificates: TLS to the OpenAI/Anthropic endpoints.
# nodejs runs the launcher that selects the newest host VS Code Claude executable.
# Do not install a second npm CLI here: a cached image can lag behind VS Code.
# bubblewrap: the codex provider IS a native binary mounted at runtime, but it needs a sandbox
#   backend to run model-generated commands. codex looks for a SYSTEM bwrap on PATH first, and only
#   falls back to the `codex-resources/bwrap` bundled next to the mounted binary — whose digest must
#   then match the exact codex version. Because codex is mounted (not baked), that bundled resource
#   drifts out of sync every time codex is upgraded on the host without its resources being refreshed
#   (measured 2026-07-17: codex 0.145 binary + a 0.142 bwrap → "digest mismatch", the bridge could not
#   read a single file). Installing a system bwrap here mirrors the host and makes codex version-drift
#   irrelevant: codex uses the system bwrap and never consults the bundled digest.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl gnupg bubblewrap \
 && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && apt-get purge -y gnupg \
 && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*

# The extension root is mounted read-only. Resolve again for every provider process.
COPY --chmod=0555 scripts/claude-vscode.mjs /usr/lib/ai-mcp-containers/claude-vscode.mjs
RUN ln -s /usr/lib/ai-mcp-containers/claude-vscode.mjs /usr/bin/claude
ENV AI_MCP_CLAUDE_BIN=/usr/bin/claude
ENV AI_MCP_CLAUDE_HOME=/claudehome
ENV AI_MCP_CLAUDE_EXTENSIONS=/vscode-extensions

# OPTIONAL baked-in codex CLI (default OFF — Linux hosts mount the VSCode extension's
# binary so the bridge and the host extension share one version + session state db).
# On hosts where no linux codex binary exists to mount (the Mac mini: the host binary
# is darwin-arm64, unrunnable in a linux container), build with
# --build-arg INSTALL_CODEX_CLI=true and point AI_MCP_CODEX_BIN at /usr/bin/codex.
ARG INSTALL_CODEX_CLI=false
RUN if [ "$INSTALL_CODEX_CLI" = "true" ]; then npm install -g @openai/codex; fi

COPY --from=build /app ./

# Listen on all interfaces *inside* the container; the host maps it to 127.0.0.1 only.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
# Newer host kernels (Linux 7.x) enforce W^X strictly; without this the .NET runtime
# segfaults on startup. Disabling write-xor-execute makes the runtime boot reliably.
ENV DOTNET_EnableWriteXorExecute=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "AiMcpContainers.dll"]
