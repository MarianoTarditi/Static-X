import { readFileSync } from 'node:fs';
import { fileURLToPath, URL } from 'node:url';
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig(({ command, mode, isPreview }) => {
  const env = { ...loadEnv(mode, process.cwd(), ''), ...process.env };
  const target = new URL(env.API_PROXY_TARGET || 'https://localhost:7226');
  if (target.protocol !== 'https:' || !['localhost', '127.0.0.1', '[::1]'].includes(target.hostname)) {
    throw new Error('The development API proxy must target HTTPS on loopback.');
  }
  const serving = command === 'serve' && !isPreview;
  if (serving && !env.HTTPS_PFX_PATH) {
    throw new Error('Set HTTPS_PFX_PATH and HTTPS_PFX_PASSWORD in .env.local. See README.md.');
  }
  return {
    plugins: [react(), tailwindcss()],
    resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
    server: {
      host: 'localhost', port: 5173, strictPort: true,
      https: serving ? { pfx: readFileSync(env.HTTPS_PFX_PATH!), passphrase: env.HTTPS_PFX_PASSWORD } : undefined,
      proxy: {
        '/api': {
          target: target.origin,
          // Preserve browser Origin and Host for same-origin antiforgery validation.
          changeOrigin: false,
          secure: env.ALLOW_UNTRUSTED_LOCAL_API !== 'true',
        },
      },
    },
  };
});
