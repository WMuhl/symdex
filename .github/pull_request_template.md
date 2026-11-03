## Summary

<!-- Why this change is needed. One or two sentences is enough. -->

## Tests run

<!-- Command and result. `dotnet test symdex.sln` is the usual one. Say if you could not run it. -->

## Checklist

- [ ] Behavior changes are covered by a test, or this change has no runtime behavior (docs only).
- [ ] No new directory named `native` under `src/Symdex.TreeSitter`. Native build output stays in `native-build/`.
- [ ] If a `.scm` query changed, its pattern order still matches the corresponding `PatternKinds` array in `GrammarRegistry.cs`.
- [ ] If the build failed with a stale MSBuild or VBCSCompiler error, `dotnet build-server shutdown` was run before the successful build.
