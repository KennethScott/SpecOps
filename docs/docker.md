# Running SpecOps in Docker

SpecOps ships as a Linux container image: `ghcr.io/kennethscott/specops`. It runs on any Docker host; the typical
setup is a Linux VM (VMware, Hyper-V, cloud) with Docker Compose.

## Quick start

```bash
git clone https://github.com/KennethScott/SpecOps.git && cd SpecOps
cp .env.example .env        # uncomment and fill in a sign-in provider (below) and your admin
docker compose up -d        # http://<host>:8080
```

On first start SpecOps copies starter scripts and a sample `specops.json` into its data volume and imports
them, so there's something to run straight away. From then on admins manage scripts, policies and the `.ps1`
code under **Configure** in the app.

> Sign-in needs at least one identity provider (Microsoft Entra ID, Google or any OpenID Connect provider) and
> HTTPS for anything other than `localhost`, because providers only redirect back to `https://` URLs.
> For HTTPS see [below](#https).

## Configuration

All settings go in `.env` (next to `compose.yaml`, git-ignored). `.env.example` lists the common ones. Any
`appsettings.json` value can be set: nested keys use `__`, list items an index:

| appsettings                         | `.env`                                   |
| ----------------------------------- | ---------------------------------------- |
| `Auth:Providers:Microsoft:ClientId` | `Auth__Providers__Microsoft__ClientId=…` |
| `Auth:BootstrapAdmins[0]`           | `Auth__BootstrapAdmins__0=…`             |

Invalid settings stop the container with a list of problems (`docker compose logs specops`).

### Signing in the first time

1. Configure a provider. For **Microsoft Entra ID**: create an app registration with the redirect URI
   `https://<your-host>/signin-microsoft`, a client secret, and (recommended) the `groups` claim. Then set
   `Authority` (`https://login.microsoftonline.com/<tenant-id>/v2.0`), `ClientId`, `ClientSecret` and
   `AllowedTenantIds__0` (your tenant ID).
2. Sign in. You'll land on "waiting for approval", which shows your **sign-in ID** (e.g. `Microsoft:AbC…`).
3. Set `Auth__BootstrapAdmins__0=<that sign-in ID>`, `docker compose up -d`, and sign in again: you're an admin.
4. Approve and assign roles to everyone else on the **Users** page.

## HTTPS

Sign-in providers only redirect back to `https://` URLs, so every server people use needs HTTPS. The server
doesn't have to be reachable from the internet: the redirect happens in the user's browser, so a name like
`https://specops.corp.local` works with Entra ID, Google and OIDC as long as users' browsers trust the
certificate.

The Caddy setup (`compose.caddy.yaml`) puts Caddy in front of SpecOps: it serves HTTPS, redirects HTTP to HTTPS
and renews certificates. `SPECOPS_TLS` picks where the certificate comes from:

| `SPECOPS_TLS`    | Certificate                               | Use it for                                         |
| ---------------- | ----------------------------------------- | -------------------------------------------------- |
| `auto` (default) | Let's Encrypt                             | Public DNS name, ports 80/443 open to the internet |
| `internal`       | Caddy's own CA                            | Internal networks, testing                         |
| `files`          | Yours, e.g. from your internal CA (AD CS) | Internal networks, production                      |

### Internal network: your internal CA (recommended for production)

Issue a certificate for the SpecOps host name from your internal CA (e.g. Active Directory Certificate
Services). Domain-joined machines already trust that CA, so nothing needs installing on clients. Save it as PEM
files next to `compose.yaml`:

- `certs/specops.crt`: the certificate, followed by any intermediate CA certificates
- `certs/specops.key`: its private key (unencrypted; keep the folder readable only by the Docker host admins)

```bash
SPECOPS_HOST=specops.corp.local SPECOPS_TLS=files docker compose -f compose.yaml -f compose.caddy.yaml up -d
```

Got a PFX instead? Convert it: `openssl pkcs12 -in specops.pfx -nokeys -out certs/specops.crt` and
`openssl pkcs12 -in specops.pfx -nocerts -nodes -out certs/specops.key`. When the certificate is renewed,
replace the files and run `docker compose restart caddy`.

### Internal network: Caddy as its own CA (testing)

Caddy creates a private CA on first start and issues and renews the certificate itself. No certificate
requests and nothing to renew, but browsers show a warning until Caddy's root certificate is trusted on the
client.

```bash
SPECOPS_HOST=specops.corp.local SPECOPS_TLS=internal docker compose -f compose.yaml -f compose.caddy.yaml up -d

# Copy Caddy's root certificate out of the container
docker compose -f compose.yaml -f compose.caddy.yaml cp caddy:/data/caddy/pki/authorities/local/root.crt caddy-root.crt
```

Then trust `caddy-root.crt` on the machines that will use SpecOps:

- **One Windows test machine**: double-click it, then **Install Certificate** > **Local Machine** > **Trusted Root
  Certification Authorities**. Or, from an elevated prompt: `certutil -addstore -f Root caddy-root.crt`.
- **Many Windows machines**: Group Policy > Computer Configuration > Policies > Windows Settings > Security Settings
  > Public Key Policies > **Trusted Root Certification Authorities** > Import.
- **macOS**: `sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain caddy-root.crt`
- **Firefox** uses its own store unless `security.enterprise_roots.enabled` is on (it is by default on Windows
  for roots installed by an administrator).

The root certificate (and its key) live in the `caddy-data` volume: keep that volume, or clients have to trust a
new root. Anyone with that key can issue certificates your clients trust, so treat the volume like a secret and
prefer `files` with your real CA for production.

### Other options

- **Your existing reverse proxy / load balancer** (F5, NGINX, IIS ARR, Azure App Gateway…): terminate TLS there,
  forward to `http://<host>:8080`, and tell SpecOps to trust it so redirects, secure cookies and client IPs are
  right: `Proxy__KnownProxies__0=<proxy IP>` (or `Proxy__KnownNetworks__0=<CIDR>`). WebSockets must be passed
  through (Blazor uses them).

- **SpecOps serves HTTPS itself, no proxy**: mount a PFX and let Kestrel use it:

  ```yaml
  # compose.override.yaml
  services:
    specops:
      ports: ['443:8443']
      volumes: ['./certs:/certs:ro']
      environment:
        ASPNETCORE_HTTPS_PORTS: '8443'
        Kestrel__Certificates__Default__Path: /certs/specops.pfx
        Kestrel__Certificates__Default__Password: ${CERT_PASSWORD}
  ```

## Your scripts

Everything SpecOps keeps is in the `specops-data` volume, mounted at `/data`:

| Path                 | What                                                                                   |
| -------------------- | -------------------------------------------------------------------------------------- |
| `/data/specops.json` | Runner settings; its scripts, categories and policies are imported on first start only |
| `/data/scripts/`     | Your `.ps1` files (edited under Configure; script paths are relative to this folder)   |
| `/data/modules/`     | Extra PowerShell modules (see below)                                                   |
| `/data/specops.db`   | Users, roles, configuration and its history, run history, audit log (SQLite)           |
| `/data/logs/`        | Application logs                                                                       |
| `/data/keys/`        | Sign-in cookie encryption keys (keep with the database)                                |

To work with these files directly, bind-mount a host folder instead of the named volume, e.g.
`./specops-data:/data` (the folder must be writable by UID 1654, the container's non-root user).

### PowerShell modules

The image (about 850 MB) includes **VMware.VimAutomation.Core** (the vSphere cmdlets: `Get-VM`, `Get-VMHost`,
snapshots, datastores…), **Microsoft.Graph.Authentication / .Users / .Groups** and **Az.Accounts**. PowerCLI's
Customer Experience Improvement Program prompt is pre-declined so it doesn't appear in every run's output.

- **Add modules at runtime** by saving them into `/data/modules` (SpecOps hosts PowerShell itself, so the runtime
  image has no `pwsh`; the .NET SDK image does, and can write into the same volume):

  ```bash
  docker run --rm -v specops_specops-data:/data mcr.microsoft.com/dotnet/sdk:11.0 \
    pwsh -c "Save-PSResource ImportExcel -Path /data/modules -TrustRepository"
  ```

  New modules are picked up by the next run; no restart needed.

- **Or bake your own list into the image**, e.g. the full PowerCLI bundle (NSX, Image Builder, storage…; adds about
  650 MB) and the complete Graph SDK:

  ```bash
  docker build --build-arg PS_MODULES="VCF.PowerCLI Microsoft.Graph Az.Accounts ExchangeOnlineManagement" -t specops .
  ```

## Active Directory and other Windows-only scripts

The container runs PowerShell 7 on Linux, so Windows-only modules such as **ActiveDirectory**, GroupPolicy, DNS
and DHCP server management aren't available inside it, and WinRM remoting from Linux isn't reliable. The proven
pattern is a **Windows jump host** reached over **PowerShell SSH remoting**:

1. On a domain-joined Windows server: install RSAT (AD module), PowerShell 7 and the OpenSSH Server, then add to
   `C:\ProgramData\ssh\sshd_config`:

   ```
   Subsystem powershell c:/progra~1/powershell/7/pwsh.exe -sshs -NoLogo
   ```

   and restart the `sshd` service.

2. Create a key pair for SpecOps, put the private key in `/data/ssh/id_ed25519`, and authorize the public key for
   a service account on the jump host.
3. In your scripts, run the AD part remotely:

   ```powershell
   $session = @{
       HostName    = 'mgmt01.corp.contoso.com'
       UserName    = 'CORP\svc-specops'
       KeyFilePath = '/data/ssh/id_ed25519'
       Options     = @{ StrictHostKeyChecking = 'accept-new'; UserKnownHostsFile = '/data/ssh/known_hosts' }
   }
   Invoke-Command @session -ScriptBlock { Get-ADUser -Identity $using:UserName -Properties LastLogonDate }
   ```

Cloud-side management (Microsoft Graph, Exchange Online, Azure, VMware PowerCLI) works directly from the container.

## Backups and upgrades

- **Upgrade**: `docker compose pull && docker compose up -d`. Database migrations run automatically on start.
- **Back up** the volume (stop first for a consistent copy of the database):

  ```bash
  docker compose stop specops
  docker run --rm -v specops_specops-data:/data -v "$PWD":/backup alpine tar czf /backup/specops-data.tgz -C /data .
  docker compose start specops
  ```

## Notes

- **Run one instance.** Active runs live in memory and the database is SQLite; don't scale SpecOps out.
- **Azure Container Apps**: not supported yet. Its persistent storage is a network file share, which isn't safe for
  SQLite. A PostgreSQL option for Azure is planned.
- The image runs as a non-root user, listens on port 8080 and reports health at `/healthz`.
