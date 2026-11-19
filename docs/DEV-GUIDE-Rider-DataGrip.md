# Dev guide: maintaining MedStock (inventory) with Rider and DataGrip

Audience: maintainer (senior .NET dev). Tools: JetBrains Rider + DataGrip, GitHub, GitHub Actions. Menu names vary by Rider version; use **Search Everywhere (double Shift)** or **Ctrl+Shift+A** if a path differs. No secret values are in this file; never paste them into commits, issues or chat.

## 0. At a glance

| | MedStock (inventory) |
|---|---|
| Solution | `MedStock.slnx` |
| Web project | `src/MedStock.Web/MedStock.Web.csproj` |
| GitHub repo | `MiguelAlonso1/medstock-inventory` (public) |
| Local URL | http://localhost:5120 |
| Local DB | `src/MedStock.Web/App_Data/medstock.db` (SQLite, created and seeded on first run) |
| Health endpoint | `/health` |
| Workflow | `.github/workflows/ci-cd.yml` |
| Version | 1.3.1 (`<Version>` in the web csproj) |
| AI feature | Ask MedStock |

Demo logins (public on purpose, synthetic data, also in the README): `admin@medstock.demo`, `manager@medstock.demo`, `picker@medstock.demo`, `viewer@medstock.demo`, password `MedStock-Demo-2026!`.

Stack: .NET SDK 11.0.100 (`net11.0`), Blazor Web App (Interactive Server), MudBlazor 9.13.0, EF Core + SQLite, xunit tests under `tests/`.

Hosting model: push to `main` -> GitHub Actions builds, tests, publishes self-contained win-x86, then deploys over FTPS (lftp) to a shared IIS host using `app_offline.htm` during the copy. The deploy job runs only when the repo variable `DEPLOY_ENABLED` is `true`.

---

## 1. One-time setup

1. **.NET 11 SDK**: `dotnet --list-sdks` should show 11.0.x. In Rider: *Settings -> Build, Execution, Deployment -> Toolset and Build*.
2. **Open the solution**: *File -> Open* and pick `MedStock.slnx` (not the folder); use a new Rider window per app. The `.slnx` format needs a recent Rider.
3. **GitHub in Rider**: *Settings -> Version Control -> GitHub* -> **+** -> log in (token scopes `repo`, `workflow`). Check *Git -> Pull/Push* works without prompts.
4. **Git identity**: `git config --global user.name "Miguel Alonso"` and `git config --global user.email "miguel@alonsomiguel.com"` (use the GitHub no-reply address if your email is private).
5. **See Actions runs**: repo -> *Actions* tab, or the GitHub CLI: `gh auth status`, `gh run list -R MiguelAlonso1/medstock-inventory -L 5`, `gh run watch -R MiguelAlonso1/medstock-inventory`.
6. **Antivirus**: if a debug build fails with "file in use / access denied" or an exe vanishes from `bin`, restore it from the antivirus quarantine and add exclusions for the project's `bin` and `obj` folders (Rider also offers a Windows Defender exclusion notification).

---

## 2. Daily workflow

1. Open `MedStock.slnx`, **Git -> Pull** (Ctrl+T), check the branch and the last Actions run (do not start on a red `main`).
2. **Run/debug**: choose the `http` launch profile of the web project (`Properties/launchSettings.json`); Run Shift+F10, Debug Shift+F9; the app listens on http://localhost:5120 in `Development`. Edit env vars or URL via *Edit Configurations...*. Static asset changes (e.g. `wwwroot`) need a restart, not hot reload.
3. **Hot reload**: *Settings -> Build, Execution, Deployment -> Hot Reload*; *Run -> Apply Changes*. Rude edits need a restart.
4. **Tests**: *Unit Tests* window (Alt+8) or `dotnet test MedStock.slnx`. Run before every push; CI runs them in Release.
5. **Zero warnings**: 0 warnings, 0 errors (`TreatWarningsAsErrors` is on, including MudBlazor analyzer warnings). *Build -> Build Solution* (Ctrl+F9). To replicate CI: `dotnet build MedStock.slnx -c Release`, and publish **outside** the repo: `dotnet publish src/MedStock.Web/MedStock.Web.csproj -c Release -r win-x86 --self-contained true -o C:/Temp/publish-check`.

### Hard rules (from CLAUDE.md)
1. MudBlazor-first for all UI; no deprecated parameters; icons only `Icons.Material.*`.
2. No external UI libraries, CDN scripts/styles or Google Fonts (fonts are self-hosted). Test packages are fine.
3. No custom JavaScript (no `.js`, inline `<script>`, `IJSRuntime`).
4. All data is synthetic; never add real data.
5. Test at 390 px wide and desktop, light and dark, before merging UI changes.

### Version and changelog
For each release-worthy change: bump `<Version>` in `src/MedStock.Web/MedStock.Web.csproj`, add an entry at the top of `CHANGELOG.md`, and bump the schema version if the model changed (section 6.3).

---

## 3. Commit and deploy

> **Pushing to `main` IS a production deploy** (when `DEPLOY_ENABLED=true`).

1. **Commit** (Alt+0): review diffs, never commit `bin/`, `obj/` or `App_Data/*.db*`.
2. Safer: feature branch (*Git -> New Branch*), push, open a PR (*Git -> GitHub -> Create Pull Request* or `gh pr create -R MiguelAlonso1/medstock-inventory --fill`). PRs run `build` + `test` only (no deploy). Merging is the deploy.
3. **Watch**: `gh run list -R MiguelAlonso1/medstock-inventory -L 3`, `gh run watch -R MiguelAlonso1/medstock-inventory`, `gh run view -R MiguelAlonso1/medstock-inventory --log-failed`.
4. **What the deploy job does**: validates `FTP_SERVER_DIR`, runs a write probe, uploads `app_offline.htm`, mirrors the publish output with lftp (no `--delete` normally; `App_Data` databases are never overwritten), always removes `app_offline.htm`, then smoke-tests `/health` on `SITE_URL`. **If the `SITE_URL` variable is unset the smoke test is skipped** with a notice.
5. **Redeploy without changes**: *Actions -> CI/CD -> Run workflow* (branch `main`), or `gh workflow run ci-cd.yml --ref main -R MiguelAlonso1/medstock-inventory`. Leave `clean_target` off normally.

---

## 4. Rollback

Revert and push (CI redeploys the previous behaviour): *Git tool window (Alt+9) -> Log* -> right-click the bad commit -> **Revert Commit** -> **Push**; or `git revert <sha>` then `git push`. Never force-push `main`. To stop deploys while fixing: `gh variable set DEPLOY_ENABLED --body false -R MiguelAlonso1/medstock-inventory`. Databases are not rolled back by git (they live only on the host).

---

## 5. Managing CI/CD

- Edit `.github/workflows/ci-cd.yml` in Rider (the bundled GitHub Workflow schema gives completion). Use a branch + PR; the deploy job can only be fully proven on `main`. Spaces only, 2 per level. Do not enable `--delete` or `--ignore-time` in the normal mirror step.
- Settings: repo -> *Settings -> Secrets and variables -> Actions*, or `gh secret set NAME -R MiguelAlonso1/medstock-inventory` (prompts for the value), `gh secret list`, `gh variable set NAME --body VALUE`, `gh variable list`. Secrets cannot be read back, only replaced.

| Name | Type | Meaning |
|---|---|---|
| `FTP_SERVER`, `FTP_USERNAME`, `FTP_PASSWORD` | secrets | FTPS login to the host |
| `FTP_SERVER_DIR` | variable | site folder, exactly `inventory/` (never `/`, `./`, empty or with `..`) |
| `DEPLOY_ENABLED` | variable | must be exactly `true` for the deploy job to run |
| `SITE_URL` | variable | URL for the smoke test (optional; skipped when unset) |
| `GEMINI_API_KEY` | secret, optional | Gemini key (free tier); injected as `Ai__Gemini__ApiKey` |
| `AI_PROVIDER` | variable, optional | `Gemini` (default) or `Anthropic`; injected as `Ai__Provider` |
| `ANTHROPIC_API_KEY` | secret, optional | Claude key (paid, optional); injected as `Claude__ApiKey` |

The AI values are written into the published `web.config` **only in the deploy job**, only when non-empty. Re-run the workflow after changing a secret. The deploy job uses the `production` environment (secrets can be repo- or environment-level; be consistent).

**`clean_target`** (workflow_dispatch input): mirrors with `--delete` to remove stale server files, keeping `App_Data`, `.well-known` and logs. Allowed only when `FTP_SERVER_DIR` is exactly `inventory/`. Use it only for a first deploy over a retired app.

---

## 6. Databases with DataGrip

### 6.1 Local SQLite
1. DataGrip: *File -> New -> Data Source -> SQLite*, **File**: `src/MedStock.Web/App_Data/medstock.db` inside your clone. Download missing drivers, **Test Connection**.
2. Stop the app in Rider before writing from DataGrip, or open read-only (tick **Read-only**). `-wal`/`-shm` files may appear next to the DB; do not delete them while the app runs. Keep `App_Data/*.db*` untracked.
3. Useful queries: `SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;`, `PRAGMA user_version;`, `SELECT Email FROM AspNetUsers;`, `PRAGMA integrity_check;`.

### 6.2 Reset demo data
Delete the DB file (app stopped) and restart (it is recreated and reseeded, no EF migrations), or sign in as Admin and use **Admin -> Reset demo data**.

### 6.3 Automatic recreate on schema change
The schema version is stored in `PRAGMA user_version`: bump `DemoSeeder.SchemaVersion` in `src/MedStock.Web/Data/DemoSeeder.cs` on every model change. On the first start after a release with a different version the app drops, recreates and reseeds the demo DB (live demo data is wiped on those releases). Forgetting to bump it can break an existing DB at runtime.

### 6.4 Server-side SQLite
The live DB is a file in `App_Data/` on the host, not reachable from DataGrip. To inspect it, download a copy (plus `-wal`/`-shm`) over FTPS and open the **copy** read-only; delete it afterwards. `App_Data/*.db*` is excluded from deploys, so live data is not overwritten.

---

## 7. Using Claude Code alongside Rider

Run `claude` in Rider's Terminal (Alt+F12) at the repo root; it reads `CLAUDE.md` automatically (keep it current). Let it work on a feature branch and review the diff in the Commit window before committing. Do not run two agents on `main` at once. Always run build + tests yourself before pushing. Never give Claude secret values; set secrets yourself with `gh secret set`.

---

## 8. AI features (Ask MedStock, v1.3.1)

Default provider is **Google Gemini free tier** (`gemini-3.5-flash-lite`); Claude is optional and paid (`claude-haiku-5-5`). Both use plain `HttpClient` (no SDK). Without a key for the selected provider the feature shows a disabled state and nothing else is affected.

In the app: nav entry **Ask MedStock** (`/assistant`, any signed-in role), a read-only tool-use chat over the inventory query services, plus an **AI briefing** card on the Overview. Code: `src/MedStock.Web/Services/Assistant/`.

| Config key (env var) | Default | Meaning |
|---|---|---|
| `Ai:Provider` (`Ai__Provider`) | `Gemini` | `Gemini` or `Anthropic` |
| `Ai:Gemini:ApiKey` (`Ai__Gemini__ApiKey`) | none | Gemini key |
| `Ai:Gemini:Model` | `gemini-3.5-flash-lite` | model id |
| `Claude:ApiKey` (`Claude__ApiKey`) | none | Claude key |
| `Claude:Model` | `claude-haiku-5-5` | model id |
| `Claude:DailyLimit` / `Claude:HourlyLimit` | 200 / 20 | global per day / per user per hour |

### Production
Set the `GEMINI_API_KEY` secret (and optionally the `AI_PROVIDER` variable or `ANTHROPIC_API_KEY`) as in section 5, then redeploy.

### Run it locally
The project has a `UserSecretsId`, so **user secrets work** and load automatically in Development (recommended; nothing to commit):
```powershell
dotnet user-secrets set "Ai:Gemini:ApiKey" "<your key>" --project src/MedStock.Web/MedStock.Web.csproj
# Claude: set "Ai:Provider" "Anthropic" and "Claude:ApiKey"
```
Alternative, an environment variable in a Rider run configuration:
1. Rider: run-config dropdown -> **Edit Configurations...** -> the web project's `http` profile.
2. **Environment variables**: add `Ai__Gemini__ApiKey` = your key (double underscore = `:`). Optionally `Ai__Provider` and `Claude__ApiKey`.
3. Run. Do not commit the run configuration if it contains the key (untick "Share through VCS").

Or in a terminal for one session:
```powershell
$env:Ai__Gemini__ApiKey = Read-Host "Gemini key"
dotnet run --project src/MedStock.Web
```
Free key: https://aistudio.google.com/apikey (rate-limited; only synthetic demo data is sent). A 429 shows as "busy, try again later".

---

## 9. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Deploy: `lftp could not connect/login` or FTP `530` | FTP secrets wrong or password changed. Re-set with `gh secret set`, re-run. |
| `ECONNRESET` / data socket error | Transient FTPS TLS issue. Re-run the failed job. |
| `FTP_SERVER_DIR is unset or unsafe` | Variable empty or `/`, `./`. Set it to `inventory/`. |
| `clean_target is only allowed when ...` | Wrong folder; untick it. |
| 503 "Updating, back in a minute." after the run | `app_offline.htm` left on the server. Re-run the failed job (cleanup retries) or delete the file over FTPS. |
| HTTP 500.30 / 502.5 after deploy | App failed to start: enable `stdoutLogEnabled="true"` temporarily in the server `web.config`, read the log, turn it off (the next deploy overwrites it). Usual causes: bad setting, DB schema mismatch. |
| Smoke test fails but site is up | Host warm-up, or `SITE_URL` points at a hostname not live yet. Re-run. |
| CI fails with build warnings | Zero-warnings rule. `dotnet build -c Release` locally and fix each warning. |
| CI test fails, passes locally | CI is Linux (case-sensitive paths); check culture/time-zone assumptions. |
| Port in use locally | Stop the other run, or `Get-NetTCPConnection -LocalPort 5120` then `Stop-Process -Id <owning pid>`. |
| Rider does not see the solution | Open the `.slnx` file explicitly; update Rider. |
| Push rejected (email privacy) | Use the GitHub no-reply email or relax the setting. |

More: `README.md`, `CLAUDE.md`, `CHANGELOG.md`.

## 10. Cheat sheet

```powershell
dotnet run --project src/MedStock.Web        # http://localhost:5120
dotnet build MedStock.slnx
dotnet test  MedStock.slnx
gh run list  -R MiguelAlonso1/medstock-inventory -L 5
gh workflow run ci-cd.yml --ref main -R MiguelAlonso1/medstock-inventory
gh variable set DEPLOY_ENABLED --body false -R MiguelAlonso1/medstock-inventory
```

Release checklist: pull -> change -> build 0 warnings -> tests green -> bump `<Version>` + CHANGELOG -> commit -> push (or PR) -> watch run -> open the live URL and `/health`.
