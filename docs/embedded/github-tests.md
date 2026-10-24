# embedded:github-tests

An opt-in, stack-neutral destination for TRX, JUnit XML, NUnit 2/3 XML, and xUnit XML.
Uses built-in parsers and the GitHub Checks API: no `dorny/test-reporter`, Node installation,
host `gh`, or container is needed for test processing/Checks.

Import after the test toolchain:

```yaml
extends:
  - embedded:standard
  - embedded:dotnet
  - embedded:github-tests
vars:
  githubTests:
    paths: "{{outputs.tests.results}}/**/*.trx"
    checkName: Application tests
```

Read the [shared configuration, permissions, Actions, matrix, privacy and output
reference](github-reports.md). All shared settings live under `vars.githubTests`.
For downloadable reports from ordinary shell steps, use the documented
[upload-action example](github-reports.md#recommended-shell-workflow-upload-the-local-reports-afterwards)
instead of requiring Actions runtime credentials in Rexo.

## Test-specific defaults and overrides

| Variable | Default | Contract |
| --- | --- | --- |
| `paths` | empty | Automatically examine `outputs.tests.results/**/*.trx` and `**/*.xml`. Set explicit globs for other locations/stacks. |
| `format` | `auto` | `auto`, `trx`, `junit`, `nunit`, `xunit`. Detect from document structure, not `.xml` alone. |
| `outputDirectory` | `{{outputs.tests.results}}/github-report` | Generated Markdown directory. |
| `artifactName` | `test-results` | Raw reports plus generated Markdown. |
| `checkName` | `Rexo tests` | Scope is appended to the Check name. |
| `sourceRoot` | empty | Strip a build-machine/container source prefix from failure paths before mapping to repository-relative paths. |
| `includeOutput` | `false` | Include captured report console output in failure details only when explicitly enabled; may disclose sensitive data. |

```yaml
extends:
  - embedded:standard
  - embedded:node
  - embedded:github-tests
vars:
  githubTests:
    paths: artifacts/junit/**/*.xml
    format: junit
    sourceRoot: /workspace
    annotationLimit: 100
```

Configure the Node test runner itself to emit JUnit; this policy consumes reports and
does not install a test framework/reporter. `rx reports tests --publish-reports` processes
existing reports and opts into publishing outside Actions.

Failed tests produce a failed Check **and** a failed Rexo reporting step even when the
runner exited zero. Skipped tests are not failures. Failure details are bounded, HTML-escaped,
and masked for known secrets. Source annotations require a valid source path and line
that can map to an existing repository file; many TRX reports only provide test names,
so their failures remain visible in the summary without fabricated source annotations.

XML parsing prohibits DTDs and applies a 16 MiB document limit. Reports are summed by
test case, not by suite aggregates. Separate input reports are separate executions;
do not select copies of the same report unless you intend to count those executions twice.
