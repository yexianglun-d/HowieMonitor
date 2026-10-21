# Howie Monitor

A personal Windows 11 performance monitor with an HTTPS server and a responsive web dashboard. It reports real CPU, GPU, memory, disk, network and process measurements, keeps up to seven days of history, and supports optional ServerChan Turbo notifications.

The current source supports monitoring multiple computers under one private dashboard, with separate device credentials, live state and history. Adding a computer keeps existing computers enrolled. Notification rules, trigger/recovery state and sending records are separate per computer. The SendKey, delivery switch and daily request budget are shared. On upgrade, existing shared rules are copied once to currently enrolled computers; newly added computers start without rules. The computer overview shows separate cards and opens the selected computer directly. Previously published installers can use the additive pairing flow after the server is upgraded.

The owner can remove a computer from the computer overview or history page after confirming its identity and re-entering the dashboard password. Removal deletes that computer's online monitoring data and notification configuration, revokes its credential and ends terminal sessions. Other computers and the shared delivery channel/budget are preserved. Backups, logs, local agent data and notifications already sent are not erased; re-enrollment requires a new pairing code.

The Windows installer guides an authorized user through environment checks, a short-lived pairing code, explicit installation confirmation, Windows administrator consent, and verification of real telemetry. Temperature and power readings depend on hardware and permissions; unavailable readings stay marked as unavailable. The installer does not install sensor drivers.

The setup source also supports interactive command-line installation with `--cli`, using the same package, pairing protocol and Windows service. After the updated server and a CLI-capable installer are published, the enrollment page provides one PowerShell command that downloads, checks the package and starts the CLI. Run it from an administrator PowerShell, follow the prompts, and explicitly choose whether to authorize the current account's administrator terminal. Installation is reported successful only after live enrollment verification. The public bootstrap/download endpoints expose software only; panel data and pairing authorization remain protected. See the [CLI instructions](BUILDING.md#interactive-cli-installation); older published installers require an update before using this option.

GPU usage is shown separately for each reported graphics adapter, with its model name and independent history curve. An idle integrated GPU does not hide a discrete GPU's load; missing readings remain unavailable.

The source adds per-computer agent version/build identification and controlled upgrades. A computer must first install a compatible package locally and separately opt in; remote upgrades remain off by default. The dashboard pins a higher release version and package hash, rechecks its password, shows progress, and confirms new live telemetry before success. Installation or telemetry failure attempts to restore the previous version. This implementation has been built but has not yet passed upgrade/rollback functional or Windows acceptance tests, and is not deployed. See [upgrade behavior and release manifests](docs/agent-updates.md).

The source also includes an optional administrator PowerShell terminal, disabled by default. The updated installer must explicitly authorize its installing Windows account; that account must remain logged in and have an elevated administrator token. The panel rechecks its own password before connecting, and sessions are isolated by computer and browser login. Terminal input/output is relayed through the operator's server without persistent transcripts; this is not end-to-end encryption. Switching computers or leaving the page ends the session. No Windows password is saved, and commands never fall back to SYSTEM. See [privacy details](PRIVACY.md) and the [Windows acceptance procedure](docs/terminal-acceptance.md). This source implementation has passed protocol tests; actual Windows service/elevation acceptance is still required before release.

This is an early personal project. Real service installation, repair, uninstall, reboot recovery, notification delivery and long-running stability are still undergoing acceptance testing. Do not treat a successful build as proof of those scenarios.

## Downloads and setup

Use [GitHub Releases](https://github.com/yexianglun-d/HowieMonitor/releases) for public builds. The Windows installer is `HowieMonitorSetup.exe`; each release must state whether its signature is valid or the build is unsigned and include its SHA-256 checksum. A signing application or workflow configuration is not a signed release.

The official installer currently connects to **https://monitor.howied.me/**. This is a private dashboard, not a public hosted service: only its operator can issue pairing codes. Obtain permission from that operator before using it. Review the [privacy notice](PRIVACY.md) and the server address before pairing a computer. To run your own server and build an installer for it, see [building and deployment](BUILDING.md).

The updated uninstaller runs outside the installation directory so it can remove the installed executable immediately. Complete removal is selected by default and clears local identity, telemetry queues, update snapshots and the current account's installer state; uncheck it to preserve configuration and queued samples. The temporary uninstaller removes itself after its window closes. A newer installer can resume an interrupted uninstall after verifying product ownership, service removal and unlocked files, and cancels only this product's obsolete reboot-delete entries. Files still held by another process require that process to close; do not manually delete the pending marker. Server history is retained.

Do not put dashboard passwords, SendKeys, pairing codes or device tokens in an issue. Source releases contain examples only, not live credentials or telemetry.

## Code signing policy

See the [code signing policy](CODE_SIGNING.md). The SignPath Foundation application was submitted on 17 October 2026 and is awaiting review. Current releases remain unsigned until a release explicitly states otherwise and its signature has been verified.

## Build and license

Source and build instructions: [BUILDING.md](BUILDING.md).

Project code uses the [MIT License](LICENSE). Bundled third-party components retain their own licenses. Published packages include third-party notices, license texts and upstream source links; LibreHardwareMonitorLib is distributed under MPL-2.0 without modifying its source.
