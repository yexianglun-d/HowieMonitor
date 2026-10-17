# Code signing policy

Status, 2026-10-17: The SignPath Foundation application has been submitted and is awaiting review. The project has not been approved, and there is no signed release yet. Merely having this policy, a CI workflow or an application does not establish a trusted signature.

## Requested provider and scope

The intended attribution after approval is: "Free code signing provided by SignPath.io, certificate by SignPath Foundation". This is a description of the requested service, not a claim that the Foundation currently endorses this project. [Foundation conditions](https://signpath.org/terms.html).

The initial requested signing scope is the project's own `HowieMonitorSetup.exe` installer. Its embedded agent archive is protected by a SHA-256 check and the enclosing installer signature; the extracted agent is not claimed to have a separate Authenticode signature. Third-party runtime/library binaries are not signed under this project's signing policy.

## Source, build and approval

Only artifacts built from this public repository on GitHub-hosted Windows runners may be submitted. The workflow records the source revision, runs project tests and package checks, uploads the exact unsigned installer to GitHub Actions, and passes that artifact identity to SignPath for origin verification. Product name is `Howie Monitor`; product version is `0.1.0` for this version.

Untrusted contributions require maintainer review. Signing is a manually requested workflow operation and also requires a SignPath approver to review the specific request. The signing workflow must run from the default branch; it does not sign arbitrary uploaded files. All maintainers with source or signing access must enable multi-factor authentication. Signing secrets belong in GitHub Actions secrets, never in source or release archives.

Author and reviewer: [yexianglun-d](https://github.com/yexianglun-d), the repository owner. Proposed signing approver: the same maintainer; the corresponding SignPath account and approver role must be assigned after approval. No additional signing approver is currently authorized. GitHub MFA was confirmed enabled by the maintainer on 2026-10-17. The corresponding SignPath account and MFA have not yet been configured; both must be ready before signing.

## Release verification

After signing, `scripts/verify-signed-setup.ps1` must check Windows signature validity, the expected SignPath Foundation publisher and timestamp, unchanged product metadata and embedded agent payload, and the installer's read-only `--check-package` result. SHA-256 is calculated from the returned signed executable. An unsigned artifact must remain labeled unsigned. No signed release is published automatically by this workflow.

Code signing identifies a publisher and detects changes to the signed file. It does not promise that Windows SmartScreen or security products will never display a warning.

## Privacy

See [PRIVACY.md](PRIVACY.md) for the information collected, intended server, optional notification service, retention, and stopping/uninstalling the agent. Installation requires the computer user's confirmation and standard administrator consent. Do not upload private telemetry or credentials to signing requests.
