# Static-X Web

Independent React + TypeScript + Vite application, included in `Static-X.sln`
as a JavaScript/TypeScript project. The backend is unchanged. This first delivery contains sign-in, session restoration/sign-out,
role-aware navigation and a **read-only, live staff student directory**. There
are no dashboard metrics, demo records, CRUD actions or student/payment tools.
Student and global SuperAdmin accounts receive an account-only landing page.

## Local startup

Use Node **22.15+** (implementation environment: 22.15.0) and npm 11. Run all npm
commands from `src/Static_X.Web`. Install the locked dependencies with `npm ci`.
The generated `package-lock.json` is included for reproducible installations.

The browser must use **HTTPS** so the API's Secure, HttpOnly, SameSite=Strict
cookies work. Use your own trusted localhost certificate. One manual option is
to export the existing .NET development certificate to an ignored `.certs`
directory with `dotnet dev-certs https --export-path .certs/localhost.pfx
--password <local-password>`. Create that directory first. This exports a
certificate; it does not start the API or apply a migration. Certificate trust
is a separate local decision (`dotnet dev-certs https --trust` modifies OS trust;
the app never runs it). Do not commit certificates or private keys.

Manually create `.env.local` (the environment denied automated `.env.*` writes,
including `.env.example`) with these server-only settings:

```dotenv
API_PROXY_TARGET=https://localhost:7226
HTTPS_PFX_PATH=.certs/localhost.pfx
HTTPS_PFX_PASSWORD=your-local-certificate-password
ALLOW_UNTRUSTED_LOCAL_API=false
```

Then run `npm run dev` and open **https://localhost:5173**. The development
server binds to localhost only. Start the backend separately only when you are
authorized to do so. The app never starts it. HTTP port 5211 redirects to HTTPS;
the proxy targets HTTPS 7226 directly.

The browser must trust/accept the Vite certificate. Independently, Node must
trust the API's certificate. `npm run dev` launches Node with `--use-system-ca`
(available since Node 22.15.0), honoring already-trusted Windows .NET development
certificates through the Current User and Local Machine certificate stores in
addition to Node's bundled roots. This creates no new trust and keeps TLS
certificate validation enabled. For a manually untrusted certificate or custom
CA, preferably supply an appropriate public CA/certificate PEM through
`NODE_EXTRA_CA_CERTS` **before launching npm** (or Visual Studio). Do not point
this variable at a private key. This launch flag is development-only; it does
not change the production bundle or hosting configuration.
If that is unavailable, `ALLOW_UNTRUSTED_LOCAL_API=true` explicitly disables
backend certificate verification **only in this loopback-only development
proxy**. It is false by default; never copy that exception into production.
No setting uses `VITE_*`, so proxy targets/certificate passwords are not exposed
to the client bundle. Vite preserves Host/Origin across the proxy. The API must
permit the local hostname in its host configuration; do not weaken CORS or CSRF.

After updating the launch script, **restart any already-running Vite process**:
press Ctrl+C in its terminal, then run `npm run dev` again; in Visual Studio,
stop debugging and restart with F5. Hot reload cannot apply a Node startup flag.
Port overrides remain supported, for example `npm run dev -- --port 5174`.

The diagnosed local failure returned **500** for anonymous
`GET /api/account/me` through Vite while the direct HTTPS API returned **401**.
Node's default HTTPS request failed with `DEPTH_ZERO_SELF_SIGNED_CERT`; the same
request with `--use-system-ca` returned **401**. That 401 is the expected anonymous
response, not proof of a successful browser session or real-account sign-in.

## Visual Studio

Use Visual Studio 2022 **17.11 or later** with the **ASP.NET and web development**
workload and JavaScript/TypeScript project support, plus Node 22.15+ and npm 11
available on PATH. The project pins `Microsoft.VisualStudio.JavaScript.Sdk`
to stable version `1.0.7204101`; restoring that SDK requires NuGet access or an
existing local cache. Installed IDE compatibility still needs a local smoke test.

1. Run `npm ci` in `src/Static_X.Web` if locked dependencies are not installed.
   Automatic npm installation is disabled; IDE restore/build does not install or
   update npm dependencies or the lockfile.
2. Complete the existing HTTPS certificate and `.env.local` setup above. Supply
   any `NODE_EXTRA_CA_CERTS` environment setting before launching Visual Studio.
   This integration does not create certificates or change trust settings.
3. Close and reopen `Static-X.sln` (or reload the solution if it is already open).
   `Static_X.Web` should appear as a real JSPS `.esproj`, **not a .NET class
   library**. No backend project reference is required.
4. If shared profiles are unavailable, enable **Enable Multi-Project Launch
   Profiles** under **Tools > Options > Preview Features** (in Visual Studio 18,
   **All Settings > Preview Features**), then reload the solution.
5. Select **Static-X - API + Web** in the Start dropdown. In **Configure Startup
   Projects**, select **https** as the API entry's debug target once: the Vite
   proxy expects **https://localhost:7226**, not the API's HTTP-only profile.
6. Press **F5** to start both applications. The web project runs `npm run dev`;
   `.vscode/launch.json` opens Microsoft Edge at **https://localhost:5173**.
   Edge must already be installed. The shared `Static-X.slnLaunch` file lives
   beside the solution; no project dependency is added.

Before launching, stop any existing instance in its terminal (Ctrl+C) or Visual
Studio (Stop Debugging) to avoid duplicate processes and occupied ports. The API
is listed first, but launch order does **not** guarantee HTTP readiness. If the
page reports that the API is still starting, use its retry action once ready.

### Run independently

- **Frontend in VS Code:** open `src/Static_X.Web`, then run `npm run dev` in
  its terminal and open **https://localhost:5173**. No `.esproj` or Visual Studio
  launch profile is needed. Vite never starts the API.
- **Backend in Visual Studio:** choose **Static-X - API only**, keep the API's
  **https** debug target selected, and press F5. Start the frontend separately
  in VS Code when needed.
- **Frontend only in Visual Studio:** choose **Static-X - Web only** and press
  F5. API-backed features require a separately running API.

These profiles change only which projects Visual Studio starts. Authentication,
TLS validation, certificates, database configuration and email settings remain
unchanged. Starting the API also starts its persisted-outbox hosted workers,
which can send pending email. Actual Visual Studio F5 behavior has not been
manually verified.

The web project is excluded from both default solution build configurations
(no `Build.0` entries), keeping the .NET CLI build independent of Visual Studio's
JavaScript build tooling. Development builds also skip the production npm build
(`ShouldRunBuildScript=false`). Run `npm run build` explicitly for TypeScript
checks and the production bundle; `BuildCommand` retains that same command for
JSPS production operations. No combined publish pipeline is configured.
`dotnet run` cannot run this JavaScript project; use Visual Studio or `npm run dev`.
The JSPS test command is explicitly non-watch (`npm run test -- --run`); frontend
tests remain a separate npm check, not evidence from the .NET test suite.

## Commands

```text
npm run build       # TypeScript checks + production bundle
npm run lint        # ESLint, no warnings allowed
npm test -- --run   # Vitest + Testing Library, mocked fetch only
```

On Windows PowerShell 5.1, if npm consumes `--run` instead of forwarding it,
use `npm.cmd --% test -- --run`.

`npm run preview` is only a static bundle inspection tool, not a production
server or an authenticated integration environment. It does not proxy `/api`.

## Integration and security boundaries

- All browser requests are relative `/api`; there is no backend URL in the UI.
- Login explicitly selects **Gym account** (positive gym ID required) or
  **Global administrator** (`gymId: null`). No public gym discovery or tenant
  fallback is invented. The backend remains the authorization authority.
- `/account/me` establishes identity before private routes load. A 401 is
  anonymous; 403/network/invalid identity responses produce a retryable session
  gate, not a false signed-out state. Roles are validated as numeric 1–4;
  only GymAdmin/Trainer (1/2) can mount/query the staff directory.
- Request tokens come from `/account/csrf` and are kept only in memory. Every
  unsafe operation fetches a fresh token. Login refreshes again after the cookie
  identity changes, before showing private routes. No unsafe automatic retries.
- Passwords are not placed in state, storage or logs; the form clears on submit.
  There are no JWTs, localStorage sessions, analytics or third-party requests.
- Identity transitions are serialized. Private requests consume cancellation
  signals and an identity generation guard. Transitions cancel queries and clear
  caches; private query keys include account ID and gym. A private 401 hides the
  private UI immediately. Filter changes never use previous-filter placeholder
  data. Search is explicitly submitted, trimmed and limited to 320 characters.
- Logout requires a confirmed 204. Failed/unknown logout retains the local
  identity with an explicit warning, never claims the server session ended.
- Server errors are status-mapped to safe copy, never rendered as raw details.

## Production hosting

Deploy only `dist/` behind your production **HTTPS** host. Route `/api/*` to the
ASP.NET Core API at the **same browser origin**; preserve cookies and applicable
forwarding headers with correctly trusted proxies. Do not serve API failures as
the SPA document. All non-file frontend paths (such as `/students` and `/account`)
need a history fallback to `index.html`. Cache hashed assets immutably, but keep
`index.html` revalidated and `/api` responses private/no-store. No Vite dev/preview
server, certificate bypass or new CORS configuration belongs in production.

## Structure and visual system

- `src/auth/`: session lifecycle, identity validation, in-memory CSRF and provider.
- `src/lib/`: same-origin HTTP client and utility composition.
- `src/components/ui/`: owned, shadcn-style Button/Input primitives using Radix
  Slot, CVA and Tailwind merge. `components.json` and `@/` aliases support future
  shadcn additions; no remote generator was run.
- `src/pages/`: sign-in/account and lazy-loaded staff directory.
- `src/features/students/`: HTTP query encoding and response validation.
- `src/test/`: UI/HTTP composition checks using Vitest and Testing Library.

The visual direction is restrained sporting/editorial: charcoal surfaces,
violet accents, a condensed system display face and responsive navigation.
No external fonts/images/CDNs, decorative charts or placeholder controls.
Keyboard focus, labels, live status messages and reduced-motion support are
included. Tables scroll horizontally on narrow screens.

**Provisional logo:** `public/brand/static-x-mark.svg` is an approximate vector
interpretation of the supplied chat reference, **not the original brand asset**.
Replace it with the authorized original when available (preserve its accessible
usage and adjust the viewBox/dimensions as needed). The visible wordmark is
separate text and the application name is exactly **Static-X**.

## Verification status

The existing HTTP controllers/DTOs were inspected read-only. Backend/API startup,
database changes, migrations, email and real account creation were not performed.
Dependencies were installed and the lockfile generated. TypeScript/production
build, ESLint and all 17 mocked HTTP/UI test cases passed in the implementation
environment. A dependency audit identified advisories in the initial pins;
React Router, Vite, Vitest, TypeScript ESLint and the compatible transitive
esbuild version were updated. The subsequent `npm audit --audit-level=low`
reported zero known vulnerabilities. An independent read-only check found no
blocking defect in the session, CSRF, role isolation or development TLS paths.
Test files were written before implementation, but the first runner execution
was blocked by missing offline dependencies: **no observed pre-implementation
RED result is claimed**. Tests use mocked fetch, not the live API.

Real browser TLS, API proxying, cookie/antiforgery round trips, responsive visual
rendering and live student data remain **unverified**. After installation, run
all three commands above and perform an authorized browser smoke test with an
existing account. Do not treat mocked HTTP tests as live-backend evidence.
