# DocFX Instructions for AgentEval

> Instructions for working with DocFX documentation generation in AgentEval

## Project Setup

### File Structure
```
docs/
├── docfx.json          # Main configuration
├── toc.yml             # Navigation structure
├── index.md            # Landing page
├── api/                # Generated API YAML (git-ignored for *.yml)
├── _site/              # Generated HTML output (git-ignored)
├── images/             # Static images
├── templates/          # Custom templates (material theme)
├── adr/                # Architecture Decision Records
└── showcase/           # Code gallery and examples
```

### Key Configuration (docfx.json)
```json
{
  "metadata": [{
    "src": [{ "src": "../src", "files": ["**/*.csproj"] }],
    "dest": "api",
    "properties": { "TargetFramework": "net10.0" }
  }],
  "build": {
    "template": ["default", "modern", "templates/material"],
    "output": "_site"
  }
}
```

## Build Commands

### Full Build (Recommended)
```powershell
# From repository root
.\scripts\build-documentation.ps1
```

### Step-by-Step
```powershell
# 1. Build project (generates XML docs)
dotnet build src\AgentEval\AgentEval.csproj --configuration Release

# 2. Generate API metadata (YAML files)
cd docs
docfx metadata

# 3. Build HTML site
docfx build

# 4. Preview locally
docfx serve _site
# Opens at http://localhost:8080
```

### Quick Rebuild (Markdown only)
```powershell
# Skip metadata regeneration if only editing .md files
cd docs
docfx build
start _site\index.html
```

## Common Issues & Solutions

### Issue: API docs not generating
**Cause:** XML documentation disabled in csproj
**Fix:** Ensure `<GenerateDocumentationFile>true</GenerateDocumentationFile>` in csproj

### Issue: docfx metadata fails
**Cause:** Build errors in source code
**Fix:** Run `dotnet build` first and fix any compilation errors

### Issue: Changes not appearing
**Cause:** Browser cache or stale `_site` folder
**Fix:** 
```powershell
Remove-Item -Recurse -Force docs\_site
docfx build
```

### Issue: Search not working
**Cause:** Missing search index
**Fix:** Verify `"postProcessors": ["ExtractSearchIndex"]` in docfx.json

### Issue: Broken links in API docs
**Cause:** Missing XML comments on public members
**Fix:** Add `<summary>` XML docs to all public types and members

## Navigation (toc.yml)

The table of contents uses this structure:
```yaml
- name: Section Name
  items:
  - name: Page Name
    href: page-name.md
  - name: External Link
    href: https://example.com
```

**Rules:**
- File references are relative to docs/ folder
- External links must start with `http://` or `https://`
- Use `href: api/` for API reference section

## Adding New Documentation

### New Markdown Page
1. Create `docs/new-page.md`
2. Add to `docs/toc.yml` in appropriate section
3. Run `docfx build` to verify
4. Check links work in `_site/`

### New Code Example
Wrap in fenced code blocks with language:
```markdown
```csharp
// C# code here
```
```

### New ADR (Architecture Decision Record)
1. Create `docs/adr/NNN-title.md`
2. Add to `docs/adr/README.md` list
3. Follow ADR template format

## Template Customization

Custom material theme is in `docs/templates/material/`:
- `partials/` - Override default HTML partials
- `styles/` - Custom CSS
- `main.css` - Primary styling overrides

**Caution:** Template changes require full rebuild and cache clear.

## Validation Checklist

Before committing documentation changes:
- [ ] `docfx build` completes without warnings
- [ ] All internal links work (check in browser)
- [ ] Images display correctly
- [ ] Search finds new content
- [ ] Mobile view looks acceptable
- [ ] No broken API links

## GitHub Pages Deployment

Deployment is automatic via `.github/workflows/docs.yml`:
1. Push to `main` that changes `docs/**` or `src/AgentEval/**/*.xml` (the workflow also runs when a release is published, and can be run by hand)
2. Workflow builds documentation
3. Publishes to GitHub Pages
4. Available at `https://agenteval.dev/` (the custom domain set by `docs/CNAME`)

**Manual deployment is not needed** - just push to main.

## Performance Tips

- **Incremental builds:** Only run `docfx metadata` when C# code changes
- **Skip API during editing:** Comment out `metadata` section temporarily
- **Use serve for live reload:** `docfx serve _site --port 8080`

## Reference

- [DocFX Official Docs](https://dotnet.github.io/docfx/)
- [AgentEval building-docs.md](docs/building-docs.md)
- [GitHub Pages workflow](.github/workflows/docs.yml)
