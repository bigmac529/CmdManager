# CmdManager

CmdManager keeps your library of command-line scripts in one place and puts it on the `PATH` of every Windows PC you use. It is the successor to [ExeLibrary](https://github.com/bigmac529/ExeLibrary).

| Part | Project | Target |
|---|---|---|
| Desktop client (WPF, ClickOnce) | `src/CmdManager.Client` (assembly `CmdManager.exe`) | net10.0-windows |
| Web API (ASP.NET Core, EF Core 10, SQL Server) | `src/CmdManager.Api` | net10.0 |
| Shared contracts, API client, path/PATH/PATHEXT logic, three-way sync | `src/CmdManager.Core` | net10.0 |
| Tests (xUnit) | `tests/CmdManager.Core.Tests`, `tests/CmdManager.Api.Tests` | net10.0 |

**Where the ideas come from.** ExeLibrary kept tools in a shared folder. [CMDs](https://github.com/bigmac529/CMDs) holds the actual `.cmd`/`.bat`/`.csx` scripts. MkNewCmd and CSXScripts supplied the "new command from a template" flow and the `.csx` + `.cmd` wrapper pattern. CmdManager moves the library into SQL Server behind an authenticated API. Each PC then mirrors that library into a local folder on `PATH`, so every script you save runs from any console.

```
                                   Cloudflare ─► IIS site cmdmanager.socha3.com
 browser ──── install/update ────► https://cmdmanager.socha3.com/       C:\WebApps\CmdManager       pool CmdManagerWeb (static ClickOnce files)
 CmdManager.exe ── HTTPS/JWT ────► https://cmdmanager.socha3.com/api/   C:\WebApps\CmdManager\api   pool CmdManager (IIS application /api, ASP.NET Core in-process)
   │ mirrors to %LOCALAPPDATA%\CmdManager\cmds                                                        │ EF Core 10, Windows auth
   └─ folder on user PATH, CMDS=<folder>, .CSX in PATHEXT                                              └─► SQL Server 2025 Express, DB "CmdManager"
```

**URL layout.** The API is the IIS application `/api`, and ANCM (in-process) passes `PathBase=/api` to the app. The app's own routes therefore have **no** `/api` prefix (`/health`, `/auth/login`, `/commands`, …), while the public URLs do (`https://cmdmanager.socha3.com/api/health`, `…/api/auth/login`). Under Kestrel (local dev and tests) the app applies the same `/api` prefix itself (`Hosting:PathBase`, default `/api`). That step is skipped when the host has already set a PathBase, so under IIS it is never applied twice (`/api/api/...` gets no route). The client's base URL is `https://cmdmanager.socha3.com/api/` (trailing slash), and every request URI is relative with no leading slash (`auth/login`). The client rejects a leading slash, because it would drop `/api/`.

## Build and test

```bash
dotnet build CmdManager.sln -c Release
dotnet test CmdManager.sln -c Release
```

The WPF project sets `EnableWindowsTargeting`, so the whole solution builds (the client does not run) on Linux and macOS too. That is how `.github/workflows/ci.yml` runs.

## Running the API locally

- **LocalDB** (Windows): `dotnet run --project src/CmdManager.Api --launch-profile http`. This uses `appsettings.Development.json`: database `CmdManager_Dev` on `(localdb)\MSSQLLocalDB`, a dev-only JWT key, and migrations applied at startup.
- **SQLite** (any OS, no SQL Server): `dotnet run --project src/CmdManager.Api --launch-profile sqlite`. The schema is created with `EnsureCreated` in `cmdmanager-dev.db` (git-ignored). Migrations are SQL Server only.
- Everything is served under `/api` locally too, e.g. `http://localhost:5066/api/health` (point the client at `http://localhost:5066/api/`). Unprefixed paths (`/health`) also answer under Kestrel.
- In Development, Swagger UI is at `/api/swagger` and the OpenAPI document at `/api/openapi/v1.json`. Neither is mapped outside Development.

### Configuration keys

Set these in `appsettings.Production.json` next to the DLL (never committed and excluded from deploys) or as environment variables:

| Key (env var) | Default | Notes |
|---|---|---|
| `ConnectionStrings:CmdManager` (`ConnectionStrings__CmdManager`) | *(empty)* | Production: `Server=.;Database=CmdManager;Trusted_Connection=True;TrustServerCertificate=True` |
| `Jwt:Key` (`Jwt__Key`) | *(empty)* | **Required**, at least 32 characters. The app refuses to start without it. Generate: `[Convert]::ToBase64String((1..48 \| % { Get-Random -Max 256 }))` |
| `Jwt:Issuer` / `Jwt:Audience` | `CmdManager` / `CmdManager.Client` | |
| `Jwt:AccessTokenMinutes` / `Jwt:RefreshTokenDays` | 60 / 30 | Refresh tokens rotate on every use and are stored hashed. |
| `Auth:AllowRegistration` (`Auth__AllowRegistration`) | `true` | Set to `false` after creating your account. |
| `Auth:MinPasswordLength`, `MaxFailedLogins`, `LockoutMinutes`, `RateLimitPerMinute` | 8, 5, 5, 30 | The rate limit applies per IP to `/api/auth/*`. |
| `Hosting:PathBase` (`Hosting__PathBase`) | `/api` | Prefix applied under Kestrel. Under IIS, ANCM already sets PathBase=/api from the application path and the setting has no effect. `""` disables it. |
| `Database:Provider` (`Database__Provider`) | `SqlServer` | `Sqlite` for dev only |
| `Database:MigrateOnStartup` (`Database__MigrateOnStartup`) | `true` | Runs `MigrateAsync` at startup and logs each migration applied. Needs `db_ddladmin`. |
| `Library:MaxFileBytes` (`Library__MaxFileBytes`) | 104857600 (100 MB) | Per file. |
| `Library:MaxRequestBytes` | 0 = derived (~1.4 × MaxFileBytes + 1 MB) | Covers base64 JSON overhead. Also raise `requestLimits` in `web.config` (200 MB) if you raise this. |

## Server setup (web server, one time)

IIS layout: one site, two app pools.

| URL | Folder | IIS | App pool |
|---|---|---|---|
| `https://cmdmanager.socha3.com/` | `C:\WebApps\CmdManager` | site **CmdManager** (static: ClickOnce `CmdManager.application`, `setup.exe`, `Application Files\`) | **CmdManagerWeb**, No Managed Code |
| `https://cmdmanager.socha3.com/api/` | `C:\WebApps\CmdManager\api` | **application** `/api` under that site (ASP.NET Core in-process) | **CmdManager**, No Managed Code, identity `IIS APPPOOL\CmdManager` |

1. Install the **ASP.NET Core 10.0 Hosting Bundle** (10.0.1 is installed) and restart IIS (`iisreset`).
2. Create the app pools **`CmdManagerWeb`** (static root) and **`CmdManager`** (API). Both use .NET CLR version *No Managed Code* and *ApplicationPoolIdentity*.
3. Create the site **`CmdManager`** at `C:\WebApps\CmdManager` with pool `CmdManagerWeb`, bound to `cmdmanager.socha3.com` (HTTPS, or HTTP behind Cloudflare, depending on your Cloudflare SSL mode). Then add the **application** `/api` → `C:\WebApps\CmdManager\api` with pool `CmdManager`. Grant `IIS APPPOOL\CmdManager` read access to the api folder, plus modify on `api\logs\` if you enable stdout logs.
4. **Root `web.config`:** anything in the root's `<system.webServer>` is inherited by the `/api` application unless it is wrapped in `<location path="." inheritInChildApplications="false">`. That includes handlers, static-content rules, MIME maps for `.application`/`.manifest`/`.deploy`, and rewrite rules. The API's own `web.config` (from `dotnet publish`) wraps its settings the same way. It also removes IIS's `WebDAVModule` module and `WebDAV` handler for `/api`. If the server has the WebDAV Publishing feature installed, those intercept `PUT`/`DELETE` and return an IIS `405` page (`Allow: GET, HEAD, OPTIONS, TRACE`), which breaks saving, renaming and deleting.
5. Database (SQL Server 2025 Express, default instance, Windows auth). Run as a sysadmin:
   ```cmd
   sqlcmd -S . -E -i db\server-setup.sql
   ```
   This creates the `CmdManager` database and the login and user `[IIS APPPOOL\CmdManager]` with `db_datareader`, `db_datawriter` and `db_ddladmin`. The schema then comes from the app's startup migration. If you set `Database:MigrateOnStartup=false`, you can remove `db_ddladmin` and apply the idempotent script yourself:
   ```cmd
   sqlcmd -S . -E -d CmdManager -i db\CmdManager-schema.sql
   ```
   *Express limit:* each database is capped at **50 GB** of data (SQL Server 2025 Express). File contents live in the database (`varbinary(max)`/`nvarchar(max)`), so that cap is the effective library size.
6. Create `C:\WebApps\CmdManager\api\appsettings.Production.json` with the connection string and `Jwt:Key` (see above). The deploy never overwrites it.
7. Check `https://cmdmanager.socha3.com/api/health`. It returns **200** with `{"status":"Healthy", ... "connected":true,"pendingMigrations":0}`. It returns **503** if the DB is unreachable or migrations are pending.

**Cloudflare:** Cloudflare Free and Pro plans reject request bodies over **100 MB** (413). A command upload is JSON with base64 content, so through Cloudflare the practical per-file limit is about 70 MB for commands and a little under 100 MB for multipart asset uploads. It is effectively lower than `Library:MaxFileBytes`. Cloudflare's proxy timeout (100 s) also applies to slow uploads.

## Deploying the API (GitHub Actions)

`.github/workflows/deploy-api.yml` runs on pushes to `main` that touch `src/CmdManager.Api/**` or `src/CmdManager.Core/**`, and on manual *Run workflow*. It runs on the **self-hosted Windows runner** on the web server (`runs-on: [self-hosted, windows, cmdmanager]`) in the GitHub **environment `production`**, so any protection rules on that environment (required reviewers, branch restrictions) apply. It only ever writes to the api folder.

Repository variables (Settings → Secrets and variables → Actions → *Variables*):

| Variable | Value | |
|---|---|---|
| `DEPLOY_PATH` | `C:\WebApps\CmdManager\api` | Required. The job fails unless it is set and ends in `\api`. |
| `DEPLOY_BACKUP_ROOT` | `C:\WebApps\_deploy-backups\CmdManager` | Optional. When set, a backup is taken before every deploy. |

Steps:

0. **Guard:** fail unless `DEPLOY_PATH` is non-empty and ends in `\api`, so `/MIR` can never purge the site root that holds the ClickOnce files.
1. Build, test and `dotnet publish` the API.
2. **Backup** (only if `DEPLOY_BACKUP_ROOT` is set): robocopy the current api folder to `<DEPLOY_BACKUP_ROOT>\<yyyyMMdd-HHmmss>`, then delete all but the **newest 5** backups. The copy includes `appsettings.Production.json`, so a backup can be restored as-is. File names and contents are not logged (`/NFL /NDL`). The runner account needs modify rights on the backup root.
3. Write `app_offline.htm` into the api folder (the ASP.NET Core Module stops the app and releases file locks).
4. `robocopy /MIR` the publish output into the api folder with `/XF appsettings.Production.json app_offline.htm /XD logs`. Robocopy exclusions apply to the purge as well as the copy, so the server's `appsettings.Production.json` is never overwritten and never deleted by `/MIR`. Exit codes below 8 count as success.
5. Remove `app_offline.htm`.
6. Poll `https://cmdmanager.socha3.com/api/health`, falling back to `http://localhost/api/health` with `Host: cmdmanager.socha3.com`, up to 20 times. The job fails if it never gets a 200.

**Restoring a backup:** take the API offline by putting `app_offline.htm` in the api folder. Then run `robocopy <backup> C:\WebApps\CmdManager\api /MIR /XF app_offline.htm /XD logs` and delete `app_offline.htm`.

> **The job stays queued until a runner exists.** Register a **repo-scoped** runner for `bigmac529/CmdManager` with the extra label **`cmdmanager`** (Settings → Actions → Runners → New self-hosted runner → Windows). Run it as a service under a **non-admin** local account. That account needs modify rights on `C:\WebApps\CmdManager\api` and the backup root, plus the site root `C:\WebApps\CmdManager` for the client publish (below), and nothing else. `actions/setup-dotnet` installs the .NET 10 SDK into the runner's tool cache. The scripts run in Windows PowerShell 5.1 (`shell: powershell`), so PowerShell 7 is not required.

## Adding a migration

Migrations live in `src/CmdManager.Api/Data/Migrations` and are checked in. `dotnet-ef` is pinned in `.config/dotnet-tools.json`.

```bash
dotnet tool restore
# 1. change the entities / CmdManagerDbContext, then:
dotnet tool run dotnet-ef migrations add <MigrationName> -p src/CmdManager.Api -s src/CmdManager.Api -o Data/Migrations
# 2. regenerate the idempotent SQL script (checked in for manual deploys):
dotnet tool run dotnet-ef migrations script --idempotent -p src/CmdManager.Api -s src/CmdManager.Api -o db/CmdManager-schema.sql
# 3. sanity check: should print "No changes have been made to the model since the last migration."
dotnet tool run dotnet-ef migrations has-pending-model-changes -p src/CmdManager.Api -s src/CmdManager.Api
```

The design-time factory uses the connection string from `ConnectionStrings__CmdManager` (defaulting to LocalDB), but `migrations add` and `script` do not connect. On the next deploy the app applies the new migration at startup (`Database:MigrateOnStartup`), and `/api/health` stays 503 until it has been applied.

## Desktop client

### Publishing (ClickOnce)

**Install:** <https://cmdmanager.socha3.com/> (landing page with an *Install* button that opens `CmdManager.application`; `setup.exe` is the fallback for browsers without ClickOnce support).

`.github/workflows/publish-client.yml` publishes on every push to `main` that touches `src/CmdManager.Client/**`, `src/CmdManager.Core/**`, `deploy/clickonce/**` or the workflow, and on manual *Run workflow*. Pull requests run the build job only.

1. **build** (GitHub-hosted `windows-latest`, Visual Studio 2026 MSBuild; `dotnet publish` cannot produce ClickOnce): `msbuild /restore /t:Publish /p:PublishProfile=ClickOnceProfile /p:CmdManagerBuildNumber=<run number>`, then renders `deploy/clickonce/index.html` (version, date, size) into the output and uploads it as the `clickonce-site` artifact.
2. **deploy** (self-hosted runner, environment `production`, `main` only). The site root is the parent of `DEPLOY_PATH`, i.e. `C:\WebApps\CmdManager`. The job:
   - backs up the root **without `api`** to `<DEPLOY_BACKUP_ROOT>\site-root\<yyyyMMdd-HHmmss>` and keeps the newest 5;
   - copies the new `Application Files\CmdManager_1_0_<N>_0` folder in, then the top-level files, and `CmdManager.application` last (that file switches clients to the new version);
   - mirrors (`/MIR`) **only** `Application Files`, which holds nothing but ClickOnce output, to drop old versions. It then deletes top-level files that an earlier publish deployed and this one no longer ships (tracked in `<DEPLOY_BACKUP_ROOT>\site-root\deployed-root-files.txt`). There is no `/MIR` on the root, so `api\`, the root `web.config` and anything else owned by the server are never touched;
   - checks `/`, `CmdManager.application`, the application manifest, `.deploy` files, `setup.exe` and `/api/health` over the public URL.

The runner account needs Modify on the site root (in addition to `api` and the backup root).

By hand (Windows, VS 2026 MSBuild):

```cmd
msbuild src\CmdManager.Client\CmdManager.Client.csproj /restore /t:Publish /p:PublishProfile=ClickOnceProfile /p:CmdManagerBuildNumber=<N>
```

> **Never delete the `api` folder when uploading ClickOnce files by hand.** Copy with `robocopy src\CmdManager.Client\bin\publish\clickonce C:\WebApps\CmdManager /E /XD api /XF web.config`, and never `/MIR` the root.

The profile:

- **Framework-dependent win-x64 (about 1 MB):** needs the **.NET 10 Desktop Runtime (x64)** ([download](https://dotnet.microsoft.com/download/dotnet/10.0)). `setup.exe` checks for the runtime and installs it from Microsoft when Visual Studio's `Microsoft.NetCore.DesktopRuntime.10.0.x64` bootstrapper package is on the build machine; the workflow detects the package and turns the prerequisite off if it's missing. The landing page always states the requirement. Launching without the runtime shows Windows' ".NET is required" dialog. `/p:CmdManagerSelfContained=true` bundles the runtime instead (~175 MB). Satellite resources are English only (`SatelliteResourceLanguages=en`).
- Checks `https://cmdmanager.socha3.com/` for updates before startup, and every version is required.
- Version is `1.0.<N>.0`, where N is the workflow run number.
- Manifests are **unsigned**, so Windows shows "Unknown publisher". To sign, pass `/p:SignManifests=true /p:ManifestCertificateThumbprint=...` and always use the same certificate.
- IIS serves `.application` (`application/x-ms-application`), `.manifest` (`application/x-ms-manifest`) and `.deploy` (`application/octet-stream`) with its built-in MIME map.

### First run

1. Log in, or create an account (the API URL defaults to `https://cmdmanager.socha3.com/api/` and can be changed on the login screen). Settings saved by older builds that point at the site root are moved to `/api/` automatically.
2. The client creates the command folder `%LOCALAPPDATA%\CmdManager\cmds`. It adds the folder to the **user** `PATH` (keeping `REG_EXPAND_SZ`) and sets `CMDS` to it. It then sets up `.csx` integration (below) and broadcasts `WM_SETTINGCHANGE`, so new consoles see the changes. The status bar shows `Command folder: … (on PATH)`.
3. Use **Import folder…** to upload an existing folder such as a clone of CMDs. Script kinds (`.cmd .bat .csx .ps1 .rdp`) and other UTF-8 text files become commands, `.exe`/`.lnk` become commands only at the root, and everything else becomes an asset. `bin/obj/.git/.vs` are skipped.
4. The library is then mirrored into the command folder.

**Why `%LOCALAPPDATA%\CmdManager\cmds` and not the install folder?** ClickOnce installs every version into a new hashed folder under `%LOCALAPPDATA%\Apps\2.0\…` and deletes old ones. A `PATH` entry pointing there would break on every update, and files written there would vanish. All client data therefore lives in `%LOCALAPPDATA%\CmdManager`:

- `cmds\`: the command folder (changeable in Settings)
- `settings.json`
- `session.bin`: refresh token, DPAPI CurrentUser
- `sync-state.json`
- `edit\`: temp files for the external editor
- `cmdmanager.log`

**Settings** lets you change the folder, which moves the PATH entry and `CMDS` and re-syncs. You can also add or remove the folder from PATH, and manage `.csx` integration (status, Repair, Remove). The rest of the options are the `.csx` runner, the `.cmd` wrappers toggle, dotnet-script install, the external editor and the background sync interval.

### Authoring

- **New…** offers these templates:
  - **C# scriptlet (.csx)**: a dotnet-script starter, plus an optional `name.cmd` wrapper `dotnet script "%~dp0name.csx" -- %*`
  - Start command (MkNewCmd style)
  - C# library `.csx`
  - PowerShell pair
  - Empty `.cmd`
- Names are validated, and cmd built-ins such as `dir` and `copy` are rejected.
- The editor is AvalonEdit with syntax highlighting (C#, batch, PowerShell), line numbers and a monospace font. **Ctrl+S** saves to the DB and writes the file locally at once. A 409 conflict offers reload or overwrite. **F5 / Run** runs the command with arguments. **Open in external editor** watches the file and reloads it.
- The `.csx` runner is configurable. The default is `dotnet script`, and legacy `scriptcs` is supported. The New dialog and Settings check `dotnet tool list -g` for `dotnet-script` and offer an **Install dotnet-script** button (`dotnet tool install -g dotnet-script`).

### Running `.csx` files directly (per user, no admin)

With integration enabled, typing `hello` in a console runs `hello.csx`:

- **User `PATHEXT`** = machine `PATHEXT` + `.CSX`. A user `PATHEXT` *replaces* the machine value rather than appending to it, so the machine list is copied in (`PathExt` in Core, unit tested).
- **`HKCU\Software\Classes\.csx`** → ProgID `CmdManager.csx`, with `shell\open\command` = `"<path to dotnet.exe>" script "%1" -- %*` and a `DefaultIcon`. `SHChangeNotify(SHCNE_ASSOCCHANGED)` is called afterwards.
- Previous values are backed up in `settings.json`, and **Remove** restores them. **Repair** rewrites everything. The Settings status line reports PATHEXT, the association and dotnet-script. It also warns when an Explorer *UserChoice* override for `.csx` exists (set with "Open with → Always use this app"). That override takes precedence over the HKCU ProgID for double-click, and may also take precedence for console launches. Remove it in Windows Settings → Default apps if `.csx` opens in an editor instead of running.
- `.cmd` wrappers become optional (default **off** when the association works, on otherwise).
- **There is no elevated or machine-wide option.** Everything is written to HKCU and the user environment, so it works without admin rights and applies only to the current user.

### Two-way sync (DB is the source of truth)

`sync-state.json` records every file the app itself wrote to the command folder: path, SHA-256, server `updatedUtc`, type and id. Each path is decided by comparing three hashes: **local file**, **last-synced (base)** and **server manifest**. The decision is `Core/Sync/ThreeWayDiff.Decide`, a pure function with exhaustive tests:

| local vs base | server vs base | Result |
|---|---|---|
| local = server | – | NoOp |
| unchanged | changed / new | **Download** |
| unchanged | deleted | **DeleteLocal** |
| changed / new | unchanged / absent | **Upload** |
| deleted | unchanged | **DeleteRemote** |
| changed | changed (different) | **Conflict** (includes edit/delete, delete/edit, add/add) |

- **Downward (automatic).** Runs on startup, every *N* minutes (default 5), after every save, and on **Sync now**. It applies only the server-wins operations: Download and DeleteLocal. Only files tracked in `sync-state.json` are ever deleted. Untracked files are never touched and appear as **"local only"**. Files edited outside the app are left alone and reported as pending.
- **Upward (manual): "Sync local changes to DB".**
  1. Builds the plan against the manifest and shows a confirmation list: files to upload (edited or added outside the app, e.g. in Notepad) and files to delete from the DB (deleted locally).
  2. Asks about each conflict with **Keep local / Keep server / Keep both**. *Keep both* renames the local file to `name.conflict.ext`, uploads it, and downloads the server version.
  3. Uploads send `If-Match`/`expectedSha256`. If the server changed in the meantime the API returns **409**, and the file is reported as a conflict instead of being overwritten.
- **FileSystemWatcher** on the command folder only updates a "**N local change(s) pending**" indicator (hover it for the list). It never uploads automatically.
- The state file is discarded if the folder, server or user changes. The next sync then treats matching files as in sync and differing ones as conflicts.

### HTTP client and auth behaviour

The client uses a single `HttpClient` with `AuthTokenHandler` (a `DelegatingHandler`). The handler attaches the bearer token and refreshes shortly before expiry, and retries once after a refresh following a 401. If the refresh fails, the session is marked expired: background sync stops and the app returns to the Login window.

## API

Base URL: **`https://cmdmanager.socha3.com/api/`**. The paths below are public paths; the app's own routes are the same without `/api`. All endpoints require a JWT bearer token (fallback authorization policy) **except** `/api/`, `/api/health`, `/api/auth/config`, `/api/auth/register`, `/api/auth/login` and `/api/auth/refresh`. Unknown paths also answer 401 to anonymous callers. Every library query is filtered by the token's user id, so users never see each other's files. The tests assert both rules for every endpoint.

| Method | Path | Notes |
|---|---|---|
| GET | `/api/` | Plain-text banner |
| GET | `/api/health` | 200 Healthy / 503 Unhealthy: DB connected, pending migrations = 0 |
| GET | `/api/auth/config` | `{ allowRegistration, minPasswordLength }` |
| POST | `/api/auth/register` | 201 + tokens. 403 when registration is off, 409 when the name is taken |
| POST | `/api/auth/login` | 200 + tokens. 401 bad credentials, 429 locked out, 403 disabled |
| POST | `/api/auth/refresh` | Rotates the refresh token |
| POST | `/api/auth/logout` | Revokes the refresh token |
| GET | `/api/auth/me` | Current user |
| GET | `/api/commands?search=&kind=&folder=` | Summaries |
| GET | `/api/commands/{id}` | With text content |
| GET | `/api/commands/{id}/content` | Raw bytes |
| POST | `/api/commands` | Create (text or base64 binary). 409 on duplicate path |
| PUT | `/api/commands/{id}` | Update path/content/metadata. `expectedSha256` mismatch → **409** |
| PUT | `/api/commands/{id}/content` | Raw body. `If-Match: "<sha256>"` mismatch → **409** |
| DELETE | `/api/commands/{id}?expectedSha256=` | Mismatch → **409** |
| GET | `/api/assets`, `/api/assets/{id}`, `/api/assets/{id}/content` | |
| POST | `/api/assets` | multipart: `path`, `file`, `description` |
| PUT | `/api/assets/{id}/content` | Raw body, `If-Match` → **409** |
| PUT | `/api/assets/{id}` | Path/description, `expectedSha256` |
| DELETE | `/api/assets/{id}?expectedSha256=` | |
| GET | `/api/library/manifest` | `{ type, id, relativePath, sha256, size, updatedUtc }` for every file (drives sync) |
| POST | `/api/library/import` | Batch create/overwrite with per-item results |
| GET | `/api/library/export` | Whole library as a zip |

Optimistic concurrency: `Sha256` (`char(64)`) is an EF concurrency token. Any update whose expected hash is stale, or that races another writer, returns **409 Conflict**.

## Database schema (EF Core migration `InitialCreate`)

| Table | Columns | Keys / indexes |
|---|---|---|
| `Users` | Id, UserName nvarchar(64), NormalizedUserName nvarchar(64), Email nvarchar(256)?, PasswordHash nvarchar(512) (ASP.NET Identity PBKDF2), CreatedUtc, LastLoginUtc?, AccessFailedCount, LockoutEndUtc?, IsDisabled | unique NormalizedUserName |
| `RefreshTokens` | Id, UserId → Users (cascade), TokenHash varchar(64) (SHA-256 of the token), CreatedUtc, ExpiresUtc, RevokedUtc? | unique TokenHash, index UserId |
| `Commands` | Id, UserId → Users, Name nvarchar(255), Folder nvarchar(400), RelativePath nvarchar(400), PathKey nvarchar(400) (upper-case), Kind varchar(16), Tags nvarchar(1000)?, Description nvarchar(1000)?, IsBinary, TextContent nvarchar(max)?, BinaryContent varbinary(max)?, Size bigint, Sha256 char(64) (concurrency token), CreatedUtc, UpdatedUtc | unique (UserId, PathKey), index (UserId, Kind) |
| `Assets` | same file columns + Content varbinary(max) | unique (UserId, PathKey) |

Paths are unique per user across both tables (enforced in the service). All timestamps are UTC `datetime2`.

## Placeholders / TODO

- Publish the first ClickOnce build to the site root (https://cmdmanager.socha3.com/). Remember `/XD api`.
- ClickOnce manifests are unsigned. Choose a certificate (e.g. the CN=Socha3 one SochaDiff uses) before the first public release.
- Self-hosted runner not registered yet, so deploys queue.
- `C:\WebApps\CmdManager\api\appsettings.Production.json` (connection string + Jwt:Key) must be created on the server by hand.
- No app icon yet, no password change/reset UI, and no admin UI for disabling users (`Users.IsDisabled` exists).
