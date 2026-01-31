/* ═══════════════════════════════════════════════════════════════════════════
   AgentEval Documentation - Custom JavaScript
   GitHub Repository Widget (like MkDocs Material)
   ═══════════════════════════════════════════════════════════════════════════ */

(function() {
    'use strict';

    // Configuration
    const REPO_OWNER = 'joslat';
    const REPO_NAME = 'AgentEval';
    const NUGET_PACKAGE = 'AgentEval';

    // Create and inject the GitHub widget
    function createGitHubWidget() {
        const navbar = document.querySelector('.navbar .container-xxl');
        if (!navbar) return;

        // Create widget container
        const widget = document.createElement('div');
        widget.className = 'github-widget';
        widget.innerHTML = `
            <a href="https://github.com/${REPO_OWNER}/${REPO_NAME}" 
               class="github-widget-link" 
               target="_blank" 
               rel="noopener noreferrer"
               title="View on GitHub">
                <svg class="github-icon" viewBox="0 0 16 16" width="20" height="20" fill="currentColor">
                    <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"/>
                </svg>
                <span class="github-widget-name">${REPO_NAME}</span>
                <span class="github-widget-version" id="github-version">v0.2.1</span>
                <span class="github-widget-stats">
                    <span class="github-stat" id="github-stars" title="Stars">
                        <svg viewBox="0 0 16 16" width="14" height="14" fill="currentColor">
                            <path d="M8 .25a.75.75 0 01.673.418l1.882 3.815 4.21.612a.75.75 0 01.416 1.279l-3.046 2.97.719 4.192a.75.75 0 01-1.088.791L8 12.347l-3.766 1.98a.75.75 0 01-1.088-.79l.72-4.194L.818 6.374a.75.75 0 01.416-1.28l4.21-.611L7.327.668A.75.75 0 018 .25z"/>
                        </svg>
                        <span>--</span>
                    </span>
                    <span class="github-stat" id="github-forks" title="Forks">
                        <svg viewBox="0 0 16 16" width="14" height="14" fill="currentColor">
                            <path d="M5 3.25a.75.75 0 11-1.5 0 .75.75 0 011.5 0zm0 2.122a2.25 2.25 0 10-1.5 0v.878A2.25 2.25 0 005.75 8.5h1.5v2.128a2.251 2.251 0 101.5 0V8.5h1.5a2.25 2.25 0 002.25-2.25v-.878a2.25 2.25 0 10-1.5 0v.878a.75.75 0 01-.75.75h-4.5A.75.75 0 015 6.25v-.878zm3.75 7.378a.75.75 0 11-1.5 0 .75.75 0 011.5 0zm3-8.75a.75.75 0 100-1.5.75.75 0 000 1.5z"/>
                        </svg>
                        <span>--</span>
                    </span>
                </span>
            </a>
        `;

        // Insert before search or at end of navbar
        const searchBox = navbar.querySelector('#search');
        if (searchBox) {
            navbar.insertBefore(widget, searchBox);
        } else {
            navbar.appendChild(widget);
        }

        // Fetch GitHub stats
        fetchGitHubStats();
        fetchLatestVersion();
    }

    // Fetch stars and forks from GitHub API
    async function fetchGitHubStats() {
        try {
            const response = await fetch(`https://api.github.com/repos/${REPO_OWNER}/${REPO_NAME}`);
            if (!response.ok) return;
            
            const data = await response.json();
            
            // Update stars
            const starsEl = document.querySelector('#github-stars span:last-child');
            if (starsEl && data.stargazers_count !== undefined) {
                starsEl.textContent = formatNumber(data.stargazers_count);
            }
            
            // Update forks
            const forksEl = document.querySelector('#github-forks span:last-child');
            if (forksEl && data.forks_count !== undefined) {
                forksEl.textContent = formatNumber(data.forks_count);
            }
        } catch (error) {
            console.log('Could not fetch GitHub stats:', error);
        }
    }

    // Fetch latest release version from GitHub
    async function fetchLatestVersion() {
        try {
            const response = await fetch(`https://api.github.com/repos/${REPO_OWNER}/${REPO_NAME}/releases/latest`);
            if (!response.ok) {
                // Try tags if no releases
                const tagsResponse = await fetch(`https://api.github.com/repos/${REPO_OWNER}/${REPO_NAME}/tags`);
                if (tagsResponse.ok) {
                    const tags = await tagsResponse.json();
                    if (tags.length > 0) {
                        updateVersion(tags[0].name);
                    }
                }
                return;
            }
            
            const data = await response.json();
            if (data.tag_name) {
                updateVersion(data.tag_name);
            }
        } catch (error) {
            // Try NuGet as fallback
            fetchNuGetVersion();
        }
    }

    // Fetch version from NuGet
    async function fetchNuGetVersion() {
        try {
            const response = await fetch(`https://api.nuget.org/v3-flatcontainer/${NUGET_PACKAGE.toLowerCase()}/index.json`);
            if (!response.ok) return;
            
            const data = await response.json();
            if (data.versions && data.versions.length > 0) {
                const latest = data.versions[data.versions.length - 1];
                updateVersion('v' + latest);
            }
        } catch (error) {
            console.log('Could not fetch NuGet version:', error);
        }
    }

    function updateVersion(version) {
        const versionEl = document.getElementById('github-version');
        if (versionEl) {
            // Clean up version string
            let v = version;
            if (!v.startsWith('v') && !v.startsWith('V')) {
                v = 'v' + v;
            }
            versionEl.textContent = v;
        }
    }

    // Format large numbers (1234 -> 1.2k)
    function formatNumber(num) {
        if (num >= 1000000) {
            return (num / 1000000).toFixed(1) + 'm';
        }
        if (num >= 1000) {
            return (num / 1000).toFixed(1) + 'k';
        }
        return num.toString();
    }

    // Initialize when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', createGitHubWidget);
    } else {
        createGitHubWidget();
    }
})();
