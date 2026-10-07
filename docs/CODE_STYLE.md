# Code Style and Reliability

This guide records patterns that keep Rexo portable, predictable, and easy to review. The
repository build treats analyzer warnings as errors; resolve the underlying issue rather than
silencing an analyzer.

## Paths

- Never hard-code a platform directory separator in path assertions. Build expected paths with
  `Path.Join` and use `Path.DirectorySeparatorChar` when a separator is part of the assertion.
- `Path.Combine(root, laterPart)` treats a rooted `laterPart` as a new path and discards `root`.
  Do not rely on that behavior implicitly. If a setting may be absolute, branch explicitly:

  ```csharp
  var fullPath = Path.GetFullPath(
      Path.IsPathRooted(configuredPath)
          ? configuredPath
          : Path.Join(repositoryRoot, configuredPath));
  ```

- If a value must be repository-relative, reject rooted values and traversal segments before
  resolving it. Normalize the result and verify containment under the intended root; include
  symbolic-link resolution when the operation must remain inside the repository.
- `Path.Join` appends its inputs instead of resetting on a rooted later component, but it does not
  validate untrusted paths. Validate components and enforce containment where the boundary matters.
- Use `Path.Join` for append-only joins even when later components are literals, generated file
  names, hashes, or already checked with `Path.IsPathRooted`. Review every join in a touched file,
  including temporary files, caches, and fallback branches, not just the reported line.
  This rule applies to test fixtures and expected paths as well as production code.
- Add tests for both relative and rooted inputs when both are supported. Keep path expectations
  platform-neutral so the same test passes on Windows and Unix.

## Exceptions and cleanup

- Do not leave catch blocks empty. Handle only expected exceptions, narrowly, and either report the
  cleanup failure or document the benign race in executable diagnostics. Do not hide failures that
  leave the requested operation incomplete.
- For best-effort cleanup, report the affected path and exception message to standard error. Cleanup
  failure should not replace the primary operation's result, but it must remain observable.
- For races such as a child process exiting just before cancellation tries to kill it, filter the
  expected exception to that state. Let failures outside the expected state propagate.

## Collection transforms and conditions

- Prefer `Select` for a simple one-to-one projection and `Where` for filtering. Keep imperative
  loops when they perform meaningful side effects, validation, or branching that is clearer as a
  loop.
- Project the actual value the body needs; do not introduce a new alias of the iteration variable
  as the first statement. Extract a normalization helper if the mapping requires several steps.
  For stateful traversal, update the accumulator directly inside the loop rather than capturing
  mutable state in a `Select` lambda. Path traversal through symbolic links must stay sequential.
- Avoid redundant or constant-condition checks in switch expressions and boolean branches. Express
  precedence directly, then test each meaningful input state (including unset and override cases).

## Review follow-through

After a fix is pushed, inspect feedback on the new head commit. A successful analysis check means
the analyzer ran successfully, not that it found no quality issues. Re-read the current inline
comments and scan sibling code for the same pattern before declaring the review addressed.
