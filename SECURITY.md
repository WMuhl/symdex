# Security

## Reporting a vulnerability

Report vulnerabilities privately through [GitHub private vulnerability reporting](https://github.com/WMuhl/symdex/security/advisories/new). Please do not open a public issue for a security report.

## Local index, and what indexing runs

symdex writes its index to `.index/code.db` in the repository you point it at. The program has no telemetry and does not upload source or the index.

Indexing C# is different from parsing a file. When the repo root contains a `.sln`, `.slnx`, or `.csproj`, when a `.sln` or `.slnx` is found up to three levels below the root, or when you pass `--solution`, symdex opens that project with `MSBuildWorkspace`. Opening a project evaluates its MSBuild files, which can run tasks the project imports. Index an untrusted repository only if you would run its build.

There is no flag that skips the Roslyn feeder. Tree-sitter is used for those `.cs` files when the workspace fails to load or a project has compile errors, and that failure still follows an attempt to open the project whenever a project file was found. A repository with no `.sln`, `.slnx`, or `.csproj` at the root, no `.sln` or `.slnx` within three levels below it, and no `--solution` path, is not opened as an MSBuild project. TypeScript, JavaScript, and Python are parsed locally with tree-sitter either way.

Choosing the SDK runs `dotnet --version` with the target repository as the working directory, and may run `dotnet --list-sdks`. Those are local `dotnet` processes.
