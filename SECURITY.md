# Security Policy

## Supported Versions

Tabula is in active development. Security fixes are accepted for the
`main` branch and the latest tagged release. Older versions may be handled on a
best-effort basis.

## Reporting a Vulnerability

Please do not open a public issue with exploit details, credentials, private
API keys, proof-of-concept code, or screenshots that reveal sensitive data.

Preferred reporting channels:

1. Use GitHub private vulnerability reporting for
   [Shelios-Ceres/infinite-canvas](https://github.com/Shelios-Ceres/infinite-canvas/security/advisories/new).
2. If private reporting is not available, open a public issue that asks for a
   private contact channel and does not include technical exploit details.

Please include:

- Affected version, commit, or branch, and your Windows version.
- Clear reproduction steps.
- Impact and attack scenario.
- Any relevant logs, screenshots, or proof of concept, with secrets removed.
- Whether the issue affects locally stored canvases or media, API key storage,
  AI provider configuration, the local Agent connection, or network requests.

## Scope

Examples of in-scope reports:

- Exposure of locally stored API keys, canvases, or media caused by project
  code.
- Unsafe file handling, import, paste, or recycle behavior.
- Access-control flaws in the local Agent connection, such as another Windows
  user or a remote host reaching the canvas MCP pipes.
- Supply-chain issues that are exploitable through this repository's shipped
  code or default configuration.

Examples that are usually out of scope:

- Vulnerabilities in third-party AI providers, model APIs, or local agents
  (such as Claude Code) outside this repository.
- Compromise of a user's own API key outside the app.
- Denial-of-service reports that require unrealistic traffic volume or physical
  access to the user's device.
- Social engineering, phishing, spam, or account recovery requests.
- Dependency reports without a practical impact on this project.

## Disclosure

The maintainers aim to acknowledge valid reports within 7 days and coordinate a
fix before public disclosure. Response and fix timelines are best effort for
this community project.

Please allow time for investigation and remediation before publishing details.
Credit will be given on request unless you prefer to remain anonymous.

