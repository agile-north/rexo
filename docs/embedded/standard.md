# embedded:standard

`embedded:standard` is the baseline lifecycle policy.

It provides user-facing commands (`plan`, `validate`, `version`, `test`, `analyze`, `verify`, `build`, `tag`, `push`, `release`, `clean`) and composes toolchain-specific behavior through command overlays.

## Command Model

### plan

Description: Validate and show what would be built/pushed.

Steps:

1. `builtin:validate`
2. `builtin:resolve-version`
3. `builtin:plan-artifacts`

### validate

Description: Validate repository configuration.

Steps:

1. `builtin:validate`

### version

Description: Resolve repository version.

Steps:

1. `builtin:resolve-version`

### test

Description: Run configured tests.

Steps:

1. `command:test` (overlay contribution, when present)

### analyze

Description: Run configured analysis.

Steps:

1. `command:analyze` (overlay contribution, when present)

### verify

Description: Run validation, then overlay-provided quality checks.

Steps:

1. `builtin:validate`
2. `command:verify` (overlay contribution, when present)
3. `command:test` (when present)
4. `command:analyze` (when present)
5. `command:security` (when present)

Notes:

- User-facing `verify` always includes `builtin:validate` first.
- There is no dedicated core verify builtin; quality-gate behavior is policy-composed.

### build

Description: Build and tag configured artifacts locally.

Steps:

1. `builtin:validate`
2. `builtin:resolve-version`
3. `command:build` (overlay continuation, skipped when no inner layer contributes steps)
4. `builtin:build-artifacts`
5. `builtin:tag-artifacts`

Notes:

- Toolchain overlays such as `embedded:dotnet` and `embedded:node` inject their build steps at the continuation point.
- When no overlay contributes steps, the continuation marker is skipped successfully.

### tag

Description: Tag configured artifacts.

Steps:

1. `builtin:resolve-version`
2. `builtin:tag-artifacts`

### push

Description: Push configured artifacts when explicitly confirmed.

Options:

- `--confirm` (`bool`, default `false`)

Steps:

1. `builtin:push-artifacts` with `with.confirm = {{options.confirm}}`

### release

Description: Validate, verify, build, tag, and optionally push.

Options:

- `--push` (`bool`, default `false`)

Steps:

1. `builtin:validate`
2. `builtin:resolve-version`
3. `command:verify`
4. `builtin:build-artifacts`
5. `builtin:tag-artifacts`
6. `builtin:push-artifacts` when `{{options.push}}`, with `with.confirm = {{options.push}}`

### clean

Description: Remove generated Rexo output.

Steps:

1. `builtin:clean`

## Aliases

- `all` -> `release`
- `ship` -> `push`
