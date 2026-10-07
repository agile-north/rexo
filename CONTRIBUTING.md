# Contributing

## Development Workflow

1. Create a topic branch from `main`.
2. Make focused changes with tests.
3. Run restore, build, and test locally.
4. Open a pull request with clear context.

## Local Commands

```bash
dotnet restore
dotnet build solution.slnx -c Release
dotnet test solution.slnx -c Release
```

## Standards

- Use central package version management only.
- Keep project files minimal and avoid repeated metadata.
- Keep implementation under `src/` and tests under `tests/`.
- Prefer small, composable interfaces and deterministic behavior.

## Commit Message Format

Use Conventional Commits for commit messages:

```text
type(scope): summary
```

Allowed `type` values:

- `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`, `revert`

Examples:

- `feat(cli): add direct configured command execution`
- `fix(configuration): reject unsupported schemaVersion values`
- `test(execution): cover builtin registry dispatch failures`

Rules:

- Use lowercase type and scope.
- Use kebab-case scope when present.
- Do not end the summary with a period.
- Keep the header to 100 characters or fewer.

## Pull Requests

- Include problem statement and approach.
- Link related issues.
- Update docs when behavior changes.
- Ensure CI is green.

## Pull Request Format

Use Conventional Commits format for PR titles:

```text
type(scope): summary
```

Include all of these sections in the PR body:

- `## Summary`
- `## Checklist`
- `## Validation`

Additional PR body requirements:

- At least one checklist item must be checked (`- [x]`).
- Validation should include the results of:
  - `dotnet build solution.slnx -c Release`
  - `dotnet test solution.slnx -c Release` (or `--no-build` variant)
