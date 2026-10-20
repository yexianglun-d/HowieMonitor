# Howie Monitor

A personal Windows 11 performance monitor with an HTTPS server and a responsive web dashboard. It reports real CPU, GPU, memory, disk, network and process measurements, keeps up to seven days of history, and supports optional ServerChan Turbo notifications.

The current source supports monitoring multiple computers under one private dashboard, with separate device credentials, live state and history. Adding a computer keeps existing computers enrolled. Notification rules, trigger/recovery state and sending records are separate per computer. The SendKey, delivery switch and daily request budget are shared. On upgrade, existing shared rules are copied once to currently enrolled computers; newly added computers start without rules. The computer overview shows separate cards and opens the selected computer directly. Previously published installers can use the additive pairing flow after the server is upgraded.

The Windows installer guides an authorized user through environment checks, a short-lived pairing code, explicit installation confirmation, Windows administrator consent, and verification of real telemetry. Temperature and power readings depend on hardware and permissions; unavailable readings stay marked as unavailable. The installer does not install sensor drivers.

This is an early personal project. Real service installation, repair, uninstall, reboot recovery, notification delivery and long-running stability are still undergoing acceptance testing. Do not treat a successful build as proof of those scenarios.

## Downloads and setup

Use [GitHub Releases](https://github.com/yexianglun-d/HowieMonitor/releases) for public builds. The Windows installer is `HowieMonitorSetup.exe`; each release must state whether its signature is valid or the build is unsigned and include its SHA-256 checksum. A signing application or workflow configuration is not a signed release.

The official installer currently connects to **https://monitor.howied.me/**. This is a private dashboard, not a public hosted service: only its operator can issue pairing codes. Obtain permission from that operator before using it. Review the [privacy notice](PRIVACY.md) and the server address before pairing a computer. To run your own server and build an installer for it, see [building and deployment](BUILDING.md).

Do not put dashboard passwords, SendKeys, pairing codes or device tokens in an issue. Source releases contain examples only, not live credentials or telemetry.

## Code signing policy

See the [code signing policy](CODE_SIGNING.md). The SignPath Foundation application was submitted on 17 October 2026 and is awaiting review. Current releases remain unsigned until a release explicitly states otherwise and its signature has been verified.

## Build and license

Source and build instructions: [BUILDING.md](BUILDING.md).

Project code uses the [MIT License](LICENSE). Bundled third-party components retain their own licenses. Published packages include third-party notices, license texts and upstream source links; LibreHardwareMonitorLib is distributed under MPL-2.0 without modifying its source.
