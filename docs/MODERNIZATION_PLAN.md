# SpecOps v2 — Modernization Plan

_Analysis of `master` @ c566a87 plus the uncommitted work on `feature/dotnet8`, written 2026-11-15._

---

## 1. TL;DR

- **Start over, but in the same repo, and port the good parts.** The parts worth keeping are the PowerShell execution engine (about 300 lines in `PowershellHub.cs`), the script/parameter/policy config model, and the overall idea. Everything else is AdminLTE scaffolding or hand-built jQuery. Of the 2,554 tracked files, **2,513 (99 MB) are in `wwwroot`**, and almost all of that is unused AdminLTE plugins and docs.
- **Target .NET 11 + Microsoft.PowerShell.SDK 7.7, with every package on its latest stable version.** PowerShell 7.7 requires .NET 11. .NET 11 is an STS release, but STS now gets 24 months of support (to Nov 2028), the same end date as .NET 10 LTS, so there is no support downside. Plan to move to .NET 12 LTS when it ships (Nov 2027).
- **Switch the UI to Blazor (Interactive Server) + Tailwind CSS.** The app is already a SignalR app that streams output and builds forms from config. Blazor Server does both natively in C#, so the jQuery, DataTables, string-built HTML and the hub plumbing all go away.
- **Add a database (SQLite via EF Core). It isn't overkill if you use it for the right thing.** The big benefit is **run history and auditing** (who ran what, with which parameters, the output, and whether it succeeded). Moving scripts and policies into the DB with an admin UI is the second step. It also gets rid of the "restart the app after changing policies" limitation.
- **Cross-platform from the start: Linux/Docker and Windows/IIS are both first-class.** Auth moves to **ASP.NET Core Identity + external login providers** (Microsoft Entra, Google, any OIDC), with Windows auth as an optional extra provider. Windows-only features (impersonation, AD modules) light up when available and are hidden otherwise (section 7).
- **Fix two real bugs first, whatever else you decide** (section 3).

---

## 2. What's there today

| Area | Current state |
|---|---|
| Runtime | `master`: .NET 7 (out of support since May 2024). `feature/dotnet8` (uncommitted): net8 bump, Startup merged into minimal `Program.cs`, switched to `Negotiate` auth. |
| UI | Razor Pages + AdminLTE 3 / Bootstrap 4 / jQuery / DataTables (+ jszip, pdfmake) / jquery-validation / jQuery Terminal + Prism |
| Real-time | SignalR hub `PowerShellHub`, mapped twice (`/streamPowerShell` and `/streamPowerShellRaw` both point to the same hub) |
| Config | `appsettings.json` → SecurityPolicies + OutputLevels; `scriptsettings.json` → scripts + params + runspace config |
| Auth | Windows auth; each policy = `RequireRole(groups)`; default policy = union of all groups |
| Logging | Serilog → rolling file; custom ClientIp/UserName enrichers |
| Tests / CI | None |
| Other branches | `feature/blazor` (no commits beyond master), `origin/feature/database` (LiteDB WIP, one commit; it deletes the Terminal and has a binary `.db` committed) |

**Worth salvaging:** the execution flow (stream handlers → output records), the `SpecOpsCurrentUser / IP / ScriptId` built-in parameters, the parameter metadata schema, the deep-link route `/User/Scripts/{category}/{script}`, the Obsidian log color palette, and the README docs.

**Not worth salvaging:** all of `wwwroot`, `Classes/AuthorizeOr*` (unused), `ErrorLoggingMiddleware` (Serilog request logging and the exception handler already cover it), the `OutputLevels` config (just CSS), and the LiteDB branch.

---

## 3. Bugs & security issues found (all fixed by design in v2; no v1 hotfixes)

| # | Severity | Issue | Where |
|---|---|---|---|
| 1 | **High** | **Script execution doesn't check category authorization.** `StreamPowerShell(scriptId, …)` looks the script up in *all* scripts. Any user who can reach the site (any policy) can run an **Admin-category** script by calling the hub with its Id from devtools. `OnGetScripts`/`OnGetScript` also return any category's scripts, including `PathAndFilename`. | `Hubs/PowershellHub.cs:66`, `Services/ScriptService.cs:65`, `Pages/User/Scripts.cshtml.cs:45-53` |
| 2 | **High (functional)** | `finally { RsPool.Dispose(); }` throws `NullReferenceException` for every script **without** a `Runspace` block (e.g. the first Demo). The client sees "Script execution ended." and then an invoke error. | `Hubs/PowershellHub.cs:215` |
| 3 | Medium | **Output is rendered as HTML.** DataTables `row.add([... logRecord.data])` treats strings as HTML, so script output containing `<img onerror=…>` (from AD attributes, file contents, user-supplied params echoed back) executes in the viewer's browser. | `Pages/User/Scripts.cshtml:231` |
| 4 | Medium | **Cancel doesn't stop the script.** It only stops *waiting* (`asyncTask.Wait(token)`); the PowerShell pipeline keeps running in the background. `ps.Stop()` is never called. The `.Wait()` also blocks a thread-pool thread for the length of every script. | `PowershellHub.cs:222-228` |
| 5 | Medium | **No server-side parameter validation.** Required/Pattern/Min/Max/Options are enforced only in the browser. Clients can send parameters that aren't defined, and sending a `SpecOpsCurrentUser` key makes `Dictionary.Add` throw. | `PowershellHub.cs:66-68` |
| 6 | Medium | **Impersonation is fragile.** The runspace pool uses `PSThreadOptions.UseNewThread`, and new threads don't inherit the impersonation token. Whether the script really runs as the user depends on timing and pool state. Test this explicitly. | `PowershellHub.cs:106, 186` |
| 7 | Low | A new runspace pool is created **and opened on every run** (slow, especially with modules); pools should be cached per script. | `PowershellHub.cs:84` |
| 8 | Low | The authorized-category cache lasts a **24h** per user, so AD group changes don't apply for up to a day. Policy changes need a restart. | `ScriptService.cs:84` |
| 9 | Low | Any user can cancel any other user's run given the connection id (no ownership check). | `Scripts.cshtml.cs:55` |
| 10 | Low | Script config renders through string templates (`value='${parm.value}'`), so a quote in config breaks the form. Admin-controlled, but fragile. | `Scripts.cshtml:~360` |
| 11 | Docker blocker | `AddJsonFile("scriptSettings.json")` vs the file on disk `scriptsettings.json`. Fine on Windows, **crashes at startup on Linux** (case-sensitive, `optional:false`). | `Program.cs:55` |
| 12 | Hygiene | `EnableUnsafeBinaryFormatterSerialization` (removed in .NET 9+), unused `Newtonsoft` using, Razor runtime compilation (replaced by Hot Reload), deprecated `Serilog.Sinks.Elasticsearch` (use `Elastic.Serilog.Sinks`), duplicated bootstrap-logger config, both `package-lock.json` and `pnpm-lock.yaml`, stray root `channels.json`/`package-lock.json`. The uncommitted `appsettings.json` has your real machine\user name in it; move that to `appsettings.Development.json` or user-secrets before committing anything. |

---

## 4. Recommendation: rewrite vs. upgrade

**Rewrite in place** on a new branch off `master` (e.g. `v2`), and keep the repo, the history and the README.

Why not keep going on `feature/dotnet8`? The useful part of that branch is about 80 lines (`Program.cs`) and would be rewritten anyway. A UI switch from AdminLTE to anything else touches every page, and the hub is getting restructured. When every file changes, a clean scaffold that you port logic into is faster and easier to review than incremental edits.

Housekeeping before starting:
1. Tag the old branches so nothing is lost: `git tag archive/feature-database origin/feature/database`, same for `feature/dotnet8` (commit the WIP first, minus the appsettings change) and `feature/blazor`.
2. Tag `master` as `v1-final`.
3. On `v2`, delete `SpecOps/` wholesale and scaffold fresh. This drops the 99 MB from the working tree. History still has it; rewriting history is possible but not worth it.

---

## 5. Target architecture

```
SpecOps.slnx
├─ src/SpecOps.Core        # no ASP.NET dependency
│   ├─ Scripts/            # Script, ScriptParameter, Category, RunspaceProfile
│   ├─ Execution/          # ScriptRunner, RunspacePoolCache, RunRegistry, OutputRecord
│   ├─ Discovery/          # PowerShell AST → parameter metadata
│   └─ Validation/         # server-side param validation & type conversion
├─ src/SpecOps.Data        # EF Core DbContext, migrations, JSON import/export
├─ src/SpecOps.Web         # Blazor Web App (Interactive Server), auth, endpoints
└─ tests/SpecOps.Tests     # xUnit: runner, validation, authz, discovery
```

### Stack

| Concern | Choice | Notes |
|---|---|---|
| Runtime | .NET 11 | `dotnet new blazor --interactivity Server`; SDK pinned in `global.json` |
| PowerShell | Microsoft.PowerShell.SDK 7.7.x | Targets net11.0 |
| Dependencies | Latest stable everywhere | Central versions in `Directory.Packages.props`; Dependabot weekly PRs for NuGet, the SDK and GitHub Actions |
| UI | Blazor components + **Tailwind CSS v4** (npm `@tailwindcss/cli`, built by MSBuild; Node needed at build time only) | Bootstrap 5 is the zero-build alternative if you'd rather skip a CSS build step. For a small app either works; Tailwind gives you the "modern" look more easily. |
| Icons | Lucide or Heroicons (inline SVG) | Replaces Font Awesome |
| Code editor (Terminal) | Monaco editor via a small JS interop (or `BlazorMonaco`) | PowerShell syntax highlighting built in; replaces jQuery Terminal + Prism |
| Data | EF Core 11 + SQLite (swappable to SQL Server/Postgres) | |
| Auth | ASP.NET Core Identity (EF Core store) + external providers: Entra ID (OIDC), Google, generic OIDC, optional Windows/Negotiate | See section 7 |
| Logging | Serilog → console + file; optional OpenTelemetry | |
| Health | `/healthz` | For Docker/IIS probes |

### Execution engine redesign (`SpecOps.Core/Execution`)

The current hub runs scripts inline during the SignalR call. In v2:

- **`ScriptRunner` (singleton)** starts a run, returns a `RunId` immediately, and runs the pipeline on a background task using `ps.InvokeAsync(input, output)` (no `.Wait()`).
- Output goes through a **`Channel<OutputRecord>`** per run. The Blazor page subscribes to it, and so does the DB writer that persists output. **Runs survive a page refresh or disconnect**: reopen `/runs/{id}` and you see the live tail.
- **Cancel = `ps.StopAsync()`**, which actually stops the pipeline. It is allowed only for the owner of the run or an admin.
- **`RunspacePoolCache`**: one opened pool per runspace profile, kept warm and disposed on shutdown or config change.
- **Per-script timeout** (default e.g. 30 min) and a **global concurrency limit**.
- **Impersonation**: when enabled, use a dedicated single runspace created and invoked inside `WindowsIdentity.RunImpersonatedAsync`, with `PSThreadOptions.UseCurrentThread`, instead of a shared pool. Cover this with an integration test.
- **Authorization at the point of execution**: `runner.StartAsync(user, scriptId, params)` checks that the user's policies include the script's category. The UI filtering is just convenience.
- **Server-side validation**: params are validated against the definition (required, pattern, min/max, options), converted to proper .NET types (int, DateTime, bool, string[]), unknown keys are rejected, and `SpecOps*` keys are reserved.
- **Secret parameters** (`Type: "password"`): masked input, never logged, never stored in run history.
- **Structured output**: if the pipeline emits objects with properties, show them as a **sortable table** (plus CSV/JSON download) instead of `.ToString()`. This is probably the biggest usability improvement for non-technical users.

---

## 6. The database question

**Recommendation: yes, SQLite + EF Core, in two steps.**

### Step A — run history & audit (high value, low risk)
```
ScriptRun        Id, ScriptId, ScriptName(snapshot), User, ClientIp, StartedAt, EndedAt,
                 Status(Running|Succeeded|Failed|Cancelled|TimedOut), ParametersJson(redacted),
                 ErrorCount, WarningCount
ScriptRunOutput  RunId, Seq, Timestamp, Stream, Text           (retention: N days, configurable)
Favorite         User, ScriptId
```
This adds "My recent runs", "Re-run with the same parameters", an admin audit log, and shareable run links. It's all new capability, and none of it fits well in JSON.

### Step B — scripts, categories and policies in the DB (do it, but keep JSON import/export)
```
Category         Id, Name, Description, Icon, SortOrder
Script           Id, CategoryId, Name, Summary, SourceType(File|Inline), Path, InlineBody,
                 RunAs(Service|Impersonate), RunspaceProfileId?, TimeoutSeconds, IsEnabled,
                 ParametersJson (overrides on top of discovered params)
RunspaceProfile  Id, Name, ExecutionPolicy, Min, Max, ModulesJson
Policy           Id, Name, IsAdmin, PrincipalsJson (AD groups or OIDC claims), CategoryIds
```
- **Admin UI** to manage all of it, and to **import `scriptsettings.json`/`appsettings` SecurityPolicies** on first run, so migration is one click.
- **Export to JSON**, so you can still keep config in git if you like ("config as code").
- **Dynamic authorization**: replace the startup-time `AddPolicy` loop with an `IAuthorizationHandler` that reads policies from the DB with a short (≈1-minute) cache. Policy changes then take effect without a restart.

**When it *would* be overkill:** if you'll only ever have about 10 scripts and you like editing JSON in git. In that case do Step A only and keep config in files. The design allows that: an `IScriptCatalog` interface with a JSON implementation and a DB implementation.

### Script parameter auto-discovery
Use `System.Management.Automation.Language.Parser.ParseFile` to read the script's `param()` block and comment-based help:

| PowerShell | Generated UI |
|---|---|
| `[Parameter(Mandatory)]` | required |
| `[int]`, `[datetime]`, `[switch]`, `[string[]]` | number / datetime-local / toggle / tag list |
| `[ValidateSet('a','b')]` | dropdown |
| `[ValidatePattern('…')]` | pattern |
| `[ValidateRange(1,10)]` | min/max (slider) |
| `.SYNOPSIS` / `.PARAMETER x` help | summary / field description |
| `[SecureString]` | secret field |

Adding a script then means pointing at a `.ps1` file: the form builds itself, and the JSON/DB only stores overrides (friendly labels, placeholders, dropdown display text). This removes most of the config work the current app requires.

---

## 7. Cross-platform hosting & authentication

Goal: one codebase, one build, running equally well as a **Linux container** or **on Windows (IIS / Windows Service / Windows container)**. Nothing core depends on Windows; Windows-specific features are *capabilities* the app detects at startup.

### 7.1 Platform capabilities

| Capability | Linux / Docker | Windows | How v2 handles it |
|---|---|---|---|
| Run PowerShell 7 scripts | ✅ | ✅ | Core feature, the same everywhere |
| Windows-only modules (ActiveDirectory, GroupPolicy, Exchange mgmt…) | ❌ | ✅ | Script gets `Platforms: [Windows]`; on other hosts it's shown greyed out with an explanation, and the runner refuses it |
| Impersonation (run as the signed-in user) | ❌ | ✅ (Windows sign-in only) | Only offered when the host is Windows **and** the user signed in with Windows auth |
| Reaching Windows/AD from Linux | ✅ via PS remoting over SSH/WinRM, Graph API, REST | ✅ | A **credential vault** (below) gives scripts `PSCredential`s without hard-coding them |

- `IHostCapabilities` is checked at startup and shown on an Admin "System" page (OS, pwsh version, installed modules, available features).
- Config validation at startup warns (not crashes) about scripts that can't run on this host.
- **Credential vault:** admins store named credentials (encrypted with ASP.NET Data Protection, keys persisted to `/data`). A script declares `Credentials: ["AdServiceAccount"]` and receives `$SpecOpsCredentials.AdServiceAccount` as a `PSCredential`. On Linux this replaces most of what impersonation did; on Windows it's a cleaner alternative to "run as the app pool".
- File paths: always `Path.Combine` and a configurable `ScriptsRoot` (default `/data/scripts` in the container, `.\Scripts` on Windows). Lowercase file names, and no case-sensitivity surprises (bug #11).
- **CI runs the test suite on both `ubuntu-latest` and `windows-latest`**, so platform regressions show up right away.

### 7.2 Packaging

- **Linux image** (primary container): multi-stage `Dockerfile` on `mcr.microsoft.com/dotnet/aspnet:11.0`. The PowerShell SDK ships with the app, so no separate pwsh install is needed for hosted runspaces. Extra modules are installed at image build (`Install-PSResource` from a `modules.txt`) or at runtime into `/data/modules`. `/data` is a volume for SQLite, scripts, modules, logs and DataProtection keys. `compose.yaml` included, non-root user, `/healthz` health check.
- **Windows**: `dotnet publish` → IIS or Windows Service (zip artifact on each release). A Windows container image is optional/later; the demand for it is small.
- **HTTPS.** OAuth providers require `https` redirect URIs for anything except `localhost`, so this is needed for real use, not optional. Two supported setups:
  - **Reverse proxy terminates TLS (recommended for Docker):** Caddy (automatic Let's Encrypt, simplest), Traefik, nginx, or IIS/ARR on Windows. The app listens on plain HTTP inside the container network.
  - **Kestrel serves HTTPS directly:** certificate path (PFX or PEM) set in config/env vars. Fine for a small Windows or VM install with no proxy.
- **App-side proxy support (small, Phase 1):** `UseForwardedHeaders` with a configured `Proxy:KnownNetworks` allowlist, so the app trusts `X-Forwarded-For/Proto/Host` only from the proxy. Then OAuth redirect URIs, secure cookies, HTTPS redirection and `SpecOpsCurrentUserIP` are all correct. This replaces the v1 `ClientIpEnricher`, which read `X-Forwarded-For` from anyone and so let users spoof their IP in the logs. Optional `PathBase` setting for hosting under a sub-path (`https://host/specops`).
- **WebSockets through the proxy:** Blazor Server and SignalR need them. Caddy and Traefik pass them through automatically; the nginx example includes the `Upgrade`/`Connection` headers, and the IIS guide covers enabling the WebSocket Protocol feature.
- **Local dev:** `dotnet dev-certs https --trust`.

### 7.3 Authentication: ASP.NET Core Identity + external providers

**Yes, Identity is a good fit**, mainly because we're adding a database anyway and Google (and most social logins) send **no group information**. Something has to store "this person is a HelpDesk user", and Identity's user/role tables are the standard place for that.

How it fits together:

```
 Sign in with ──► Microsoft Entra ID (OIDC)  ─┐
                  Google                     ├─► ASP.NET Core Identity ──► SpecOps user (DB)
                  Generic OIDC (Keycloak,    │     • external logins linked to one user
                  Authentik, Okta…)          │     • app roles  (assigned in Admin UI)
                  Windows / Negotiate (opt.) ─┘     • status: Pending | Active | Disabled
                                                          │
                              Policy grants categories ◄──┘ to roles AND/OR external groups
```

Details and gotchas:

- **Microsoft: use OpenID Connect (`Microsoft.Identity.Web` or `AddOpenIdConnect`), not `AddMicrosoftAccount`.** `AddMicrosoftAccount` is designed around personal Microsoft accounts; OIDC against your tenant gives work accounts, tenant restriction, and **group/app-role claims**.
- **Google:** `AddGoogle()`. Identity only, no groups, so access comes from app roles.
- **Generic OIDC:** one config block per provider (Authority, ClientId, Secret, optional group-claim name). Great for self-hosters running Keycloak/Authentik.
- **Windows auth as a provider:** Negotiate can be used as an external login into Identity, so Windows users get the same user record, roles and history. This is the only path where impersonation is allowed. Enabled by config, Windows hosts only (Kerberos on Linux is possible but not worth supporting at first).
- **Providers are enabled purely from config**: anything without a ClientId is hidden from the login page. No recompiling to add Google.
- **Policies grant categories to principals of two kinds:**
  - *App roles*, managed in the Admin UI. Works with every provider.
  - *External groups*: an Entra group object ID, an OIDC `groups` claim value or an AD group (`DOMAIN\group`). These are read at sign-in from claims and **refreshed on every sign-in**, so organizations can keep managing access in Entra/AD like today.
- **Locking the front door (important):** with Google enabled, *anyone* with a Google account can sign in. Defaults:
  - New external users start as **Pending** with no access; admins approve them (shown with a badge in Admin).
  - Optional auto-approve rules: allowed email domains (`@contoso.com`) or allowed tenant IDs.
  - Users matched by a group-based policy are auto-approved (their org already vouched for them).
- **First admin:** `Auth:BootstrapAdmins: ["you@example.com"]` in config/env var. Those users get the Admin role on first sign-in, so there's no chicken-and-egg problem.
- **No local username/password accounts.** External providers (plus Windows sign-in) cover real deployments, and skipping local accounts avoids email confirmation, password resets, lockout and password storage entirely.
- **Development sign-in:** a "Sign in as dev user" scheme that is registered **only when `ASPNETCORE_ENVIRONMENT=Development`**, with a startup guard that refuses to boot if it's enabled in any other environment. You can run and test locally (and in CI) without registering OAuth apps.
- **Lockout recovery:** if the identity provider is misconfigured and no admin can sign in, fix the provider config or add yourself to `Auth:BootstrapAdmins` and restart. No hidden backdoor account.
- **UI:** the Blazor "Individual accounts" template scaffolds Identity pages as Blazor components; we keep only login, external-login callback, profile and logout, and restyle them in Tailwind. No default Bootstrap Identity UI.
- **Authorization in code** stays one place: `ISpecOpsAuthorizer.CanRun(user, script)` → user's roles + group claims → policies → categories. The UI, the runner and the API all use it (fixes bug #1 by design).
- **API/webhook triggers (Phase 5)** use per-script API keys, separate from user sign-in.

Data model additions (Identity's tables plus):
```
Policy           …, Roles[] (app roles), ExternalGroups[] (provider + group id/name)
UserProfile      UserId, DisplayName, Status(Pending|Active|Disabled), LastSignInAt
Credential       Id, Name, Username, EncryptedSecret, Description   (vault)
Script           …, Platforms[] (Any|Windows|Linux), Credentials[]
```

---

## 8. UI/UX outline

- **Layout**: slim top bar (logo, global script search with ⌘K, user menu, light/dark toggle) + collapsible left nav (Scripts, History, Terminal*, Admin*). (*admin only)
- **Scripts home**: cards grouped by category, with search, favorites pinned at the top, and "recently run".
- **Script page** `/scripts/{category}/{script}`: summary + auto-generated form on the left; live output console on the right/bottom, with stream filter chips (Info/Warning/Error/Verbose…), copy, download, auto-scroll, and a "Results" tab for structured objects.
- **Run page** `/runs/{id}`: status, duration, parameters (redacted), and full output. Live if the run is still going. Shareable.
- **History**: filterable table (mine / all for admins), status badges, re-run button.
- **Terminal** (admin): Monaco editor + Run (Ctrl+Enter) + output pane, a runspace-profile picker, an impersonation toggle, and an optional **persistent session** (keeps one runspace per user so variables carry between commands, like a real REPL).
- **Admin**: Scripts / Categories / Policies / Runspace profiles CRUD, JSON import/export, settings (retention, timeouts).
- Keep the Obsidian terminal colors; use proper `aria` labels and keyboard support throughout.

---

## 9. Phased roadmap

Each phase ends with something working and committable.

### Phase 0 — Housekeeping (½ day) ✅ done
- No v1 hotfixes; everything in section 3 is fixed by the v2 overhaul.
- Tag the archive branches, create `v2` from `master`, remove old `SpecOps/`, scaffold the solution layout above.
- Add `Directory.Build.props` (nullable on, warnings as errors, analyzers), `.editorconfig`, and an updated `.gitignore` (`logs/`, `*.db`, `node_modules/`).

### Phase 1 — Parity on the new stack (≈ 1–2 weekends)
- Blazor Web App + Tailwind + layout + dark mode.
- EF Core + SQLite brought forward (Identity needs it). ASP.NET Core Identity with Entra OIDC + Google + generic OIDC from config, Pending/approval flow, bootstrap admins. Policies from JSON for now, mapped to roles and external groups, with authorization enforced in the runner.
- Host capability detection + `Platforms` on scripts. CI on Linux **and** Windows from day one.
- Forwarded-headers/trusted-proxy config, optional `PathBase`, and Kestrel HTTPS cert config (needed for OAuth outside localhost).
- Execution engine (section 5), with real cancel, pool cache, server-side validation, and output rendered as text (fixes XSS by construction).
- Script runner page with deep links, and the Terminal page with Monaco.
- Serilog, health check, xUnit tests for the runner, validation and authz.
- **Milestone:** every scenario in `Demo.ps1` works on both Linux (`dotnet run` / container) and Windows; bugs #1–#11 are closed.

**Status: ✅ done on `v2`** (verified on Windows; Linux runs in CI). What changed from the plan along the way:
- Tailwind comes from npm (`@tailwindcss/cli`, built automatically by an MSBuild target) instead of the standalone binary, so Dependabot keeps it current. Node is a build-time requirement only.
- Built-in context arrives as a `$SpecOps` variable (`$SpecOps.User`, `.ClientIp`, `.RunId`, `.ScriptId`) instead of injected `SpecOpsCurrentUser*` parameters, so scripts can use strict `[CmdletBinding()]` param blocks.
- Script files are invoked as files (`$PSScriptRoot` works); a PowerShell quirk (multi-statement pipelines hang on runspace pools) shaped the runner's single-pipeline design.
- The built-in **Admin** role always means full admin, even with no policies configured.
- Impersonation moves to Phase 2 with the Windows sign-in provider it depends on; scripts flagged `EnableImpersonation` are refused with a clear message until then.
- Runs live in memory (4h retention) until Phase 2 adds the database history.

### Phase 2 — History & credentials (≈ 1 weekend)
- Run history (persist `ScriptRun` + output to the DB), re-run with the same parameters, favorites, retention job.
- Impersonation (Windows host + Windows sign-in).
- Credential vault + `$SpecOpsCredentials` injection.
- Optional Windows/Negotiate provider wired into Identity.

### Phase 3 — Smarter config (≈ 1–2 weekends)
- AST parameter auto-discovery.
- Admin CRUD UI, JSON import/export, DB-backed dynamic authorization (no more restarts).
- Structured-output results grid + CSV/JSON export.

### Phase 4 — Ship it (≈ 1 weekend)
- Hardened Dockerfile + compose (non-root, health check, `/data` volume), startup config validation.
- Ready-to-use HTTPS examples: `compose.caddy.yaml` (automatic Let's Encrypt), a Traefik labels example, an nginx config, and an IIS/ARR guide.
- GitHub Actions: build + test on Linux and Windows for each PR; publish the container image to GHCR on tag; publish a zipped Windows/IIS artifact on release.
- Rewrite the README with new screenshots and both deployment guides.

### Phase 5 — Optional extras
- **Scheduled runs** (cron-style, via a hosted background service; Quartz.NET only if needed).
- **Webhook/API trigger** with per-script API keys (lets other systems call SpecOps).
- **Notifications** on completion/failure (email, Teams/Slack webhook).
- **Approval workflow** for dangerous scripts ("needs a second admin to approve").
- OpenTelemetry traces/metrics.

---

## 10. Decisions for you

1. **Blazor Server vs. Razor Pages + htmx**: Blazor is recommended (C#-only, built-in streaming). Choose htmx if you want plain server-rendered pages and very little interactivity framework.
2. **Tailwind vs. Bootstrap 5**: Tailwind is recommended for the look. Choose Bootstrap if you want no CSS build step at all.
3. **Config in DB (Step B) or JSON only + DB for history (Step A)?** Both are recommended; Step A is the must-have.
4. ~~Is OIDC/container hosting something you'd actually use?~~ **Decided:** cross-platform with Linux and Windows both first-class; ASP.NET Core Identity + external providers.
5. **New-user default:** require admin approval (recommended), or auto-approve by email domain/tenant?
6. ~~Local password/passkey accounts?~~ **Decided:** none. Development-only dev sign-in for local testing.
