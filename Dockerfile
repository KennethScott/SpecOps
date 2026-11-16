# SpecOps container image (Linux). Build: docker build -t specops .
# Customize the PowerShell modules baked into the image:
#   docker build --build-arg PS_MODULES="VCF.PowerCLI Microsoft.Graph Az.Accounts" -t specops .   (full PowerCLI + Graph)
# More modules can be added at runtime under /data/modules without rebuilding.

# ---- 1. Client assets: Tailwind CSS and the code editor bundle (Node) -----------------------------
FROM node:22-slim AS client
WORKDIR /src/src/SpecOps.Web
COPY src/SpecOps.Web/package.json src/SpecOps.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/SpecOps.Web/ ./
RUN npm run build:css && npm run build:editor

# ---- 2. Build and publish the app (.NET SDK) -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
# Restore first so package downloads are cached until a project or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SpecOps.Core/SpecOps.Core.csproj src/SpecOps.Core/
COPY src/SpecOps.Data/SpecOps.Data.csproj src/SpecOps.Data/
COPY src/SpecOps.Web/SpecOps.Web.csproj src/SpecOps.Web/
RUN dotnet restore src/SpecOps.Web/SpecOps.Web.csproj
COPY src/ src/
COPY --from=client /src/src/SpecOps.Web/wwwroot/css src/SpecOps.Web/wwwroot/css
COPY --from=client /src/src/SpecOps.Web/wwwroot/lib/editor src/SpecOps.Web/wwwroot/lib/editor
# No --no-restore: restore again now the sources are here. The SDK only adds the Blazor framework assets package
# (which provides _framework/blazor.web.js) when it can see Razor components, which the csproj-only restore above
# can't. Packages are already cached, so this is quick.
RUN dotnet publish src/SpecOps.Web/SpecOps.Web.csproj -c Release -o /app \
      -p:SkipClientBuild=true -p:UseAppHost=false

# ---- 3. PowerShell modules (the SDK image includes pwsh) -------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS modules
# VMware.VimAutomation.Core = vSphere cmdlets (Get-VM etc.), ~300 MB. The full VCF.PowerCLI bundle (NSX, Image Builder,
# storage, ...) is ~950 MB: pass it in PS_MODULES if you need it.
ARG PS_MODULES="VMware.VimAutomation.Core Microsoft.Graph.Authentication Microsoft.Graph.Users Microsoft.Graph.Groups Az.Accounts"
SHELL ["pwsh", "-NoLogo", "-NoProfile", "-Command"]
RUN $ErrorActionPreference = 'Stop'; \
    New-Item -ItemType Directory -Path /modules | Out-Null; \
    $env:PS_MODULES -split '\s+' | Where-Object { $_ } | ForEach-Object { \
      Write-Host "Saving module $_"; \
      Save-PSResource -Name $_ -Path /modules -Repository PSGallery -TrustRepository -Quiet \
    }; \
    New-Item -ItemType Directory -Force -Path /var/opt/VMware | Out-Null; \
    if (Test-Path /modules/VMware.VimAutomation.Core) { \
      # Decline VMware's CEIP for all users, otherwise PowerCLI prints a long notice into every run's output.
      $env:PSModulePath = "/modules:$env:PSModulePath"; \
      Import-Module VMware.VimAutomation.Core -WarningAction SilentlyContinue 6>$null; \
      Set-PowerCLIConfiguration -Scope AllUsers -ParticipateInCEIP $false -Confirm:$false | Out-Null \
    }

# ---- 4. Runtime ------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
# ssh: PowerShell SSH remoting (e.g. to a Windows jump host for AD scripts). curl: health check, and handy in scripts.
RUN apt-get update \
 && apt-get install -y --no-install-recommends openssh-client curl \
 && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    SpecOps__DataDirectory=/data \
    SpecOps__ConfigFile=/data/specops.json \
    SpecOps__SeedDirectory=/app/samples \
    HttpsRedirection=false \
    PSModulePath=/data/modules:/opt/specops/modules

WORKDIR /app
COPY --from=build /app ./
COPY --from=modules /modules /opt/specops/modules
# PowerCLI's all-users settings (CEIP declined), written in the modules stage.
COPY --from=modules /var/opt/VMware /var/opt/VMware
# Starter config + scripts, copied into /data on first run (never over existing files).
COPY samples/specops.json /app/samples/specops.json
COPY samples/scripts /app/samples/scripts

RUN mkdir -p /data/modules && chown -R $APP_UID:$APP_UID /data
USER $APP_UID
VOLUME /data
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "SpecOps.Web.dll"]
