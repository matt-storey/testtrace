# testtrace

**Run only the tests your change could have affected.**

Point testtrace at two builds of your solution. It compares the compiled assemblies,
works out which tests could possibly be impacted, and prints a filter you hand straight
to your test runner.

```bash
$ testtrace analyze --solution MyApp.slnx --manifest baseline.json --test-framework nunit --format filter
FullyQualifiedName=MyApp.Tests.OrderTests.Total|FullyQualifiedName=MyApp.Tests.OrderTests.Discount
```

The one rule worth trusting it on: **it never silently skips a test that might be
affected.** If it cannot work something out — a package upgrade, a config file, a
missing baseline, any error at all — it says "run everything" rather than guess.

Supports **NUnit**, **xUnit**, **TUnit** and **MSTest**, on **.NET 8** and **.NET 10**.

---

## Install

```bash
dotnet tool install --global TestTrace
testtrace --help
```

Or pin it to the repo, which is what you want in CI:

```bash
dotnet new tool-manifest        # once per repo
dotnet tool install TestTrace
dotnet tool restore             # on each machine / CI agent
dotnet testtrace --help
```

## Quick start

Record what "before" looks like, make a change, then query what has changed:

```bash
dotnet build -c Release
testtrace snapshot --solution MyApp.slnx -o baseline.json

# ...edit a method, then rebuild...
dotnet build -c Release

testtrace analyze --solution MyApp.slnx --manifest baseline.json \
    --test-framework nunit --explain
```

`--explain` shows the changed methods, the tests it picked, and the chain of calls
joining them:

```
summary:   1 changed method(s) -> 3 impacted test(s)

selected tests (3):
  MyApp.Tests.OrderTests.Total_SumsLines
      why: changed method MyApp.Orders.Money::Times reaches this test in 2 hop(s)
      via: MyApp.Orders.Money.Times
        -> MyApp.Orders.OrderService.CalculateTotal
        -> MyApp.Tests.OrderTests.Total_SumsLines
```

**Always rebuild before `analyze`.** It reads compiled assemblies, so an edit you
haven't built yet is invisible to it.

## Run the tests it picks

The two-liner, for a single test project:

```bash
filter=$(testtrace analyze --solution MyApp.slnx --manifest baseline.json \
    --test-framework nunit --format filter)

dotnet test MyApp.Tests --filter "$filter"
```

This is always *correct* — an empty filter means "no filter", so a `RUN_EVERYTHING`
result runs the whole suite. To also make it *fast*, skip the run entirely when nothing
was affected (exit code 3):

```bash
if filter=$(testtrace analyze --solution MyApp.slnx --manifest baseline.json \
        --test-framework nunit --format filter); then
    dotnet test MyApp.Tests --filter "$filter"
else
    echo "nothing affected — skipping tests"
fi
```

### More than one test project

A single combined filter fails against a project that contributed no tests. Use
`--format filter-lines`, which prints one `<dll><TAB><runner><TAB><filter>` line per
assembly and skips empty ones:

```bash
testtrace analyze --solution MyApp.slnx --manifest baseline.json \
    --test-framework nunit --format filter-lines > lines.txt

while IFS=$'\t' read -r dll runner filter; do
    if [ -n "$filter" ]; then
        dotnet vstest "$dll" --TestCaseFilter:"$filter"
    else
        dotnet vstest "$dll"          # empty filter = run this assembly whole
    fi
done < lines.txt
```

### TUnit

TUnit runs on Microsoft.Testing.Platform, so its test project *is* the runner and it
takes a different flag. The `runner` column tells you which you got — `VsTest` or
`TreeNode`:

```bash
testtrace analyze --solution MyApp.slnx --manifest baseline.json \
    --test-framework tunit --format filter-lines
# MyApp.Tests.dll   TreeNode   /MyApp.Tests/*/(OrderTests)/(Total|Discount*)

./MyApp.Tests --treenode-filter "/MyApp.Tests/*/(OrderTests)/(Total|Discount*)"
```

## Use it in CI

testtrace has no idea what CI or git are — you give it two builds and it does the rest.
The usual wiring:

**On every `main` build**, publish a baseline artifact keyed by commit:

```bash
dotnet build -c Release
testtrace snapshot --solution MyApp.slnx -o testtrace-manifest.json
# upload testtrace-manifest.json as an artifact named for $GIT_SHA
```

**On every PR**, fetch the baseline for the merge-base and filter against it:

```bash
BASE=$(git merge-base origin/main HEAD)
# download the artifact published for $BASE into baseline.json

dotnet build -c Release

if filter=$(testtrace analyze --solution MyApp.slnx --manifest baseline.json \
        --test-framework nunit --format filter); then
    dotnet test --filter "$filter"
else
    echo "no affected tests"
fi
```

If the exact baseline is missing — expired artifact, skipped build — use an older
ancestor's. An older baseline only ever selects *more* tests, never fewer.

### No baseline? Use your diff instead

Without a manifest, testtrace can work from a diff alone. Less precise, but zero setup:

```bash
git diff -U0 $(git merge-base origin/main HEAD) > changed.diff

testtrace analyze --solution MyApp.slnx --changed-files changed.diff \
    --test-framework nunit --format filter
```

Use `-U0`: context lines widen each range and pull in neighbouring methods.

## Pick your test framework

`--test-framework` is required, because the runners' filter syntaxes are mutually
unintelligible and guessing wrong would emit a filter that runs the *wrong* tests
rather than failing. One run targets one framework; if a solution mixes them, run
testtrace once per framework.

| Value | Runner | You run tests with |
|---|---|---|
| `nunit` | VSTest | `dotnet test --filter` / `dotnet vstest --TestCaseFilter:` |
| `xunit` | VSTest | same |
| `mstest` | VSTest | same |
| `tunit` | Microsoft.Testing.Platform | `./YourTests --treenode-filter` |

## Exit codes

| Code | Meaning | What to do |
|---|---|---|
| **0** | Usable result | Run the printed filter. If it is empty, run everything. |
| **3** | Nothing affected | Skip the test run. |
| **2** | Can't express this request | See stderr — usually a multi-project TUnit filter; use `--format filter-lines` or `--assembly`. |

Analysis *failures* deliberately have no code of their own: they come back as exit 0
with an empty filter, so a naive script still runs the full suite instead of skipping it.

## Common options

| Option | What it does |
|---|---|
| `--solution <file>` | `.sln`/`.slnx` — resolves every project's build output for you |
| `--project <files>` | Point at project files instead; references are followed |
| `--current <dir>` | Or name a build output directory directly |
| `--manifest <file>` | The baseline from `snapshot` |
| `--changed-files <file>` | A diff or path list, when you have no baseline |
| `--test-framework <name>` | **Required.** `nunit`, `xunit`, `tunit`, `mstest` |
| `--format <shape>` | `filter` (default `json`), `filter-lines`, `runsettings` |
| `--explain` | Human-readable report of what was picked and why |
| `--assembly <name>` | Restrict output to one test project |
| `--always-run <patterns>` | Test-name wildcards pinned into every selection |
| `-c`, `-f` | Configuration and target framework to locate outputs for |

Run `testtrace analyze --help` for the rest.

## Things worth knowing

**Reflection is invisible.** A test that reaches your changed code only through
`MethodInfo.Invoke`, `Activator` or a dynamic proxy will not be selected — no static
call edge exists to follow. Use `--always-run "*ReflectionTests*"` to pin such suites.

**Config files are covered, but only through the build output.** `appsettings.json` and
friends are hashed, so editing one forces a full run. Files that never reach the output
directory are not seen; add them to `--force-full-run-paths` if they matter.

**Both sides must match.** Baseline and current need the same target framework, the same
`--include-assemblies` scope, and ideally the same SDK. Any mismatch is detected and
fails open to running everything rather than producing a narrowed answer.

**It picks more than the minimum, on purpose.** Ambiguity always resolves toward
including a test. Over-selecting costs a little time; under-selecting costs you a
missed regression.

## Digging deeper

[`docs/design.md`](docs/design.md) covers how it works and why — the IL-hashing scheme,
the call graph and its over-approximation edges, framework-by-framework detection
rules, performance and scaling measurements, and the repository layout and test suites.
