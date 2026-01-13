# Security Policy

## Supported Versions

AgentEval is currently in alpha. Security updates are provided for the following versions:

| Version | Supported          |
| ------- | ------------------ |
| 1.0.0-alpha   | :white_check_mark: |

Once AgentEval reaches stable release (1.0.0), we will support:
- Current major version (e.g., 1.x)
- Previous major version (e.g., 0.x) for 6 months after a new major release

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

If you discover a security vulnerability in AgentEval, please report it privately:

### Email

Send details to: **joslat@gmail.com**

Include:
- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

### What to Expect

1. **Acknowledgment**: We'll acknowledge your report within **48 hours**
2. **Investigation**: We'll investigate and validate the issue within **7 days**
3. **Fix**: We'll develop a fix and release a security patch as soon as possible
4. **Disclosure**: We'll coordinate disclosure with you after the fix is released

### Security Update Process

1. **Private Fix**: Develop fix in private repository
2. **CVE Assignment**: Request CVE if applicable
3. **Security Advisory**: Publish GitHub Security Advisory
4. **Patch Release**: Release patched version
5. **Public Disclosure**: Announce fix in release notes

### Responsible Disclosure

We follow a coordinated disclosure model:

- **90 days**: Standard disclosure timeline after initial report
- **Extended timeline**: Available for complex vulnerabilities requiring vendor coordination
- **Early disclosure**: With reporter's agreement if exploit is public

## Security Best Practices for Users

### API Keys and Credentials

AgentEval integrates with AI services (Azure OpenAI, OpenAI, etc.). **Never** commit API keys:

```csharp
// ❌ DON'T: Hard-code credentials
var client = new AzureOpenAIClient(
    new Uri("https://my-endpoint.openai.azure.com"),
    new AzureKeyCredential("sk-XXXX...")); // DON'T DO THIS!

// ✅ DO: Use environment variables or configuration
var endpoint = new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!);
var credential = new AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!);
var client = new AzureOpenAIClient(endpoint, credential);
```

### Snapshot Testing

When using snapshot testing, ensure snapshots don't contain sensitive data:

```csharp
var options = new SnapshotOptions
{
    // Scrub sensitive data
    ScrubPatterns = new List<(Regex Pattern, string Replacement)>
    {
        (new Regex(@"sk-[a-zA-Z0-9]+"), "[API_KEY]"),
        (new Regex(@"Bearer [a-zA-Z0-9_\-\.]+"), "[TOKEN]"),
        (new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}"), "[EMAIL]")
    }
};
```

### CI/CD Integration

Use secrets management in CI/CD:

```yaml
# GitHub Actions
- name: Run Tests
  env:
    AZURE_OPENAI_API_KEY: ${{ secrets.AZURE_OPENAI_API_KEY }}
    AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}
  run: dotnet test
```

### Test Data

- Don't use production data in tests
- Don't commit test data with PII
- Use synthetic/anonymized data

## Known Issues

No known security vulnerabilities at this time.

Check [Security Advisories](https://github.com/joslat/AgentEval/security/advisories) for updates.

## Security Features

AgentEval includes:

- **No network calls** - AgentEval itself makes no network calls; it only observes your agent's calls
- **No data collection** - No telemetry or data collection
- **Transparent** - Open source under MIT license
- **Minimal dependencies** - Only trusted Microsoft libraries

## Dependencies

AgentEval depends on:

- `Microsoft.Extensions.AI.Abstractions` - Microsoft's AI abstractions
- `System.Text.Json` - .NET JSON library

Both are maintained by Microsoft with regular security updates.

## Questions?

For security questions that aren't sensitive, open a [GitHub Discussion](https://github.com/joslat/AgentEval/discussions).

For private security matters, email: **joslat@gmail.com**

---

*Last updated: January 7, 2026*
