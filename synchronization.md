# Background note synchronization with Git

## Purpose

NoteManager should synchronize the currently opened notes repository by using
the existing Git for Windows installation and the repository's existing remote,
branch, credentials, and author configuration.

Synchronization is intentionally repository-scoped:

- the selected folder must itself be the Git work-tree root;
- Git must already be available through `PATH` or an explicitly configured
  executable path;
- the current branch must have an upstream remote;
- authentication and `user.name` / `user.email` must already be configured;
- NoteManager must never create a repository, select a remote, create
  credentials, change `safe.directory`, or resolve conflicts automatically.

If the selected folder is not a Git work tree, NoteManager remains idle. It
must not create `.git`, stage files, display repeated errors, or attempt pull
or push operations.

## User-visible behavior

When a Git-backed folder is open, NoteManager should:

1. wait for the configured synchronization interval;
2. pull remote changes in a background task;
3. stop the cycle and write a detailed log if pull fails or produces a
   conflict;
4. after a successful pull, stage all eligible additions, changes, renames,
   and deletions;
5. commit staged changes when any exist;
6. push the current branch to its already configured upstream;
7. wait for the full configured interval after the final Git command in the
   cycle has exited before beginning another cycle.

The default interval is five minutes. Git work must not run on the WPF
dispatcher thread, and only one synchronization cycle may operate on a
repository at a time.

The current status can reuse the existing main-toolbar status area, for
example:

- `Git sync scheduled`
- `Pulling remote changes…`
- `Committing 4 changed files…`
- `Git synchronization complete`
- `Git synchronization needs attention; see the log`

Success notifications should be quiet. Failures should remain visible until
the next successful cycle or a folder change.

## Application configuration

Add a configuration file named `NoteManager.settings.json` to the application
output and installer. A proposed section is:

```json
{
  "GitSynchronization": {
    "Enabled": true,
    "IntervalMinutes": 5,
    "GitExecutablePath": "git.exe",
    "CommandTimeoutSeconds": 180,
    "CommitMessagePrefix": "NoteManager automatic sync",
    "PullStrategy": "RebaseWithAutoStash"
  }
}
```

Configuration rules:

- `Enabled` defaults to `true`.
- `IntervalMinutes` defaults to `5` and should be restricted to a reasonable
  range such as 1 through 1440.
- `GitExecutablePath` defaults to `git.exe`, allowing normal Windows `PATH`
  resolution. An absolute path may be supplied when Git is not on `PATH`.
- `CommandTimeoutSeconds` bounds a hung network or credential operation. A
  value such as 180 seconds is a practical default.
- `CommitMessagePrefix` must be a single line. A generated message can be
  `NoteManager automatic sync 2026-07-27 14:30:00 +02:00`.
- `PullStrategy` should initially support one documented value. Adding more
  policies later must not silently change existing installations.

Invalid configuration should not crash startup. The application should use
safe defaults, show a concise status, and record the validation problem in the
daily Git synchronization log.

## Proposed architecture

Keep Git behavior outside `MainViewModel` so it can be tested without WPF:

```text
Configuration/
  GitSynchronizationOptions.cs

Services/
  GitProcessRunner.cs
  GitRepositoryInspector.cs
  GitExclusionService.cs
  GitSynchronizationService.cs
  GitSynchronizationCoordinator.cs
  GitSynchronizationLog.cs

Models/
  GitCommandResult.cs
  GitRepositoryState.cs
  GitSynchronizationResult.cs
  GitSynchronizationStatus.cs
```

Responsibilities:

- `GitProcessRunner` starts `git.exe`, passes arguments without a command
  shell, captures standard output/error, applies timeouts and cancellation,
  and returns the exit code and duration.
- `GitRepositoryInspector` determines whether the selected folder is a valid
  work-tree root, identifies the branch/upstream, and detects unfinished merge,
  rebase, cherry-pick, revert, or conflicted-index state.
- `GitExclusionService` applies the dotted-directory rule, respects all
  applicable `.gitignore` files, and identifies already tracked paths which
  must be removed from the repository index.
- `GitSynchronizationService` performs one deterministic pull-stage-commit-push
  cycle.
- `GitSynchronizationCoordinator` owns scheduling, folder-generation
  cancellation, non-overlap, and UI status notifications.
- `GitSynchronizationLog` writes sanitized, serialized daily text logs and
  performs retention.

All services should accept abstractions such as `TimeProvider`, an interface
for the Git runner, and a log interface so scheduler and failure behavior can
be tested deterministically.

## Repository detection and scope

Run these checks when a folder is opened, and repeat the lightweight checks
before every cycle:

```text
git -C <selected-folder> rev-parse --is-inside-work-tree
git -C <selected-folder> rev-parse --show-toplevel
git -C <selected-folder> symbolic-ref --quiet --short HEAD
git -C <selected-folder> rev-parse --abbrev-ref --symbolic-full-name @{upstream}
```

The work-tree root returned by `--show-toplevel` must be path-equal to the
selected folder after full-path normalization. If the selected folder is only
a subfolder of a larger repository, synchronization should be skipped and
logged as a scope warning. This prevents NoteManager from committing unrelated
files outside the folder the user opened.

The following states must prevent mutation:

- not inside a work tree;
- bare repository;
- detached `HEAD`;
- no configured upstream;
- existing unmerged index entries;
- merge, rebase, cherry-pick, or revert already in progress;
- `.git/index.lock` owned by another Git process;
- repository path rejected by Git's `safe.directory` protection.

NoteManager should never add a `safe.directory` entry automatically. That is a
trust decision for the user or administrator.

## Scheduling and concurrency

Do not use a fixed-rate timer because it can overlap a slow pull or push.
Implement an asynchronous loop with completion-based delay:

```text
folder selected
  -> validate repository
  -> wait IntervalMinutes
  -> acquire repository synchronization lock
  -> run one complete synchronization cycle
  -> record the time the final Git process exited
  -> release the lock
  -> wait the full IntervalMinutes
  -> repeat
```

This precisely implements "five minutes after the last Git command has
finished." A 12-minute pull therefore does not cause queued or concurrent
cycles.

Use:

- a `CancellationTokenSource` per selected-folder generation;
- a `SemaphoreSlim(1, 1)` inside the coordinator;
- a named mutex derived from a hash of the normalized repository root to
  prevent two NoteManager processes from synchronizing the same repository;
- a monotonic clock through `TimeProvider` for delay calculations.

On folder change, cancel a pending delay immediately. If Git is already
running, request cancellation and wait for an orderly exit before starting
the next folder. If a timeout requires terminating Git, kill only the process
tree created by NoteManager, then log the termination and check for repository
lock or operation state before any later cycle.

On application close, stop scheduling and allow a short bounded grace period
for the current Git process. Closing the window must not wait indefinitely.

## Saving the active note before synchronization

Git can only synchronize content already written to disk. The current editor
may still contain an unsaved dirty note because NoteManager normally saves on
selection/view changes and shutdown.

Before the pull begins:

1. use the WPF dispatcher only to snapshot the active note path, text, and edit
   version;
2. write the snapshot atomically on a worker thread;
3. return to the dispatcher and clear the dirty flag only if the edit version
   is unchanged;
4. if the user edited again during the write, keep the note dirty for the next
   save/cycle.

The existing atomic-save implementation should be extracted into a reusable
service. The background synchronizer must not read mutable view-model state or
block editing while network Git commands run.

## Git process execution

Start Git with `ProcessStartInfo`:

- `UseShellExecute = false`;
- `CreateNoWindow = true`;
- redirect standard output and standard error;
- use `ArgumentList` rather than building a quoted command string;
- set the working directory through `git -C <repository-root>`;
- set `GIT_TERMINAL_PROMPT=0` so a background process never waits on a hidden
  terminal prompt;
- set the appropriate Git Credential Manager non-interactive variable when
  supported by the installed version;
- read stdout and stderr asynchronously to avoid pipe deadlocks;
- include a command-specific timeout and cancellation token.

Do not invoke `cmd.exe`, PowerShell, or `bash`. Do not log environment
variables, access tokens, credential-helper output, or URLs containing embedded
userinfo. Sanitize remote URLs and command output before persistence.

Repository hooks are executed by normal Git commands. The implementation must
make an explicit product decision: either honor the trusted repository's hooks
and log hook failures, or disable hooks for NoteManager commands. This decision
must be configuration-backed and documented; it must not vary silently.

## One synchronization cycle

### 1. Preflight

Validate the repository root, current branch, configured upstream, operation
state, lock state, configuration, and Git executable. Capture:

```text
git status --porcelain=v2 --branch -z --untracked-files=all
```

Porcelain v2 with NUL delimiters is safe for spaces, Unicode, tabs, and
newlines in file names. Never parse localized human-readable `git status`
output.

If a conflict already exists, log the conflicted paths and stop. Do not stage,
commit, push, reset, checkout, or resolve files.

### 2. Pull before committing local changes

The repository can contain unstaged or staged changes. The recommended initial
policy is:

```text
git pull --rebase --autostash
```

This downloads and integrates upstream commits, rebases any local commits which
may remain after an earlier failed push, and temporarily protects tracked
working-tree changes.

Important limitations:

- untracked files are not protected by autostash and can cause pull to fail if
  an upstream commit introduces the same path;
- a rebase or autostash reapply can produce conflicts;
- repository configuration and hooks can still reject the operation.

Any non-zero exit code is a failed pull. Capture stdout, stderr, exit code,
duration, current branch state, and:

```text
git diff --name-only --diff-filter=U -z
```

If conflicts exist, log each path and stop the cycle. Leave the repository in
Git's conflict state for manual inspection; do not guess a resolution or run a
destructive reset. Later cycles should detect the unfinished operation and
remain idle until the user resolves or aborts it.

No staging, commit, or push may occur unless pull completed successfully.

### 3. Enforce exclusions

The exclusion policy has two independent parts.

#### Dotted directories

Any directory whose individual name starts with a dot is excluded at any
depth, for example:

```text
.notes/
.git/
.obsidian/
projects/.cache/
```

Ordinary dot-prefixed files are not excluded by this rule unless `.gitignore`
also excludes them.

Add a managed pattern to `.git/info/exclude`, bracketed by comments owned by
NoteManager. A directory-only pattern such as `.*/` must be verified against
the supported Git for Windows version for both root and nested directories.
The staging command must also use an explicit exclude pathspec as defense in
depth. Integration tests, rather than assumptions about glob semantics, are
required.

#### Existing `.gitignore` rules

Git already applies the root and nested `.gitignore` files, `.git/info/exclude`,
and configured global excludes when adding untracked content. Use:

```text
git check-ignore --stdin -z
```

when the application needs to explain why a path is excluded. Do not implement
a separate approximation of Git's ignore language.

#### Already tracked excluded content

Ignore rules do not affect files already tracked by Git. To satisfy the
requirement that excluded paths are removed from the repository:

1. enumerate tracked paths with `git ls-files -z`;
2. identify paths below a dotted directory;
3. enumerate tracked-but-ignored paths with
   `git ls-files -ci --exclude-standard -z`;
4. combine and deduplicate the path set;
5. remove those paths from the index with `git rm --cached`, using
   `--pathspec-from-file` and `--pathspec-file-nul` so unusual names remain
   safe;
6. leave the physical files untouched;
7. include the index removals in the normal synchronization commit.

This is a material repository change: a previously tracked `.github`,
`.obsidian`, or other dotted directory will be deleted from the remote after
the next push while remaining on the local disk. The application documentation
and release notes must state this consequence clearly.

### 4. Stage every eligible change

Stage additions, modifications, renames, and deletions:

```text
git add -A -- . <explicit dotted-directory exclude pathspecs>
```

`-A` is required so deleted notes and renamed files are synchronized. Explicit
dotted-directory exclusions take precedence over any negating `.gitignore`
rule. After staging, verify the index:

```text
git diff --cached --name-only -z
```

No staged path may contain a dotted directory segment or be ignored according
to the effective ignore rules. If validation fails, log it and stop before
commit.

### 5. Commit when needed

Determine whether the index has changes:

```text
git diff --cached --quiet
```

Exit code 0 means there is nothing new to commit; exit code 1 means changes are
present; any other code is a failure.

When changes exist:

```text
git commit -m "<configured-prefix> <local-timestamp-with-offset>"
```

A missing author identity, failed hook, locked index, or any other non-zero
result is a failed cycle and must be logged. Never change `user.name` or
`user.email` automatically.

When no changes exist, continue to push because a prior cycle may have created
a local commit whose push failed.

### 6. Push

Use the current branch's configured upstream:

```text
git push
```

Do not guess a remote or run `--set-upstream`. A rejected push, authorization
failure, hook failure, network error, or timeout is logged. The local commit is
preserved; the next successful pull/rebase cycle should retry the push.

After push, record a concise success summary containing the branch, commit
identifier, number of committed paths, and elapsed time. Do not log complete
note contents.

## Failure and no-op behavior

| Situation | Required behavior |
| --- | --- |
| Git executable unavailable | Disable cycles for the active folder, show a status, write a daily log entry |
| Selected folder is not a repository | Remain idle; do not create or modify repository data |
| Selected folder is below a larger Git root | Skip synchronization to prevent out-of-scope commits |
| Repository has no upstream or is detached | Log actionable details; do not pull, commit, or push |
| Pull fails | Log command/result and stop before staging |
| Pull/rebase/autostash conflict | Log conflicted paths and stop; never auto-resolve |
| Commit has no changes | Treat as normal and still attempt push of existing local commits |
| Commit fails | Log and stop before push |
| Push fails | Log; preserve local commits for retry |
| Folder changes during a cycle | Cancel safely, ignore stale UI updates, and do not start the new folder until the old Git process exits |
| Application closes during a cycle | Cancel with a bounded grace period; never block shutdown indefinitely |
| Another NoteManager instance owns the repository lock | Skip the cycle and retry after the interval |
| External Git process owns an index lock | Skip mutation, log once, and retry later |

Repeated identical failures should be coalesced in the UI, while every attempted
cycle may still have a timestamped log record.

## Daily text logs and one-month retention

Write logs under:

```text
<application-directory>\logs\NoteManager-GitSync-YYYY-MM-DD.log
```

Each entry should contain:

- local timestamp with UTC offset;
- cycle/correlation identifier;
- normalized repository root;
- branch and sanitized upstream name;
- operation name;
- sanitized command arguments;
- start time, duration, and exit code;
- bounded stdout/stderr;
- conflicted paths when present;
- final classification such as success, skipped, cancelled, timeout, or
  failure.

Use a single asynchronous writer or `SemaphoreSlim` so concurrent status
callbacks cannot interleave lines. Limit captured output per command and mark
truncation.

At application startup and after opening a new daily log, parse the date in
matching log file names and delete files older than `DateTime.Today.AddMonths(-1)`.
Do not delete unrelated files from the log directory. Rotation failure must not
break synchronization; record it in the current log when possible.

The installer currently targets `{autopf}`, which is normally protected for
standard users. To meet the explicit application-directory logging
requirement, add an Inno Setup `[Dirs]` entry for `{app}\logs` with the minimum
permission needed for normal users to create and rotate log files. Do not grant
modify permission to the executables or the whole application directory.

If the log directory cannot be written, show an explicit UI status. A
best-effort fallback such as Windows Event Log may be useful, but it does not
replace fixing the required application-directory log permissions.

## Detailed implementation task list

### Configuration

- [ ] Add `NoteManager.settings.json` with the `GitSynchronization` section and
  five-minute default.
- [ ] Copy the file to Debug, Release, publish, and installer outputs.
- [ ] Implement `GitSynchronizationOptions` parsing, defaults, bounds, and
  validation.
- [ ] Add tests for missing, malformed, zero, negative, extremely large, and
  valid interval values.
- [ ] Decide and document whether repository hooks run for background commands.

### Process execution

- [ ] Implement `GitProcessRunner` with `ProcessStartInfo.ArgumentList`.
- [ ] Capture stdout and stderr asynchronously and without deadlocks.
- [ ] Implement per-command timeout and cancellation.
- [ ] Disable hidden credential prompts.
- [ ] Add bounded output capture and credential/URL redaction.
- [ ] Return a structured result including exit code, duration, cancellation,
  timeout, and sanitized output.
- [ ] Unit-test argument handling for spaces, Unicode, quotes, and special file
  names.

### Repository inspection

- [ ] Detect `git.exe` without using a shell.
- [ ] Verify the selected folder is the exact work-tree root.
- [ ] Detect bare repositories, detached `HEAD`, missing upstream, unsafe
  directory rejection, index locks, and unfinished Git operations.
- [ ] Parse porcelain v2 and all path lists as NUL-delimited data.
- [ ] Represent repository state in a typed model rather than string matching
  localized output.

### Exclusions

- [ ] Implement a path-segment predicate for directories beginning with `.`.
- [ ] Add and maintain a clearly delimited NoteManager block in
  `.git/info/exclude`.
- [ ] Verify root and nested dotted-directory patterns against Git for Windows.
- [ ] Use explicit exclude pathspecs in the staging command.
- [ ] Delegate `.gitignore` evaluation to Git through standard add/check-ignore
  behavior.
- [ ] Detect tracked files below dotted directories.
- [ ] Detect tracked files made ignored by effective ignore rules.
- [ ] Remove excluded tracked paths from the index without deleting local files.
- [ ] Validate the staged index contains no prohibited path before commit.
- [ ] Test nested `.gitignore`, negation rules, spaces, Unicode, and dotted
  directories at multiple depths.

### Synchronization cycle

- [ ] Extract atomic note saving into a reusable, version-aware service.
- [ ] Snapshot and save the dirty active note before pull without holding the
  UI thread during Git work.
- [ ] Implement preflight and an explicit state machine for pull, exclusion,
  stage, commit, and push.
- [ ] Use `git pull --rebase --autostash` for the documented initial policy.
- [ ] Detect and log pull/rebase/autostash conflicts.
- [ ] Stop immediately after any failed prerequisite or command.
- [ ] Stage all eligible additions, changes, renames, and deletions.
- [ ] Commit only when the index differs.
- [ ] Push even when no new commit was needed so earlier failed pushes recover.
- [ ] Publish status changes to WPF through the dispatcher without blocking it.

### Scheduler and lifecycle

- [ ] Implement completion-based delay using the configured interval.
- [ ] Ensure cycles never overlap.
- [ ] Add a repository-scoped named mutex for multiple application instances.
- [ ] Start/replace the coordinator when `LoadMarkdownFolderAsync` succeeds.
- [ ] Cancel pending delays on folder change and application shutdown.
- [ ] Guard UI callbacks with the existing folder-generation concept so stale
  repository results cannot update the new folder.
- [ ] Add a bounded shutdown policy for active Git processes.
- [ ] Ensure non-repositories remain silent and command-free.

### Logging and installer

- [ ] Implement daily text logs in `<application-directory>\logs`.
- [ ] Include correlation IDs, command outcomes, conflicts, and actionable
  diagnostics.
- [ ] Redact credentials and bound stdout/stderr size.
- [ ] Serialize log writes.
- [ ] Delete only matching daily logs older than one calendar month.
- [ ] Add `{app}\logs` to the Inno Setup script with normal-user write/rotate
  permission.
- [ ] Verify logging under an installed standard-user account.

### Automated tests

- [ ] Create a disposable bare remote and two working clones for end-to-end
  tests.
- [ ] Prove a remote change is pulled before local changes are committed.
- [ ] Prove an added, modified, renamed, and deleted Markdown file reaches the
  remote.
- [ ] Prove non-Markdown eligible files are also committed, as required by
  "any other files."
- [ ] Prove root and nested dotted directories never reach the remote.
- [ ] Prove root and nested `.gitignore` rules are respected.
- [ ] Prove already tracked dotted/ignored content is removed from the index
  but remains on disk.
- [ ] Prove no command runs for a non-repository folder.
- [ ] Prove missing Git, missing upstream, missing author identity, rejected
  authentication, pull failure, commit failure, and push failure are logged.
- [ ] Create a real two-clone content conflict and prove it is logged without
  automatic resolution or push.
- [ ] Prove a failed push's local commit is pushed by a later successful cycle.
- [ ] Prove a long command never causes overlapping cycles.
- [ ] Prove the next cycle begins only after the full interval following the
  previous final command.
- [ ] Prove folder switching and shutdown cancel safely.
- [ ] Prove the WPF dispatcher remains responsive during a delayed pull/push.
- [ ] Prove active-note edits are atomically saved and included without losing
  edits typed during the save.
- [ ] Prove log files rotate after one calendar month and unrelated log files
  remain untouched.

### Documentation and release

- [ ] Add configuration and operational instructions to `README.md`.
- [ ] Document the consequence of untracking existing dotted directories.
- [ ] Explain how users resolve conflicts and where daily logs are located.
- [ ] Document that NoteManager does not configure repositories, remotes,
  credentials, author identity, or trust.
- [ ] Include synchronization checks in the Release and installer validation
  scripts.

## Acceptance criteria

The feature is ready when all of the following are true:

1. A configured Git repository synchronizes eligible local and remote changes
   without freezing note editing or navigation.
2. Each new cycle starts only after the configured number of minutes has
   elapsed since the previous cycle's final Git command exited.
3. Non-repository folders cause no pull, stage, commit, or push.
4. No file below a dotted directory and no effectively ignored file is added
   to a synchronization commit.
5. Previously tracked excluded content is removed from the repository while
   remaining on the local disk.
6. Pull always succeeds before NoteManager stages, commits, or pushes current
   working-tree changes.
7. Conflicts and every Git command failure stop the cycle, preserve user data,
   and produce an actionable daily text log.
8. Daily logs are writable in the installed application log directory and only
   the most recent calendar month is retained.
9. A failed push preserves its local commit and a later cycle can recover.
10. Automated unit, integration, scheduler, UI-responsiveness, and installed
    logging tests all pass.

