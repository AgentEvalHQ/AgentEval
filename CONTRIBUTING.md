# Contributing to AgentEval

Thank you for your interest in contributing to AgentEval! 🎉

AgentEval is **the .NET evaluation toolkit for AI agents**—evaluation, testing, and benchmarking for agentic AI, built first for Microsoft Agent Framework (MAF). Community contributions are essential to its success.

## Ways to Contribute

- 🐛 **Report Bugs** - Found an issue? [Open a bug report](https://github.com/joslat/AgentEval/issues/new?template=bug_report.md)
- 💡 **Request Features** - Have an idea? [Submit a feature request](https://github.com/joslat/AgentEval/issues/new?template=feature_request.md)
- 📖 **Improve Documentation** - Fix typos, clarify explanations, add examples
- 🔧 **Submit Code** - Bug fixes, new features, refactoring
- 🧪 **Write Tests** - Increase test coverage, add edge cases
- 💬 **Answer Questions** - Help others in [GitHub Discussions](https://github.com/joslat/AgentEval/discussions)

---

## Getting Started

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) or later
- [Git](https://git-scm.com/)
- (Optional) [Azure OpenAI](https://azure.microsoft.com/services/cognitive-services/openai-service/) access for running samples with real AI

### Fork and Clone

```bash
# Fork the repository on GitHub, then:
git clone https://github.com/YOUR_USERNAME/AgentEval.git
cd AgentEval
```

### Build the Project

```bash
dotnet build
```

### Run Tests

```bash
dotnet test
```

All 707 tests should pass across all three target frameworks (net8.0, net9.0, net10.0).

---

## Development Workflow

### 1. Create a Branch

```bash
git checkout -b feature/my-new-feature
# or
git checkout -b fix/issue-123
```

Use descriptive branch names:
- `feature/` - New functionality
- `fix/` - Bug fixes
- `docs/` - Documentation changes
- `refactor/` - Code refactoring
- `test/` - Test improvements

### 2. Make Your Changes

- Follow the existing code style
- Add XML documentation for public APIs
- Include unit tests for new functionality
- Update documentation if needed

### 3. Test Your Changes

```bash
# Run all tests
dotnet test

# Run tests with coverage (optional)
dotnet test --collect:"XPlat Code Coverage"
```

### 4. Commit Your Changes

We follow [Conventional Commits](https://www.conventionalcommits.org/):

```bash
git commit -m "feat: add new assertion for tool timing"
git commit -m "fix: handle null tool arguments correctly"
git commit -m "docs: update walkthrough with new API"
git commit -m "test: add edge cases for snapshot comparer"
```

Commit message format:
```
<type>(<scope>): <description>

[optional body]

[optional footer]
```

Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`

### 5. Push and Create a PR

```bash
git push origin feature/my-new-feature
```

Then [create a Pull Request](https://github.com/joslat/AgentEval/compare) on GitHub.

---

## Code Style Guidelines

### C# Conventions

- Use C# 12 features where appropriate
- Follow [Microsoft's C# Coding Conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use `file-scoped namespaces`
- Use `primary constructors` for simple types
- Prefer `required` properties over constructor parameters for models

### Naming

- **Classes/Methods**: PascalCase (`TestHarness`, `RunTestAsync`)
- **Parameters/Variables**: camelCase (`testCase`, `result`)
- **Private Fields**: _camelCase (`_logger`, `_evaluator`)
- **Constants**: PascalCase (`DefaultPassingScore`)

### XML Documentation

All public APIs must have XML documentation:

```csharp
/// <summary>
/// Runs a single test case against the agent.
/// </summary>
/// <param name="adapter">The agent adapter to test.</param>
/// <param name="testCase">The test case to run.</param>
/// <returns>The test result with score and details.</returns>
public async Task<TestResult> RunTestAsync(IAgentAdapter adapter, TestCase testCase)
```

### Test Naming

Use the pattern: `MethodName_StateUnderTest_ExpectedBehavior`

```csharp
[Fact]
public async Task RunTestAsync_WithNullAdapter_ThrowsArgumentNullException()

[Fact]
public async Task HaveCalledTool_WhenToolWasCalled_ShouldPass()
```

---

## Project Structure

```
AgentEval/
├── src/
│   └── AgentEval/           # Main library
│       ├── Adapters/        # Agent adapters (MAF, IChatClient)
│       ├── Assertions/      # Fluent assertion API
│       ├── Benchmarks/      # Benchmark runners
│       ├── Core/            # Core abstractions
│       ├── Exporters/       # Output formatters (JSON, JUnit, Markdown, CSV)
│       ├── MAF/             # Microsoft Agent Framework integration
│       ├── Metrics/         # Evaluation metrics
│       ├── Models/          # Data models
│       └── Snapshots/       # Snapshot testing
├── samples/
│   └── AgentEval.Samples/   # Sample code (10 samples)
├── tests/
│   └── AgentEval.Tests/     # Unit tests
└── docs/                    # DocFX documentation
```

---

## Pull Request Guidelines

### Before Submitting

- [ ] Tests pass locally (`dotnet test`)
- [ ] New code has unit tests
- [ ] XML documentation added for public APIs
- [ ] No compiler warnings
- [ ] PR description explains the change

### PR Review Process

1. Create a draft PR early for feedback
2. Request review when ready
3. Address feedback promptly
4. Squash commits if requested

### Merging

- PRs require at least one approval
- All CI checks must pass
- Use "Squash and merge" for clean history

---

## Reporting Issues

### Bug Reports

Include:
- AgentEval version
- .NET version
- Steps to reproduce
- Expected vs actual behavior
- Error messages/stack traces

### Feature Requests

Include:
- Problem you're trying to solve
- Proposed solution
- Alternative solutions considered
- Impact on existing functionality

---

## Community

- **GitHub Discussions**: Ask questions, share ideas
- **Issues**: Bug reports and feature requests

---

## License

By contributing to AgentEval, you agree that your contributions will be licensed under the [MIT License](LICENSE).

---

## Recognition

Contributors are recognized in:
- Release notes
- README acknowledgments section (for significant contributions)

Thank you for helping make AgentEval better! 🚀
