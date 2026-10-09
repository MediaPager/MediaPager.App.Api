# MediaPager.App.Api

The ASP.NET Core (.NET 10) **web host** for MediaPager. A thin layer over `MediaPager.App.Core`:
config, DI, auth/JWT, EF migrations + seeds, plugin loading, controllers, and the remaining
provider-coupled services until their plugin phases land.

## Architecture

- **Plugin host.** On startup, right after `Build()`, the registry is populated from two
  install directories (shared one-folder-per-plugin layout, default load context — no
  collectible ALC, Contracts types must unify with the host's):
  `~/.MediaPager/plugins/official` (shipped officials, deployed there at build via the
  Api's `DeployOfficialPlugins` target; optional officials install on demand) and
  `~/.MediaPager/plugins/community` (recognized community plugins, installed on demand).
  Metadata lives in `plugins.official.json` / `plugins.community.json`; appsettings only
  lists which ids are `Plugins:Required` (drives the setup checklist). Community plugins
  claiming an official id are rejected at load.
- **Domain** lives in `MediaPager.App.Core` (EF entities, `AuthDbContext`, settings, plugin host).
  Plugins reference only `MediaPager.App.PluginContracts`.
- **Plugin settings** are read per call via `IPluginSettingsStore` under
  `plugins.<pluginKey>.<name>`; config (`plugins:*`) is the fallback.

## API surface

The `/sources`-driven surface is the target end-state; the legacy provider routes remain
authoritative until the Phase 5 cut-over.

**Plugin / discovery (auth-gated):**

- `GET /sources` — catalog of loaded plugins, capabilities, nav sources, action icons,
  subtitle providers, and settings schemas. Drives the SPA nav + settings UI.
- `POST /plugins/{key}/actions/{actionId}` — generic dispatch to a plugin-declared action.
- `GET /plugins/activity` — plugin-raised jobs and notifications; cancel/clear/dismiss endpoints
  live under `/plugins/activity` as well.
- `GET /sources/{key}/browse` — one page of a stream source's catalog.
- `GET /sources/{key}/details/{externalId}` — a stream source's detail-sheet payload.
- `GET` / `PUT /plugins/{key}/settings` — data-driven plugin settings (admin). Writes are
  whitelisted to the plugin's declared schema; secret fields never round-trip (blank = keep).
- `GET /plugins/{key}/ui/{**path}` — a plugin's custom-UI static assets (anonymous, sandboxed
  in the SPA via iframe + `postMessage`).

**Removed:** `GET /getlink/{id}` — the host no longer hardcodes any stream lookup. Playback
resolves through the loaded stream-provider plugin.

**Auth / accounts:**

- `POST /auth/login` — exchange email + password for a bearer token and scopes.
- `POST /auth/forgot-password` · `POST /auth/reset-password` — password-reset flow, sent
  through the active email provider plugin (`IEmailProviderPlugin`).
- `POST /auth/register` — create an account from an invitation token.
- `POST /auth/invites` — send an invite (`admin:can-invite` or `admin:super`).
- `GET /stream/{streamId}/{resourceId}` — proxy an expiring HLS capability URL.

## Accounts & authorization

No public sign-up: sign in with the seeded super-admin, then invite users from **Settings**.
The API protects routes by default; login, forgot/reset-password, invite-registration, and
plugin-UI asset endpoints are public. The seeded account email defaults to
`admin@mediapager.local`; a random password is printed once on first run (only when no
`admin:super` user exists and that email is free).

## Configuration

From `appsettings.json`, environment variables, and user-secrets. Keep credentials out of
committed files. `MEDIAPAGER_`-prefixed env vars override JSON and secrets (`__` for nesting).

```sh
# Stable JWT signing key (else a temp key is generated per restart)
dotnet user-secrets set "Auth:SigningKey" "$(openssl rand -base64 48)" --project MediaPager.App.Api/MediaPager.App.Api.csproj
```

- **Email:** sent by an email provider plugin, not host config. SMTP and Mailgun ship
  loaded; pick one (and its credentials) under **Settings → Email** in the SPA — the
  values are stored as plugin settings (`plugins.smtp.*` / `plugins.mailgun.*`), never in
  this repo. Gmail and Office 365 are optional installs (see
  `Plugins:Official` in `appsettings.json` for the links).

- **Database:** `%APPDATA%/MediaPager/db/mediapager.db` (Windows) or
  `~/.MediaPager/db/mediapager.db` (macOS/Linux). Override with `MEDIAPAGER_DB_PATH` or
  `MEDIAPAGER_Auth__DatabasePath`. EF migrations apply at startup. The JWT signing key
  persists to `signing.key` beside the DB unless `MEDIAPAGER_EKEY` is configured.
- **Initial account:** `MEDIAPAGER_SEED_USER` sets the first super-admin email/login and
  `MEDIAPAGER_SEED_PASS` optionally supplies its initial password. These apply only when
  the first account is created.
- **TMDB:** `MEDIAPAGER_TMDB_API_KEY` supplies the TMDB plugin key when no value is saved
  in the plugin settings.
- **`Frontend:BaseUrl`** (default `http://localhost:5173`) — used in email links.

## Run

```sh
dotnet run --project MediaPager.App.Api/MediaPager.App.Api.csproj   # http://localhost:5074
```

The companion SPA is `MediaPager.App.Ui` (`npm run dev`, `http://localhost:5173`).
