// Vite builds and serves the front end; Vitest (the test runner) reads the "test" part of the same file.
// See docs/03-architecture.md, part v1.2: "Front-end stack" and "How the API serves the front end".
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  // The React plugin compiles JSX and, in `npm run dev`, refreshes a changed component without reloading the page.
  plugins: [react()],

  build: {
    // A-1: the API serves the front end from its own web root (src/InventoryTracker.Api/wwwroot), so `dotnet publish`
    // copies the built files into the published folder by itself. The folder is generated and git-ignored: never put a
    // file there by hand, because every build empties it first.
    outDir: '../InventoryTracker.Api/wwwroot',
    emptyOutDir: true,
    // AC-37.5 (strict Content-Security-Policy): never inline a small image or font as a data: URL; every asset is a
    // file of its own, served from the same address.
    assetsInlineLimit: 0,
  },

  server: {
    // `npm run dev` serves on http://localhost:5173 and passes every /api and /health request on to the API running
    // from source on http://localhost:5133 (the launch profile). The browser sees one address, so there is no CORS.
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://localhost:5133',
      '/health': 'http://localhost:5133',
    },
  },

  test: {
    // jsdom is a browser page simulated in Node, so component tests can render and click without a real browser.
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
    restoreMocks: true,
  },
});
