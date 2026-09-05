# Release build warning baseline

Captured on 2026-09-04 after the warning cleanup for issue #138.

## Measurement

The baseline is produced with the repository's GeneXus SDK path configured:

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build Genexus18MCP.sln -c Release -t:Rebuild -v:minimal
```

The build completed successfully (`exit code 0`). It emitted 444 compiler/analyzer
warning lines. The solution graph repeats some diagnostics, so the actionable
baseline is 216 distinct `(code, file, line)` locations:

| Project | CS8600 | CS8602 | CS8603 | CS8604 | CS8605 | CS8618 | CS8620 | CS8625 | Total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `GxMcp.Gateway` | 60 | 4 | 9 | 15 | 0 | 5 | 1 | 0 | 94 |
| `GxMcp.Gateway.Tests` | 48 | 15 | 3 | 22 | 6 | 0 | 21 | 7 | 122 |
| **Total** | **108** | **19** | **12** | **37** | **6** | **5** | **22** | **7** | **216** |

`MSB3277` is not emitted. The two xUnit1012 diagnostics reported by the issue
are not emitted, and the benchmark initialization warnings in
`SearchRankParallelismBenchmark.cs` are not emitted.

## Policy

- A Release rebuild must exit successfully.
- `MSB3277` must remain at zero; its suppression is scoped to
  `GxMcp.Worker.Tests` and does not hide compiler/analyzer warnings.
- Future warning work should compare distinct `(code, file, line)` locations
  against this baseline and must not increase the total without an explicit
  update to this document.
