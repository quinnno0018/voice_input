# AGENTS.md

## Project Context

This is a Windows-first AI real-time voice input project.

## Required Reading Before Development

Before development work, read these files first:

- `docs/PRODUCT_SPEC.md`
- `docs/ARCHITECTURE.md`
- `docs/DECISIONS.md`
- `docs/ROADMAP.md`

## Development Rules

1. Do not create new Markdown documents unless there is a clear and necessary reason.
2. Current product priority: Latency > Controllability > Intelligence.
3. Do not prematurely develop:
   - commercial systems
   - payment
   - formal License systems
   - cross-platform support
   - mobile clients
   - full Windows IME
   - complex Agent Frameworks
4. Handle one clear technical capability at a time.
5. Core architecture changes must update `docs/DECISIONS.md`.
6. After implementation, run the relevant tests.
7. Do not write API keys, tokens, passwords, or other secrets into code or Git commits.
8. `.env` must not be committed to Git.
9. The project must remain suitable for future external Beta testing:
   - do not depend on local absolute paths
   - do not depend on machine-specific tools
   - configuration must be externalizable
   - third-party service secrets must not be exposed directly to test users
