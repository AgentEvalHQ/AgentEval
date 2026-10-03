# AgentEval.ReadmeSnippets

Compile-only home of every C# example in the repository [README](../../README.md). Nothing here runs or calls a model.

Each README C# block has a region here:

```text
// begin-snippet: red-team
...
// end-snippet
```

and the README puts `<!-- snippet: red-team -->` on the line before the block. Two checks keep them honest:

- The project is in `AgentEval.sln`, so an example that names a type or member that does not exist breaks the build.
- `tests/AgentEval.Tests/Docs/ReadmeSnippetsTests.cs` fails when a README C# block has no marker, when a block and its region differ (line endings, trailing spaces and the shared indentation are ignored), when a region is not shown in the README, or when this project leaves the solution.

To change a README example, edit the region here first, build, then copy the region into the README block.
