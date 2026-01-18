# Security Scanning & Hardening Guide

> **Last Updated:** January 18, 2026  
> **Version:** 0.2.0-beta  
> **Status:** Production Security Hardening Complete

---

## Overview

AgentEval implements comprehensive automated security scanning to protect against vulnerabilities, supply chain attacks, and ensure enterprise-grade security for AI agent evaluation workflows.

---

## Security Scanning Tech Stack

| Tool | Purpose | Coverage | Status |
|------|---------|----------|--------|
| **Microsoft DevSkim** | Static analysis for security anti-patterns | Source code, config files | ✅ Active |
| **NuGet Vulnerability Scanner** | Dependency vulnerability detection | All transitive packages | ✅ Active |
| **Secret Scanner** | Hardcoded credential detection | All file types | ✅ Active |
| **SARIF Integration** | GitHub Security tab integration | Unified reporting | ✅ Active |
| **GitHub CodeQL** | Advanced semantic code analysis | C# source code | 🔲 Planned |
| **Codecov** | Code coverage tracking & reporting | Test coverage | 🔲 Setup Required |

---

## Tool Deep-Dives

### Microsoft DevSkim

**What is DevSkim?**

[DevSkim](https://github.com/microsoft/DevSkim) is Microsoft's open-source security linter that provides IDE plugins, CLI tools, and GitHub Actions to detect security vulnerabilities during development. It's like ESLint/StyleCop but specifically for security issues.

**What It Detects:**
- 🔒 SQL injection vulnerabilities
- 🔒 Cross-site scripting (XSS) patterns
- 🔒 Command injection risks
- 🔒 Path traversal vulnerabilities
- 🔒 Hardcoded credentials and secrets
- 🔒 Weak cryptographic algorithms (MD5, SHA1, DES)
- 🔒 Insecure random number generation
- 🔒 XML external entity (XXE) injection
- 🔒 Insecure deserialization patterns
- 🔒 SSL/TLS misconfigurations

**How We Use It:**

AgentEval uses the [DevSkim GitHub Action](https://github.com/microsoft/DevSkim-Action) to scan all source code on every push and PR:

```yaml
- name: Run Security DevSkim Analyzer
  uses: microsoft/DevSkim-Action@v1
  with:
    directory-to-scan: .
    output-filename: devskim-results.sarif
```

**Results Visibility:**

DevSkim findings appear in:
1. **GitHub Security Tab** → Repository → Security → Code scanning alerts
2. **Pull Request Checks** → Annotations on affected lines
3. **Artifacts** → `devskim-results.sarif` downloadable from workflow runs

**Local DevSkim Usage:**

```bash
# Install DevSkim CLI globally
dotnet tool install --global Microsoft.CST.DevSkim.CLI

# Run locally
devskim analyze --source-code . --output-file results.sarif

# Or with Docker
docker run -v $(pwd):/src mcr.microsoft.com/devskim analyze --source-code /src
```

---

### Codecov (Code Coverage)

**What is Codecov?**

[Codecov](https://codecov.io) is a code coverage reporting service that integrates with GitHub to show which lines of code are covered by tests. It provides PR comments, coverage badges, and trend analysis.

**Current Status:** 🔲 **Setup Required**

Codecov requires a `CODECOV_TOKEN` to authenticate uploads. This token is not yet configured for the AgentEval repository.

#### How to Set Up Codecov

**Step 1: Sign Up for Codecov**

1. Go to [codecov.io/signup](https://app.codecov.io/signup)
2. Sign in with your GitHub account
3. Install the [Codecov GitHub App](https://github.com/apps/codecov) for your organization/account
4. Grant access to the `joslat/AgentEval` repository

**Step 2: Get Your Repository Token**

1. Go to [app.codecov.io](https://app.codecov.io)
2. Select the `joslat/AgentEval` repository
3. Click "Setup repo" or navigate to Settings → General
4. Copy the **Repository Upload Token**

**Step 3: Add Token to GitHub Secrets**

1. Go to GitHub → `joslat/AgentEval` → Settings → Secrets and variables → Actions
2. Click "New repository secret"
3. Name: `CODECOV_TOKEN`
4. Value: (paste the token from Step 2)
5. Click "Add secret"

**Step 4: Add Codecov to CI Workflow**

Once the token is configured, add to `.github/workflows/ci.yml`:

```yaml
- name: Run tests with coverage
  run: dotnet test --collect:"XPlat Code Coverage" --results-directory ./coverage

- name: Upload coverage to Codecov
  uses: codecov/codecov-action@v4
  with:
    token: ${{ secrets.CODECOV_TOKEN }}
    files: ./coverage/**/coverage.cobertura.xml
    fail_ci_if_error: false
    verbose: true
```

**Step 5: Add Coverage Badge to README**

Once configured, add this badge to README.md:

```markdown
[![codecov](https://codecov.io/gh/joslat/AgentEval/graph/badge.svg?token=YOUR_BADGE_TOKEN)](https://codecov.io/gh/joslat/AgentEval)
```

**Coverage Reports Will Show:**
- Overall project coverage percentage
- Per-file coverage breakdown  
- Coverage changes on each PR
- Historical coverage trends
- Uncovered lines highlighted in PR diffs

---

## How Security Scanning Works

### 1. Automated CI/CD Integration

Security scanning runs automatically on:
- ✅ Every push to `main` and `develop` branches
- ✅ Every pull request to `main`
- ✅ Weekly scheduled scans (Monday 00:00 UTC)

### 2. Scan Pipeline Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                    SECURITY SCAN PIPELINE                            │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  1. CHECKOUT          Clone repository with full history             │
│         ↓                                                            │
│  2. RESTORE           Restore NuGet packages                        │
│         ↓                                                            │
│  3. DEVSKIM           Static analysis for security patterns          │
│         ↓             - SQL injection detection                      │
│         │             - XSS vulnerability patterns                   │
│         │             - Hardcoded secrets patterns                   │
│         │             - Crypto weakness detection                    │
│         ↓                                                            │
│  4. VULN CHECK        NuGet dependency vulnerability scan           │
│         ↓             - CVE database check                           │
│         │             - Transitive dependency analysis               │
│         ↓                                                            │
│  5. SECRET SCAN       Detect hardcoded credentials                  │
│         ↓             - API key patterns (sk-*, etc.)               │
│         │             - Connection strings                           │
│         │             - Password patterns                            │
│         ↓                                                            │
│  6. SARIF UPLOAD      Upload results to GitHub Security tab          │
│         ↓                                                            │
│  7. ARTIFACT SAVE     Store scan reports for audit                  │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### 3. DevSkim Static Analysis

DevSkim is integrated via the official [DevSkim GitHub Action](https://github.com/microsoft/DevSkim-Action).

**Configuration:**
```yaml
- name: Run Security DevSkim Analyzer
  uses: microsoft/DevSkim-Action@v1
  with:
    directory-to-scan: .
    output-filename: devskim-results.sarif
```

**Where to View Results:**
- **GitHub Security Tab:** Repository → Security → Code scanning alerts
- **PR Annotations:** Inline comments on affected code
- **Workflow Artifacts:** Download `devskim-results.sarif`

### 4. Dependency Vulnerability Scanning

**Process:**
```bash
dotnet list package --vulnerable --include-transitive
```

**Checks Against:**
- NuGet Advisory Database
- GitHub Advisory Database
- National Vulnerability Database (NVD)

**Exit Conditions:**
- ✅ Pass: "No vulnerable packages found"
- ❌ Fail: Any package with known CVE

### 5. Secret Detection

**Patterns Detected:**
| Pattern | Example | Action |
|---------|---------|--------|
| OpenAI API Keys | `sk-...` | Block CI |
| Azure Keys | `DefaultEndpointsProtocol=...` | Block CI |
| Connection Strings | `Server=...;Password=...` | Block CI |
| Generic Secrets | `api_key`, `secret`, `password` in code | Warn |

---

## Security Architecture

### Input Validation Layer

All user inputs are validated before processing:

```csharp
// File path validation with allowlist
private static readonly string[] AllowedExtensions = 
    { ".yaml", ".yml", ".json", ".jsonl", ".csv" };

private static void ValidateFilePath(string path)
{
    // Check for path traversal attacks
    if (path.Contains("..") || Path.IsPathRooted(path) && 
        !path.StartsWith(Environment.CurrentDirectory))
    {
        throw new SecurityException("Invalid file path detected");
    }
    
    // Validate file extension
    var extension = Path.GetExtension(path).ToLowerInvariant();
    if (!AllowedExtensions.Contains(extension))
    {
        throw new SecurityException($"File type not allowed: {extension}");
    }
}
```

### Secure JSON Parsing

```csharp
// Use System.Text.Json with secure defaults
private static readonly JsonSerializerOptions SecureJsonOptions = new()
{
    MaxDepth = 64,                    // Prevent stack overflow
    AllowTrailingCommas = true,       // Flexibility
    ReadCommentHandling = JsonCommentHandling.Skip,
    PropertyNameCaseInsensitive = true
};
```

### PII Detection & Redaction

```csharp
// Behavioral policy assertions detect sensitive data
result.ToolUsage!.Should()
    .NeverPassArgumentMatching(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b",
        because: "email addresses are PII",
        new RegexMatchingOptions { RedactMatches = true });
```

---

## Security Levels

### Level 1: Basic (Default)
- ✅ DevSkim static analysis
- ✅ Dependency vulnerability scanning
- ✅ Secret pattern detection

### Level 2: Enhanced (Recommended for Enterprise)
- ✅ All Level 1 checks
- ✅ Weekly scheduled scans
- ✅ GitHub Security tab integration
- ✅ Artifact retention for audit

### Level 3: Maximum (SOC2/HIPAA Compliance)
- ✅ All Level 2 checks
- 🔲 CodeQL semantic analysis (planned)
- 🔲 SAST/DAST integration (planned)
- 🔲 Software composition analysis (SCA) (planned)
- 🔲 Container scanning (planned)

---

## How Secure Is It?

### Current Protection Matrix

| Threat | Protection | Status |
|--------|------------|--------|
| **Known CVEs** | Dependency scanning | ✅ Active |
| **Code Injection** | DevSkim patterns | ✅ Active |
| **Hardcoded Secrets** | Secret scanning | ✅ Active |
| **Path Traversal** | Input validation | ✅ Active |
| **Supply Chain** | Package verification | ✅ Active |
| **XSS/CSRF** | Not applicable (library) | N/A |
| **SQL Injection** | DevSkim detection | ✅ Active |

### Security Certifications (Planned)

| Certification | Status | Target Date |
|---------------|--------|-------------|
| OWASP Top 10 Compliance | ✅ Self-assessed | Complete |
| CWE/SANS Top 25 | 🔲 Pending | Q2 2026 |
| SOC2 Type II | 🔲 Planned | Q4 2026 |

---

## Running Security Scans Locally

### Quick Scan
```bash
# Check for vulnerable packages
dotnet list package --vulnerable --include-transitive

# Run DevSkim locally (requires DevSkim CLI)
devskim analyze --source-code . --output-file results.sarif
```

### Full Scan (Docker)
```bash
# Pull DevSkim container
docker pull mcr.microsoft.com/devskim

# Run full analysis
docker run -v $(pwd):/src mcr.microsoft.com/devskim analyze --source-code /src
```

---

## Responding to Security Issues

### Vulnerability Found in Dependency

1. Check if the vulnerability affects AgentEval's usage pattern
2. If affected, create a security issue (private)
3. Update the dependency to patched version
4. Release patch version within 48 hours

### Security Bug in AgentEval Code

1. Report via [SECURITY.md](https://github.com/joslat/AgentEval/blob/main/SECURITY.md) process
2. Acknowledge within 24 hours
3. Patch and release within 7 days (critical) or 30 days (moderate)

---

## Configuration Reference

### CI Security Workflow (`.github/workflows/security.yml`)

```yaml
name: Security Scan

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]
  schedule:
    - cron: '0 0 * * 1'  # Weekly on Mondays

jobs:
  security:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
      - run: dotnet restore
      
      # DevSkim static analysis
      - uses: microsoft/DevSkim-Action@v1
        with:
          directory-to-scan: .
          output-filename: devskim-results.sarif
          
      # Upload to GitHub Security tab
      - uses: github/codeql-action/upload-sarif@v3
        with:
          sarif_file: devskim-results.sarif
          
      # Dependency vulnerability check
      - run: dotnet list package --vulnerable --include-transitive
```

---

## Best Practices for Contributors

### Before Committing

1. ✅ Run `dotnet list package --vulnerable` locally
2. ✅ Never commit API keys, tokens, or passwords
3. ✅ Use environment variables for secrets
4. ✅ Validate all user inputs
5. ✅ Use parameterized queries if adding database support

### Code Review Checklist

- [ ] No hardcoded credentials
- [ ] Input validation present
- [ ] No path traversal vulnerabilities
- [ ] Secure JSON parsing used
- [ ] PII properly handled/redacted

---

## Related Documentation

- [SECURITY.md](https://github.com/joslat/AgentEval/blob/main/SECURITY.md) - Vulnerability reporting process
- [CONTRIBUTING.md](https://github.com/joslat/AgentEval/blob/main/CONTRIBUTING.md) - Contribution guidelines
