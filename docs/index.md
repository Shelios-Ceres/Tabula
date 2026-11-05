# Infinite Canvas Documentation Index

## Overview

- [Quick Start](/docs/overview/quick-start)
- [Features](/docs/overview/features)

## Development

- [Local Development](/docs/development/local-development)

## License

- [Open-source License](/docs/business/license)

## Security

- [Report a Vulnerability](/docs/support/security)

## Project Progress

- [Changelog](/docs/progress/changelog)
- [Pending Tests](/docs/progress/pending-test)
- [TODO](/docs/progress/todo)
- [WinUI 3 Desktop Client Plan](/docs/progress/desktop-client-plan)

## Notes

- Infinite Canvas is a native Windows desktop client in `desktop/`; the former web client and its companion tools have been removed.
- Canvases, media files and settings are stored locally in `%LocalAppData%\InfiniteCanvas`; there is no cloud sync.
- API keys are encrypted with Windows DPAPI, and the client sends requests directly to each vendor's native API.
