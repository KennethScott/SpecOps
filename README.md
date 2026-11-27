# SpecOps

### Tactical Scripting

<img src="docs/images/banner.png" width="800">
<br/><br/>

SpecOps is a web app that hosts your **PowerShell 7** scripts so other people, especially non-technical staff, can run
them safely. Admins add a script, describe its parameters, and decide who can run it. Users pick the script, fill in a
generated form, and watch the output live.

It runs on Linux (Docker) and Windows. It's built with .NET 11, Blazor and Tailwind, uses Microsoft Entra ID, Google or
any OpenID Connect provider for sign-in, and keeps its data in SQLite.

> This is v2 (the `v2` branch), a rebuild of the original app. The v1 app (.NET 7, Windows and Active Directory only)
> is preserved at tag `v1-final`.

## Features

- **Forms generated from parameters:** text, text area, number, range slider, date, time, date and time, email,
  password, drop-down, checkbox and stored credential, with required fields, min/max, regex patterns and suggestions.
- **Live output:** every PowerShell stream (output, errors, warnings, information, verbose, debug, progress) streams
  to the browser as it happens. Refreshing the page or reconnecting loses nothing, and runs can be cancelled.
- **Results:** objects a script outputs are also shown as sortable, filterable tables and can be exported to CSV or JSON.
- **Run history:** every run (who, when, parameters, status, output and results) is kept in the database, 90 days
  by default.
- **Access control:** policies map roles and identity-provider groups to script categories. New users wait for an
  admin's approval unless their email domain is auto-approved.
- **Admin pages in the browser:** scripts, parameters, `.ps1` code (Monaco editor), script file uploads, categories,
  policies, runspace profiles and credentials. Includes JSON import/export and version history with restore.
- **Credential vault:** service-account passwords are stored encrypted, and scripts receive them as a `PSCredential`.
- **Terminal:** admins can run ad-hoc PowerShell on the server. Every command is logged in full.
- **Audit log, log viewer and user management:** all in the app.
- **Script worker:** scripts run in a separate process, so a misbehaving script can't take the web app down.

## Getting started

### Docker (recommended)

```bash
cp .env.example .env        # fill in a sign-in provider and your admin
docker compose up -d        # http://<host>:8080
```

[docs/docker.md](docs/docker.md) covers sign-in, HTTPS (Caddy, your own certificates, or an existing proxy), adding
scripts and PowerShell modules, backups, and reaching Active Directory through a Windows jump host.

### Develop or run on Windows

You need the .NET 11 SDK and Node.js 22+ (for the Tailwind and editor builds).

```bash
dotnet run --project src/SpecOps.Web --launch-profile http
```

Open http://localhost:5065 and choose **Sign in as dev user** (Development only). The first start imports the sample
scripts in [samples/specops.json](samples/specops.json) (code in [samples/scripts](samples/scripts)); after that,
manage them under **Configure**.

Tests: `dotnet test` runs the unit, component and browser tests (see [docs/testing.md](docs/testing.md)). If you're
restyling, run `npm run watch:css` in `src/SpecOps.Web`.

## Signing in and users

Sign-in providers go under `Auth:Providers` in [appsettings.json](src/SpecOps.Web/appsettings.json). Providers without
a `ClientId` are ignored. Keep secrets in user-secrets or environment variables (for example
`Auth__Providers__Microsoft__ClientSecret`), never in the file.

| Setting                        | What it does                                                                           |
| ------------------------------ | -------------------------------------------------------------------------------------- |
| `Auth:BootstrapAdmins`         | People who get the Admin role when they sign in (first admin, lockout recovery).       |
| `Auth:AutoApproveEmailDomains` | New users with a verified email in these domains skip the approval queue.              |
| `AllowedTenantIds` (Entra)     | Only accept sign-ins from your tenant. Strongly recommended.                           |
| `GroupsClaim`                  | The claim that carries group IDs, for policies that grant access by group.             |
| `Auth:DevSignIn`               | Adds **Sign in as dev user**. Development only; set in `appsettings.Development.json`. |

Everyone else lands on a "waiting for approval" page. Admins approve people, assign roles and remove users on the
**Users** page.

## Configuration

SpecOps keeps everything in its data directory (`data` next to the app, `/data` in Docker): the database, logs,
Data Protection keys, `specops.json` and the scripts folder. **Back up the whole folder.**

Scripts, categories, policies, runspace profiles and credentials live in the database, and admins manage them under
**Configure**. Every change is validated before it's saved and recorded in version history. On first start, SpecOps
imports them from `specops.json`. After that the file only supplies the `ScriptsRoot` and `Runner` settings, which take
effect without a restart. You can import a `specops.json` again at any time under **Configure > Import & history**.

Invalid `specops.json` or `Auth` settings stop the app at startup with a list of problems.

### Scripts

| Field             | Meaning                                                                                                       |
| ----------------- | ------------------------------------------------------------------------------------------------------------- |
| `Id`              | Unique id, used in the script's URL (`/scripts/{Id}`).                                                        |
| `Name`, `Summary` | What users see.                                                                                               |
| `CategoryId`      | The category the script belongs to; policies grant access by category.                                        |
| `Path`            | The `.ps1` file, relative to `ScriptsRoot` (or absolute).                                                     |
| `Parameters`      | The form fields (below).                                                                                      |
| `RunspaceProfile` | A warm runspace pool to run in. Without one, every run gets a fresh, isolated runspace.                       |
| `Platforms`       | `Windows`, `Linux` and/or `MacOS` (default: any). Scripts that can't run on this host are shown but disabled. |
| `TimeoutMinutes`  | Overrides `Runner:DefaultTimeout`.                                                                            |

### Parameters

Each parameter's `Name` must match the script's `param()` block. Admins can also create the parameter list from the
script's own `param()` block in the editor.

| Field                                 | Meaning                                                                                                                       |
| ------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `Type`                                | `Text`, `TextArea`, `Number`, `Range`, `Date`, `Time`, `DateTime`, `Email`, `Password`, `Select`, `Checkbox` or `Credential`. |
| `Label`, `Description`, `Placeholder` | How the field is presented.                                                                                                   |
| `Required`, `Default`                 | Whether a value is needed, and the initial value.                                                                             |
| `Min`, `Max`, `Step`                  | For `Number` and `Range`.                                                                                                     |
| `Pattern`                             | A regex the whole value must match (text-like types).                                                                         |
| `Options`                             | Choices for `Select` (`Value` and `Label`); for `Credential`, the stored credentials allowed.                                 |
| `Suggestions`                         | Values suggested as you type in free-text fields.                                                                             |
| `Sensitive`                           | Never log or store the value (always on for `Password`).                                                                      |

Values are validated on the server and passed to the script with their proper types: `Number` as `int` or `double`,
`Date` and `DateTime` as `DateTime`, `Time` as `TimeSpan`, `Checkbox` as `bool` (it works with `[switch]`), and
`Credential` as `PSCredential`.

### Policies

A policy grants **categories** (or `*` for all) to **roles** (assigned on the Users page) and/or **groups** (from the
sign-in provider, e.g. Entra group object IDs). Users only see the scripts they're allowed to run. Members of the
built-in **Admin** role can run everything and use the Configure, Terminal, Users, Logs and Audit pages.

### Runspace profiles

These are optional warm runspace pools, which make scripts start faster, especially ones that import large modules.
Each profile has `MinRunspaces`, `MaxRunspaces`, `Modules` to import and an optional `ExecutionPolicy` (Windows). Each
runspace is reset between runs, so variables, functions, aliases, modules and drives don't carry over. Environment
variables are process-wide and are not reset.

### Credentials

Admins store service accounts under **Configure > Credentials** instead of putting passwords in scripts.

- A script declares `[pscredential]$Account`, and its `Credential` parameter lists which stored credentials it may
  use. With just one, users aren't asked to choose.
- Passwords are encrypted with the app's Data Protection keys (`data/keys`) and are never shown again after saving.
- Passwords never appear in logs, run history or the audit log; runs show the account's user name.

Back up `data/keys` together with the database. Without the keys, stored passwords can't be decrypted and must be
entered again.

### Runner settings

These are set under `SpecOps:Runner` in `specops.json`.

| Setting                   | Default      | Meaning                                               |
| ------------------------- | ------------ | ----------------------------------------------------- |
| `MaxConcurrentRuns`       | 10           | Scripts running at once; further runs queue.          |
| `DefaultTimeout`          | 30 minutes   | Time limit for a run when the script doesn't set one. |
| `MaxActiveRunsPerUser`    | 5            | Runs one user can have queued or running.             |
| `MaxRunsPerMinutePerUser` | 20           | Runs one user can start per minute.                   |
| `HistoryRetention`        | 90 days      | How long run history is kept.                         |
| `MaxOutputRecords`        | 50,000       | Output lines kept per run.                            |
| `MaxResultRows`           | 10,000       | Objects kept per run for the Results view and export. |
| `OutputWidth`             | 160          | Console width used to format tables and lists.        |
| `ExecutionPolicy`         | RemoteSigned | PowerShell execution policy (Windows).                |

## Writing scripts

Write ordinary PowerShell with a `param()` block (`[CmdletBinding()]` is fine). `$PSScriptRoot` works, so scripts can
load files next to them. Each stream is shown in its own style:

| PowerShell                                | Shown as    |
| ----------------------------------------- | ----------- |
| `Write-Output` (or just emitting objects) | Output      |
| `Write-Host`, `Write-Information`         | Information |
| `Write-Warning`                           | Warning     |
| `Write-Error`, exceptions                 | Error       |
| `Write-Verbose`                           | Verbose     |
| `Write-Debug`                             | Debug       |
| `Write-Progress`                          | Progress    |

SpecOps's own messages (starting, finished, cancelled) are shown as **System**.

Objects are formatted as they would be in a console, and are also captured as rows for the **Results** view and
export.

Every run's status follows from how the script ends. A terminating error fails the run, and non-terminating errors
finish it as "completed with errors".

Each script also gets a `$SpecOps` variable describing the run:

| Property              | Value                             |
| --------------------- | --------------------------------- |
| `$SpecOps.RunId`      | The run's id (also in its URL).   |
| `$SpecOps.ScriptId`   | The script's id.                  |
| `$SpecOps.ScriptName` | The script's name.                |
| `$SpecOps.User`       | Who started the run.              |
| `$SpecOps.ClientIp`   | The address they started it from. |

See [samples/scripts/Demo.ps1](samples/scripts/Demo.ps1) for every parameter type and output stream.

## Runs and history

The **Runs** page lists your runs; admins see everyone's. Each run has its own page (`/runs/{id}`) with its
parameters, output and results, live while it runs and from history afterwards. Only the person who started a run and admins can open it.

## Terminal

Admins can run ad-hoc PowerShell on the server from the **Terminal** page (Ctrl+Enter runs it). Every command is
written to the log and the audit log in full. Give the Admin role carefully: the Terminal can do anything the
SpecOps account can.

## Logs and audit

- **Logs:** daily log files are written to `data/logs` and kept for 30 days (`SpecOps:LogRetentionDays`). Admins can
  view and download them on the **Logs** page.
- **Audit:** the **Audit** page records sign-ins, approvals and role changes, script and Terminal runs,
  cancellations, configuration and script file changes, credential changes and log downloads.

## How scripts run

Scripts run in a separate **script worker** process, not in the web app. A script that crashes the process (for
example `[Environment]::Exit()` or a faulty native module), or leaks memory, fails only the runs active in that worker.
The web app keeps going, and the next run starts a new worker.

The worker is the same app started with `--script-worker`, under the same account and environment, so scripts can
reach exactly what the SpecOps account can. Its log goes into the web app's log, and it exits when the web app stops.

**Run one instance.** Active runs live in the app's memory and the default database is SQLite, so SpecOps isn't
designed to run behind a load balancer with several instances.

## Credits

Thank you to [@keithbabinec](https://github.com/keithbabinec) for his work with hosted runspaces in his
[PowerShellHostedRunspaceStarterKits](https://github.com/keithbabinec/PowerShellHostedRunspaceStarterkits) project.
