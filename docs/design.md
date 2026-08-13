# testtrace — design notes

How testtrace works, and why it is built this way. For installing and using it see the
[README](../README.md); this document assumes you have already run it.

Everything here follows from four commitments:

- **Over-approximate.** Selecting too many tests is a minor cost; missing an affected
  test is the failure mode. Every ambiguous case resolves toward "include it".
- **Fail open.** Any error, missing baseline, or unanalysable change produces
  `RUN_EVERYTHING` with exit 0 — never a silently narrowed selection.
- **No instrumentation, no baseline test runs.** Static analysis of IL only; the tool
  works with nothing but two directories of DLLs.
- **Environment-agnostic.** No git or CI knowledge inside the tool.

Comparing compiled assemblies rather than source is what lets it see source-generator
output, analyzer changes, transitive package bumps and `Directory.Build.props` edits
that a source diff would miss or mis-attribute — and, equally, correctly select nothing
for an edit that compiles to identical IL, such as a comment or an `a * b + 0` that
Roslyn folds away.

Supports .NET 8 and .NET 10 (`net8.0;net10.0`); the analysis itself is runtime-agnostic
and the scenario suite runs against both.

## Commands

```bash
testtrace snapshot (--solution <file> | --project <files> | --input <dir>) -o <manifest.json>
testtrace analyze  (--solution <file> | --project <files> | --current <dir>) [--manifest <file> | --baseline <dir>]
                   --test-framework <nunit|xunit|tunit|mstest> [options]
```

### Project mode

Point at one or more project files and testtrace resolves their build output — no
`bin/<config>/<tfm>` paths to type:

```bash
testtrace snapshot --project tests/MyTests.csproj -f net8.0 -o baseline.json
testtrace analyze  --project tests/MyTests.csproj -f net8.0 --manifest baseline.json --test-framework nunit --format filter
```

Project references are followed transitively, so naming a test project puts the code
under test in scope too. Without that, a change to the code being tested would be
untraceable and every run would fall open to `RUN_EVERYTHING`.

### Solution mode

Point at a `.sln` or `.slnx` and testtrace resolves every project's build output for
you, instead of you naming directories:

```bash
testtrace snapshot --solution MySolution.slnx -f net8.0 -o baseline.json
testtrace analyze  --solution MySolution.slnx -f net8.0 --manifest baseline.json --test-framework nunit --format filter-lines
```

This is the right mode for a whole solution: several test projects each have their own
`bin`, and pointing `--current` at a repository root would also sweep `obj/`
intermediates. It also gives a better scope signal than the PDB heuristic — the
projects listed in the solution *are* your code, so they become the analysis scope
automatically (`--include-assemblies` still overrides).

| Option | Meaning |
|---|---|
| `--solution <file>` | `.sln` or `.slnx`; replaces `--input`/`--current` |
| `--project <files>` | One or more project files; replaces `--input`/`--current`. Mutually exclusive with `--solution` |
| `--configuration`, `-c` | Configuration whose output to locate (default `Release`) |
| `--framework`, `-f` | Target framework to locate, e.g. `net8.0` |

Output directories are resolved **by convention** — `<project>/bin/<config>/<tfm>`, plus
the .NET 8+ `artifacts/bin/<project>/<config>` layout. MSBuild is not invoked, which
keeps `TestTrace.Core` on its single dependency and avoids a subprocess per project, but
means a project with a custom `OutputPath` will not resolve. Unresolved projects are
reported as warnings and skipped; `--current` remains the explicit escape hatch.

When projects multi-target and `-f` is omitted, **one** framework is chosen for the
entire solution (the ordinal-first one common to all of them) and a warning is emitted.
Resolving per-project would risk mixing TFMs within a single analysis, which would make
every assembly look changed.

`analyze` inputs:

| Option | Meaning |
|---|---|
| `--manifest <file>` | Baseline manifest (preferred front-end) |
| `--baseline <dir>` | Baseline build directory, snapshotted in-memory when no manifest |
| `--current <dir>` | Current build directory (or use `--solution`) |
| `--changed-files <file>` | Changed paths **or a unified diff** — see below; feeds the escape hatch and the PDB front-end |
| `--format json\|filter\|filter-lines\|runsettings` | Output shape (`json` includes per-test reasons and chains) |
| `--test-framework <name>` | **Required.** Which framework to discover and emit filters for: `nunit`, `xunit`, `tunit` or `mstest` |
| `--assembly <name>` | Restrict filter output to one test assembly, for running a single project |
| `--explain` | Human-readable report: every selected test with the chain from the changed method to it |
| `--force-full-run-paths <globs>` | Changed files that force `RUN_EVERYTHING` (default `**/appsettings*.json`, `**/Migrations/**`, `**/*.razor`, `**/*.cshtml`) |
| `--always-run <patterns>` | Test-name wildcards pinned into every selection (for reflection-heavy suites) |
| `--max-filter-clauses <n>` | Above n clauses an assembly runs whole (default 200) |
| `--output-dir <dir>` | Where `--format runsettings` writes its files |
| `--include-assemblies <globs>` | Assemblies to analyze in depth (default: those with a co-located `.pdb`) |
| `--exclude-assemblies <globs>` | Assemblies to exclude from in-depth analysis |

`snapshot` accepts `--include-assemblies` / `--exclude-assemblies` too, and both sides
must agree — the manifest records its scope and `analyze` fails open on a mismatch.

Exit codes for `analyze`: **0** = usable result (including `RUN_EVERYTHING`);
**2** = the request cannot be expressed (see the tree-path notes below);
**3** = nothing affected, skip the test run entirely. Analysis failures do not get
their own code — they fail open to `RUN_EVERYTHING` with exit 0 and a stderr warning.
The code reflects the *result*, so every `--format` reports it, `--explain` included.

Never mix TFMs: baseline and current must come from the same target framework, and
the manifest records which (`analyze` fails open on a mismatch).

### `--changed-files`

Two shapes are accepted and detected automatically. A plain list, one entry per line:

```
src/Orders/OrderService.cs          # whole file
src/Orders/OrderService.cs:42       # single line
src/Orders/OrderService.cs:42-58    # range
```

Or a unified diff piped straight in — no conversion step. The tool never invokes a
version control system; it just reads the format:

```bash
git diff -U0 $(git merge-base origin/main HEAD) > changed.diff
testtrace analyze --manifest baseline.json --current $BIN --test-framework nunit --changed-files changed.diff
```

Hunk headers supply the line ranges the PDB front-end needs. Prefer `-U0`: context
lines widen each range and pull in neighbouring methods, which over-selects.

Detection uses the unified-diff markers (`---`, `+++`, `@@`) only, so output from any
producer works — `diff -u`, `hg diff`, a `.patch` file. Paths are taken verbatim and
matched by suffix, which absorbs whatever prefix segment the producer added (`b/` from
git, a directory name from `diff -u old/ new/`), without the tool encoding any one
convention.

## Filter syntax

Expressions use `FullyQualifiedName` with `=` (exact), `~` (contains), `!=`/`!~`
(negated), joined by `|` (or) and `&` (and). testtrace emits exact matches for plain
`[Test]` methods and contains-matches for parameterized ones (`[TestCase]`,
`[TestCaseSource]`, `[Theory]`), whose runtime names carry argument lists.

`dotnet test` forwards the filter as an MSBuild property, so a test name containing a
comma — generic type parameters, e.g. `MyTests<Dictionary<string,int>>` — can produce
`MSB1006: Property is not valid`. Contains-matching parameterized tests on
`Class.Method` avoids argument lists, so this only bites generic *test classes*; use
`dotnet vstest`, which has no MSBuild in the path, if you have them.

## Assembly scope and performance

A build output directory is mostly *not your code*: the sample solution's test output
holds **379 assemblies, of which 11 are the application**. Method-hashing all of them
produces 155,043 hashed methods — of which only 284 are the sample's own; the other
154,759 belong to NuGet packages and the framework.

So testtrace splits the work:

- **Every** assembly is content-hashed — one file read and a SHA-256. This is what
  detects transitive package bumps.
- **In-scope** assemblies additionally get method-level hashing and call-graph
  construction. By default an assembly is in scope when a portable `.pdb` sits beside
  it, which is true of projects you build and false of restored packages.

### Non-assembly outputs

`.json` and `.config` files in the build output are hashed as well, and any difference
forces `RUN_EVERYTHING`. They compile to no IL, so a change to one produces *no
assembly diff at all* — without this an `appsettings.json` edit reads as "nothing
affected" and skips the entire suite. `--force-full-run-paths` covers the same ground
from the source side, but only when the caller passes `--changed-files`; this does not
depend on the caller supplying anything.

The extension list is deliberately narrow. `.xml` is excluded because doc-comment files
track source comments, so hashing them would make a comment-only edit select
everything — precisely the over-selection the IL-level design exists to avoid. Files a
test run rewrites (`nunit_random_seed.tmp`, coverage mappings) are excluded for the
same reason in reverse: they would make every analysis after a test run fail open.

Manifests are versioned. A baseline written before this existed records no content
files, which is indistinguishable from "there were none" — so the comparison is
skipped and a warning tells you to retake the baseline, rather than reading every
file as added and failing open forever.

On the sample that is 11 assemblies in depth instead of 379:

| | every assembly | in-scope only |
|---|---|---|
| `snapshot` | 4369 ms | **308 ms** |
| `analyze` (cold graph) | 4659 ms | **281 ms** |
| `analyze` (warm graph) | 4574 ms | **210 ms** |
| manifest size | 29.3 MB | **124 KB** |

Reproduce either column on this repo's own sample — the right-hand one is the default,
and the left is what you get by forcing everything into scope:

```bash
cd sample && dotnet build -c Release -p:TargetFrameworks=net8.0
testtrace snapshot --solution SampleApp.slnx -f net8.0 -o /tmp/scoped.json
testtrace snapshot --solution SampleApp.slnx -f net8.0 -o /tmp/all.json --include-assemblies '*'
```

Two things the numbers show beyond the headline. The warm graph cache barely helps in
the unscoped column (4574 ms against 4659 ms), because there the cost is method-hashing
379 assemblies for the manifest diff, not building the graph. And `analyze` at 210 ms
sits within about 3× of this machine's ~70 ms .NET process-startup floor, so most of
what is left is not analysis at all.

Measured on an M-series Mac with .NET 8; treat them as ratios rather than absolutes.

### Reusing method hashes across a run

`analyze` passes the baseline back into the scan. Any assembly whose content hash is
unchanged keeps its recorded method hashes instead of being re-hashed — sound rather
than heuristic, because the content hash covers the whole PE with only debug regions
zeroed, so unchanged content means byte-identical IL, which cannot hash differently.
A typical change touches one assembly out of hundreds, and method hashing is most of
the cost of a scan.

`snapshot` passes no baseline and hashes everything, which is exactly what makes the
reuse safe on the next run.

### Scaling

Measured on generated solutions, each with a one-method edit, so these are the numbers
a real repository would see rather than the sample's:

| Solution | In-scope methods | `snapshot` | `analyze` cold | `analyze` warm |
|---|---|---|---|---|
| 40 projects | 5,760 | 0.5 s | 0.5 s | 0.2 s |
| 80 projects | 32,000 | 1.5 s | 2.0 s | 0.34 s |
| 160 projects, 3,200 tests | 64,000 | 2.9 s | 3.7 s | 0.58 s |

Cost is **linear** in the number of in-scope methods — including with interfaces and
virtual overrides throughout, which is what exercises the polymorphism edges. Nothing
quadratic showed up at this size.

Cold is now dominated by building the call graph, since the graph cache is keyed on the
whole MVID set and therefore misses on every new build — which in CI is every run. Warm
shows what per-assembly graph caching would recover: **0.58 s against 3.7 s** at 64,000
methods.

Also worth knowing for CI: the manifest is ~8.6 MB at 64,000 methods and gets published
and fetched per commit, so it is worth compressing at that scale.

Why this is safe rather than merely cheap: the caller of one of *your* changed methods
is your code. Framework code reaches back into yours only through interfaces, virtuals,
delegates and reflection — and those are covered by type-node edges (anything that
constructs, stores, or generically references the type), not by walking framework
method bodies. And when an out-of-scope assembly *does* change, testtrace cannot trace
it, so it fails open to `RUN_EVERYTHING` rather than guessing — the honest answer for a
package bump, which can affect anything.

Override the default when the heuristic doesn't fit — for example if your packages ship
PDBs, or you want to analyze a vendored dependency:

```bash
testtrace snapshot --input $BIN -o baseline.json --include-assemblies 'MyCompany.*'
testtrace analyze  --manifest baseline.json --current $BIN --test-framework nunit --include-assemblies 'MyCompany.*'
```

Remaining scaling considerations for very large solutions: the graph cache is keyed on
the current build's MVID set and scope, so it misses on every new build, and
per-assembly caching is the outstanding win (see the table above — it is the whole gap
between cold and warm). `ContentHasher` still reads whole assemblies into memory under
unbounded parallelism. `AddPolymorphismEdges` and the fixture-selection walk were the
suspected quadratic shapes; measured up to 64,000 methods and 160 fixtures, with
interfaces and virtual overrides throughout, neither showed super-linear growth.

## Front-end fallback chain

1. **Manifest diff** (preferred): content-hash assembly diff → method-level IL hash
   diff. Sees everything the compiler emitted, including generated code. Non-assembly
   build outputs (`.json`, `.config` — see below) are hashed too, so a config-only
   edit fails open instead of reading as "nothing changed".
2. **PDB line map** (no baseline needed): intersects `--changed-files` line ranges
   with portable-PDB sequence points of the current build. Degraded — stated in the
   output, never silently preferred: source-generator output, package bumps and
   analyzer changes are invisible; moved code is mis-attributed; a comment edited
   inside a method body over-selects that method's tests. Requires
   `DebugType=portable` (the default). A change landing in an assembly outside the
   analysis scope fails open here too — it reads every PDB it finds, but can only
   trace what is in the graph.
3. **RUN_EVERYTHING**: no PDBs, unanalysable change, or any error. Exit 0, warning
   on stderr.

A corrupt or missing manifest falls through to the PDB path (with a stderr warning),
not straight to `RUN_EVERYTHING`.

## Test frameworks

**A run targets one framework.** `--test-framework` is **required** and picks it —
there is no inference, because the runners' filter languages are mutually
unintelligible and a wrong guess would emit a filter that runs the wrong tests rather
than failing. That one choice drives both halves of the work: only that framework's
detector discovers tests, and only its dialect is emitted. There is no cross-framework
result to filter back out afterwards.

A build containing more than one framework still works — analyse it once per
framework. testtrace reads each in-scope assembly's references to see which frameworks
are present, so it can tell you two things without running every detector over every
method: that the framework you asked for is **missing** (an error, rather than a
puzzling empty selection), and that **others are present** (a note naming the re-run
that would cover them).

Everything below is inferred from the compiled test assembly; nothing is configured.

| | NUnit | xUnit | TUnit | MSTest |
|---|---|---|---|---|
| Runner | VSTest | VSTest | Microsoft.Testing.Platform | either (see below) |
| Filter flag | `--filter` / `--TestCaseFilter` | same | `--treenode-filter` | same as VSTest |
| Test | `[Test]`, `[TestCase]`, `[TestCaseSource]`, `[Theory]` | `[Fact]`, `[Theory]` | `[Test]` | `[TestMethod]`, `[DataTestMethod]` |
| Wildcarded in filters | `[TestCase]`, `[TestCaseSource]`, `[Theory]` | `[Theory]` | `[Arguments]`, data sources | **never** — see below |
| Fixture-scoped lifecycle | `[SetUp]`, `[TearDown]`, `[OneTimeSetUp]`/`[OneTimeTearDown]` in a `[TestFixture]` | the **constructor**, `Dispose`, `IAsyncLifetime` | `[Before/After(Test)]`, `[Before/After(Class)]` | `[TestInitialize]`, `[TestCleanup]`, `[ClassInitialize]`, `[ClassCleanup]` |
| Wider-scoped lifecycle | the one-time pair in a `[SetUpFixture]` (assembly) | — | `[Before/After(Assembly)]`; `TestSession`/`TestDiscovery` and `[BeforeEvery]`/`[AfterEvery]` are global | `[AssemblyInitialize]`, `[AssemblyCleanup]`; `[GlobalTestInitialize]`/`[GlobalTestCleanup]` are global |
| Data source | `[TestCaseSource]` | `[MemberData]`, `[ClassData]` | `[MethodDataSource]`, `[ClassDataSource<T>]` | `[DynamicData]` |
| Runner-injected state | — | `IClassFixture<T>`, `ICollectionFixture<T>` | — | — |

### Lifecycle scope

A lifecycle method runs around every test in *its* scope, so a change inside one
impacts all of them. testtrace models three widths:

- **Fixture** — the declaring type and anything deriving from it. `[SetUp]`,
  `[TestInitialize]`, an xUnit constructor, `[Before(Class)]`.
- **Assembly** — every test in the same assembly. `[AssemblyInitialize]`,
  `[Before(Assembly)]`, and NUnit's one-time pair inside a `[SetUpFixture]`.
- **Global** — every test in the run. `[GlobalTestInitialize]`, TUnit's `TestSession`
  and `TestDiscovery` hooks, and `[BeforeEvery]`/`[AfterEvery]`.

Treating the wider two as fixture-scoped would narrow them to whichever class happens
to declare the hook, which is a silent under-selection — so the scope is read from the
attribute, not assumed.

NUnit's `[SetUpFixture]` is really *namespace*-scoped. It is widened to the assembly
rather than matched by namespace: an over-approximation, which is the safe direction,
and much simpler.

Three xUnit specifics are worth knowing, because they are where a naive port would
silently lose tests:

- **The constructor is the setup.** xUnit builds a fresh instance of the test class
  for every test, so a change in the constructor impacts every test in that class —
  the same treatment NUnit's `[SetUp]` gets.
- **`IClassFixture<T>` gets an explicit edge.** The runner constructs `T` reflectively
  and injects it, so no call site exists and a change confined to the fixture's
  constructor would otherwise reach nothing. Fixture members that tests actually call
  are already covered by ordinary call edges; this closes the constructor case.
- **Attributes deriving from `[Fact]` count.** A project-specific `[IntegrationFact]`
  is idiomatic, and exact-name matching would classify those as "not a test" — which
  reads downstream as "not affected".

Only `[Theory]` needs contains-matching: verified against `dotnet vstest --ListTests`,
a theory's `FullyQualifiedName` carries its arguments
(`...CalculateTotal_ScalesWithQuantity(quantity: 1, expected: 5)`) while a `[Fact]`'s
does not. Contains-matching on `Class.Method` also keeps the argument list — and its
commas — out of the emitted filter, which is what avoids the `dotnet test` `MSB1006`
problem described above.

### MSTest

MSTest is **dual-runner**: with `EnableMSTestRunner` it builds a
Microsoft.Testing.Platform executable like TUnit, otherwise it runs under VSTest. That
distinction does not reach testtrace, because its MTP mode takes `--filter` in *vstest
syntax* and rejects `--treenode-filter`, and an MTP-mode assembly is still drivable by
`dotnet vstest` through the bundled VSTestBridge. One expression language either way.

Its one genuine difference is the opposite of xUnit's and TUnit's: **data-driven tests
are never wildcarded.** MSTest keeps `[DataRow]` and `[DynamicData]` arguments out of
`FullyQualifiedName` and puts them only in the display name, so

```
FullyQualifiedName=SampleApp.MSTest.Tests.OrderTotalTests.CalculateTotal_ScalesWithQuantity
```

exact-matches and runs **all** of its rows — verified against a real run. Emitting `~`
would also work but would drag in every test whose name merely starts with the same
text, so exact is both correct and tighter.

Note also that `[ClassInitialize]` and `[AssemblyInitialize]` are **static**, unlike
xUnit's instance-only lifecycle. They are detected as lifecycle regardless.

### TUnit and tree-path filters

TUnit runs on **Microsoft.Testing.Platform**, not VSTest. There is no test host and no
adapter: the test project builds as an executable that *is* the runner. Consequently
`FullyQualifiedName=` means nothing to it, and `.runsettings` `<TestCaseFilter>` is
ignored entirely — `--format runsettings` refuses a TUnit selection (exit 2) rather
than writing a file that would silently run everything.

Emitted filters look like:

```
/SampleApp.TUnit.Tests/*/(DeliveryTests)/(SmallOrder_TakesTwoDays|DeliveryDays_DependOnLineCount*)
```

Segment by segment: assembly, namespace (wildcarded), class alternation, test
alternation. Parameterized tests take a trailing `*` because their node names carry the
argument list, exactly as xUnit theories do.

Three properties of the syntax, each verified against a real run rather than inferred,
and each one a trap:

- **Alternation is per segment and must be parenthesised.** A bare `|` between two full
  paths is *not* an or — `/a/b/c|/d/e/f` matched **every test in the assembly**. A
  filter built that way looks like it works while running the whole suite.
- **Only one `--treenode-filter` argument is accepted**, so the entire selection has to
  collapse into a single expression. That is why `--format filter`, which concatenates
  assemblies, refuses a multi-assembly tree-path selection (exit 2) and points you at
  `--format filter-lines` or `--assembly`.
- **Zero matches exits 8**, so the reasoning behind exit 3 carries over unchanged: a
  selection of nothing must be signalled by the exit code, never by an unmatchable
  filter.

Segment alternation is a cross product, so two classes in one assembly sharing a test
method name will both run. That is over-selection — the safe direction, and the same
trade the VSTest contains-match already makes.

## Known limits

- **Reflection is invisible** — the fundamental limit of the static approach. Calls
  through `MethodInfo.Invoke`, `Activator`, dynamic proxies etc. produce no edge, so
  tests reaching changed code only via reflection are never selected. The
  `reflection-call` scenario asserts this miss so it stays visible. Mitigations:
  `--force-full-run-paths` for config-like files, `--always-run` for known
  reflection-heavy suites.
- **Only NUnit, xUnit, TUnit and MSTest are detected.** Against any other framework no
  test methods are found at all, which used to look like "nothing affected" but isn't;
  that case now fails open to `RUN_EVERYTHING`, and says which framework the build
  actually references. `ITestFrameworkDetector` is the seam for adding more.
- The manifest and current build should come from the same SDK toolchain — a
  compiler upgrade changes IL wholesale and degenerates (safely) into selecting
  nearly everything.

## Repository layout

- `src/TestTrace.Core` — analysis engine (single third-party dependency: Mono.Cecil)
- `src/TestTrace.Cli` — `testtrace` binary (System.CommandLine + TestPlatform
  ObjectModel for `FilterHelper.Escape`)
- `tests/TestTrace.Core.Tests` — engine and filter-emission unit tests
- `tests/TestTrace.Scenarios.Tests` — end-to-end scenarios and determinism checks
- `sample/` — fixture solution the scenario tests mutate and build. Deliberately mixed:
  three NUnit test projects plus one each of xUnit, TUnit and MSTest, so the suite
  covers every detector and `MixedSolution_EachFrameworkSeesItsOwnTests` proves each
  one sees only its own tests

Everything is C#; there are no shell or Python scripts. `dotnet test` runs the lot.

## Building and testing

```bash
dotnet build                       # both TFMs
dotnet test                        # unit tests + end-to-end scenarios
dotnet test tests/TestTrace.Core.Tests        # engine and CLI units, ~4s
dotnet test tests/TestTrace.Scenarios.Tests   # end-to-end, ~3min (drives real builds)
dotnet pack src/TestTrace.Cli -c Release      # -> artifacts/nupkg/TestTrace.<version>.nupkg
```

The scenario suite builds the sample solution repeatedly under `artifacts/`, so it is
much slower than the unit tests — expect around three minutes. Every scenario rebuilds
all eleven sample projects with `--no-incremental`, so the cost scales with the sample
rather than with the number of scenarios.

## Scenario tests

`tests/TestTrace.Scenarios.Tests` is the end-to-end verification: it copies `sample/`
into `artifacts/scenario-work`, builds it, snapshots a baseline, applies a source
edit, rebuilds, and checks the selected tests. Each scenario is declared as data in
`Scenario.cs` — the edit (an exact find/replace, which fails loudly if the anchor is
missing or ambiguous) plus its expectations — so there are no patch files to
regenerate and no generator to maintain.

Expectations are must-include / must-not-include lists rather than exact equality:
over-approximation is allowed by design, and the must-not-include list is what stops
the tool degenerating into "always run everything". Every scenario runs twice, once
per front-end.

The edits also produce a unified diff, which is fed to `--changed-files` unchanged —
so the tests exercise the same diff-parsing path a real caller uses.

Two flags keep degradations honest, both asserted rather than assumed:

- `KnownMiss` — semantically affected tests the static approach cannot reach (the
  reflection blind spot). Asserted **absent**; if one starts being selected, the test
  fails and tells you to promote it.
- `PdbDeviates` — the scenario cannot meet its expectations under the PDB front-end
  (a comment inside a method body over-selects; an attribute change carries no
  sequence points and is missed). Asserted to **still deviate**, so a fix cannot pass
  unnoticed.

Baseline and current are built in the same working directory on purpose:
deterministic MVIDs incorporate source paths, so building the two states in different
directories would make every assembly look changed. Builds are `--no-incremental`
because MSBuild's mtime check can tie against just-rewritten files and leak one
scenario's outputs into the next.

`DeterminismTests` covers the other two invariants: two clean builds produce identical
assemblies, and a build analyzed against its own manifest selects nothing (rather than
falling open to `RUN_EVERYTHING`).

## Implementation notes

- Change detection compares a **content hash** — SHA-256 of the PE with the COFF
  timestamp, checksum, debug directory and MVID zeroed — not the raw MVID:
  deterministic MVIDs embed the PDB identity, which hashes source checksums and
  sequence points, so any source edit (comments included) changes the MVID.
- Method hashes are canonical text (opcodes, resolved-name operands, literals,
  signature, structural attributes, EH table), never raw IL bytes — tokens are table
  indices that shift when anything is added. Ordinals inside generated names
  (`<M>d__4`, `<>c__DisplayClass4_0`, `b__3_0`) shift the same way and are
  normalized. Generated members fold onto their declaring method.
- The call graph adds over-approximation edges for DI: interface→implementation,
  base→override, and **generic type-argument edges** (`AddSingleton<I, Impl>` is the
  only static mention of `Impl`; `WebApplicationFactory<Program>` the only one of
  `Program`). MVC controllers, dispatched by reflection, get an edge to their
  assembly's entry point. `typeof(T)` deliberately adds no edge.
- The graph is cached on disk keyed by the exact set of input MVIDs **and the analysis
  scope** (`$TMPDIR/testtrace-graph-cache`). The scope belongs in the key because the
  graph is built from in-scope assemblies only while the MVID set is identical for
  every scope over the same build — without it, a narrowly-scoped run serves its graph
  to a later wider one and silently drops every test outside the narrower scope.
