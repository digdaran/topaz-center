import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Dev: `npm run dev` proxies the API to a local central service (dotnet run, port 8080).
export default defineConfig({
  plugins: [react()],
  // The release version (Dockerfile build arg); compared with the server's X-Topaz-Version header.
  define: { __APP_VERSION__: JSON.stringify(process.env.VERSION || 'dev') },
  server: { proxy: { '/api': process.env.TOPAZ_API ?? 'http://localhost:8080' } },
  build: {
    outDir: 'dist',
    chunkSizeWarningLimit: 1500,
    rollupOptions: { output: { manualChunks: { echarts: ['echarts', 'echarts-for-react'], antd: ['antd', '@ant-design/icons'] } } },
  },
  test: { environment: 'node', include: ['src/**/*.test.ts'] },
});
