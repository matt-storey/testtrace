# testtrace

IL-level test impact analysis for .NET. Given two builds of the same solution,
`testtrace` works out which tests could possibly be affected by the difference and
emits a `dotnet vstest` filter.

Status: M0 — sample solution and scenario harness. The tool itself is not implemented yet.

## Layout

- `src/` — `TestTrace.Core` (analysis engine) and `TestTrace.Cli` (not yet created)
- `tests/` — engine unit tests (not yet created)
- `sample/` — the fixture solution the scenario harness mutates and builds
- `scripts/run-scenarios.sh` — end-to-end scenario harness

## Running the scenarios

```bash
scripts/run-scenarios.sh                 # all scenarios, net8.0
scripts/run-scenarios.sh --tfm net10.0   # all scenarios, net10.0
scripts/run-scenarios.sh lambda-change   # one scenario
```

Until the tool exists every scenario reports `FAIL (tool not implemented)` — that is
the expected M0 state.
