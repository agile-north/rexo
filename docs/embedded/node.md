# Embedded Policy: node

`embedded:node` is a Node.js toolchain overlay. It supplies the `restore`, `build`, `test`, `analyze`,
`format` and `security` commands that `embedded:standard` lifecycle commands call into.

Only essential behavior is on by default; everything else is an opt-in switch in `vars.node.*`.

| Feature | Default | Switch |
| --- | --- | --- |
| Lockfile install (`npm ci` / `--frozen-lockfile`) | on | `vars.node.packageManager`, `vars.node.restore.command` |
| `build` / `test` scripts | on | `vars.node.build.script`, `vars.node.test.script` |
| Lint script during `analyze` | **on** | `vars.node.lint.enabled` |
| Format check during `analyze` | **off** | `vars.node.format.check.enabled` |
| SARIF lint script during `analyze` | **off** | `vars.node.sarif.enabled` |
| Dependency audit in `security` | **off** | `vars.node.audit.enabled` |
| Run inside a container | **off** (host) | `vars.node.container` |

Every enabled script must be defined in `package.json`; missing scripts fail the command.

## Usage

```yaml
extends:
  - embedded:standard
  - embedded:node
vars:
  node:
    packageManager: pnpm
    audit: { enabled: true, level: critical }
```

Policy vars are deep-merged underneath your repository's `vars`; your repository always wins.

## Commands

| Command | Steps (ids) |
| --- | --- |
| `restore` | `node-install-custom`, `node-install` (npm), `node-install-pnpm`, `node-install-yarn`, `node-install-bun` — exactly one runs |
| `build` | `node-build` |
| `test` | `node-test` |
| `analyze` | `node-lint`, `node-format-check`, `node-lint-sarif` |
| `format` | `node-format` |
| `security` | `node-audit` (writes JSON to `outputs.security.audit`) |

## vars.node reference

| Var | Default | Description |
| --- | --- | --- |
| `packageManager` | `npm` | `npm`, `pnpm`, `yarn` or `bun`. |
| `yarnVersion` | `classic` | Yarn audit implementation: `classic` (`yarn audit`) or `modern` (`yarn npm audit`). |
| `container` | `""` | Container registry name (e.g. `node`) for all node steps; empty/`none` = host. |
| `restore.command` | `""` | Custom install command replacing the package-manager default. |
| `build.script` | `build` | Script run by `build`. |
| `test.script` | `test` | Script run by `test`. |
| `lint.enabled` | `true` | Run the lint script in `analyze`. |
| `lint.script` | `lint` | Lint script name. |
| `format.script` | `format` | Script run by the `format` command. |
| `format.check.enabled` | `false` | Run the format check script in `analyze`. |
| `format.check.script` | `format:check` | Format check script name. |
| `sarif.enabled` | `false` | Run the SARIF lint script in `analyze`. |
| `sarif.script` | `lint:sarif` | SARIF lint script name (should write to `outputs.analysis.sarif`). |
| `audit.enabled` | `false` | Run `<packageManager> audit` in `security`. |
| `audit.level` | `high` | Minimum severity that fails the audit. Audit commands are selected for npm, pnpm, Bun, and Yarn Classic/Berry. |

## Containers

The policy ships a `node` container (`docker.io/library/node:lts`, working directory `/work`).
Set `vars.node.container: node` to run every node step inside it, and override `containers.node`
to pin a different image. See [Containerized run steps](../configuration/containerized-run.md).
