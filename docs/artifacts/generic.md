# Generic Artifact Provider (`type: "generic"`)

The generic provider packages a directory as an archive and then copies that archive to a destination directory.

## Command mapping

- Build: create archive from source directory
- Push: copy matching built archives to destination directory

## Settings

| Key | Type | Notes |
| --- | --- | --- |
| `format` | `string` | Archive format. Currently only `zip` is supported (default `zip`). |
| `target.source` | `string` | Source directory to archive (default repo root). |
| `target.sourceEnv` | `string` | Env var name containing source directory (default env key `GENERIC_TARGET_SOURCE`). |
| `target.output` | `string` | Output directory for built archives (default `artifacts/generic`). |
| `target.outputEnv` | `string` | Env var name containing output directory (default env key `GENERIC_TARGET_OUTPUT`). |
| `target.destination` | `string` | Destination directory for push copy step. |
| `target.destinationEnv` | `string` | Env var name containing destination directory (default env key `GENERIC_TARGET_DESTINATION`). |

## Runtime behavior

- Build output name is `<artifact-name>-<resolved-version>.<format>`
- Resolved version uses `context.Version.SemVer`; default is `0.0.0` when version has not been resolved yet
- Push copies all files matching `<artifact-name>-*` from resolved output directory to resolved destination directory
- Push fails when no destination is configured

Path resolution:

- Relative paths are resolved against the repository root
- Absolute paths are used as-is

Value precedence:

1. Env value from `settings.target.*Env` (or provider default env key)
2. `settings.target.*`

## Example

```json
{
  "type": "generic",
  "name": "bundle",
  "settings": {
    "format": "zip",
    "target": {
      "source": "dist",
      "sourceEnv": "GENERIC_TARGET_SOURCE",
      "output": "artifacts/generic",
      "outputEnv": "GENERIC_TARGET_OUTPUT",
      "destination": "published",
      "destinationEnv": "GENERIC_TARGET_DESTINATION"
    }
  }
}
```
