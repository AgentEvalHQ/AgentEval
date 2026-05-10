// Plan-08 MC1.6.2: Tailwind 4 config.
//
// Tailwind 4 prefers the new `@import "tailwindcss"` directive (used in
// src/index.css) plus this minimal config for content-purging. The Vite
// plugin (@tailwindcss/vite) handles the rest — no postcss.config needed.
import type { Config } from "tailwindcss";

const config: Config = {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        // Mission Control palette: slate base + accent blue + status hues.
        accent: {
          50: "#eff6ff",
          500: "#3b82f6",
          700: "#1d4ed8",
        },
      },
      fontFamily: {
        sans: ["Inter", "system-ui", "sans-serif"],
        mono: ["JetBrains Mono", "monospace"],
      },
    },
  },
  plugins: [],
};

export default config;
