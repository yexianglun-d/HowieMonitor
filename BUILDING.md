# Building Howie Monitor

Use Windows 11 x64, PowerShell 7, the .NET SDK selected by `global.json`, Node.js 24, Python 3 and Git. NuGet and npm use their official registries. Package versions are locked; do not delete lock files to make a build succeed.

```powershell
dotnet restore Howie.Monitor.slnx --locked-mode
dotnet build Howie.Monitor.slnx -c Release --no-restore
dotnet test Howie.Monitor.slnx -c Release --no-build --no-restore
python -m unittest discover -s tests -p test_backup_monitor.py
./scripts/publish-monitor.ps1 -PublicRelease
```

The publisher installs browser dependencies with `npm ci`, performs strict Vue type checking and a production build, publishes a self-contained Windows agent and Linux x64 server, generates third-party notices, and embeds the agent and notices into the Windows installer. `--check-package` checks the embedded archive and .NET runtime without installing software or contacting a monitoring server. The installer output includes a SHA-256 sidecar.

`-PublicRelease` includes only these public instructions, the privacy/signing policies and the project license instead of private deployment acceptance records. An unsigned local build stays unsigned. Read [CODE_SIGNING.md](CODE_SIGNING.md) before requesting a signature.

## Local development

`scripts/start-local.ps1` creates a private instance below `.local/`; follow its interactive setup prompts. `scripts/stop-local.ps1` stops only processes belonging to the selected instance. Never commit that directory, runtime configuration, queues or databases.

## Own server

The server can run on Linux x64 with its self-contained publish output and a modern supported operating system. Set `--server-url` to a loopback listener, `--public-url` to your HTTPS origin, and `--data-dir` to an access-restricted writable directory. Use `--initialize-admin true` locally to initialize a new administrator password through the hidden-input prompt; do not put the password on the command line. Do not overwrite an existing instance's account or keys.

Templates in `deploy/` show reverse proxy and service configuration. The remote terminal requires the specific WebSocket Upgrade locations in the Nginx template; ordinary HTTP proxying alone is insufficient. Upgrade the server and installer together, then complete [Windows terminal acceptance](docs/terminal-acceptance.md) before enabling the feature on a real computer. Older agents remain usable for monitoring and report no terminal channel. Replace template fields with your own values and validate the resulting configuration. The agent requires HTTPS, except for explicitly enabled loopback-only development. Its example configuration contains placeholders rather than a usable token. Generate a pairing code through your authenticated panel.

The official setup program targets `https://monitor.howied.me/`. To build an installer for your own deployment, change `SetupPaths.ServerUrl` in `src/Howie.Monitor.Setup/SetupState.cs` to your HTTPS origin before building. Such a fork is a distinct distribution: document its server and privacy behavior and arrange its own signing; do not claim the official project's signature or signing approval.

The backup helper is specifically scoped to the `howie-monitor` Docker deployment and its fixed paths and account IDs. Read and adapt its preconditions to your deployment before installation. It temporarily stops that container, keeps complete private snapshots, verifies isolated restore copies and retains the seven most recent verified backups. The cron template is not installed by building the project.

## SignPath integration after approval

Create the SignPath project, restrict it to this repository's GitHub-hosted workflow and approved default branch, and import `deploy/signpath/setup.xml`. Assign approvers and enable MFA. Set the repository secret `SIGNPATH_API_TOKEN`, and repository variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`. These values must come from your approved SignPath organization; no sample token is supplied.

Run the Windows release workflow with `request_signing` false to create an explicitly unsigned artifact. After approval and configuration, run it on the default branch with `request_signing` true and manually approve the individual SignPath request. A signed artifact is emitted only after signature, metadata and payload checks succeed. Publication to Releases and replacement of deployed downloads require selecting that verified artifact and its newly calculated hash; this workflow does not modify a running server.

## Portable terminal protocol tests

`dotnet restore tests/Howie.Monitor.Terminal.Tests --locked-mode`, followed by `dotnet test tests/Howie.Monitor.Terminal.Tests --no-restore`, runs on Windows, Linux or macOS with the selected SDK and ASP.NET Core runtime. It starts an isolated loopback Kestrel server with temporary credentials, tests the real Cookie/CSRF/password and WebSocket paths, and deletes only its own temporary data. The agent in this test is a protocol simulator; it does not validate Windows token acquisition, ConPTY or service process cleanup. Frontend race and isolation checks run with `npm test` in `src/Howie.Monitor.Web`.
