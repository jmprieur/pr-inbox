# Security Policy

## Reporting a vulnerability

Please report security vulnerabilities **privately** — do not open a public
issue for anything security-sensitive.

- **Preferred:** open a private report through GitHub's
  [security advisory form](https://github.com/jmprieur/pr-inbox/security/advisories/new).
- **Or email:** jmprieur@users.noreply.github.com

Please include enough detail to reproduce: the affected version or commit,
steps, and the impact you observed. You can expect an initial acknowledgement
within a few days. This is a personal open-source project, so fixes are made
on a best-effort basis.

## Supported versions

Security fixes target the latest `main`. There is no long-term support branch.

## Dependencies

Dependency vulnerabilities are tracked with GitHub Dependabot and addressed on
`main`. For example, the bundled native SQLite is pinned to a patched build via
`SQLitePCLRaw.lib.e_sqlite3` in `Directory.Packages.props`.
