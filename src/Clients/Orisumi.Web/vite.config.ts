import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { TanStackRouterVite } from "@tanstack/router-plugin/vite";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.dirname(fileURLToPath(import.meta.url));

// Aspire 把分配到的端口经 PORT 注入；独立运行时回落到 5173。
const port = process.env.PORT ? Number(process.env.PORT) : 5173;

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    TanStackRouterVite({ routesDirectory: "./src/routes", generatedRouteTree: "./src/routeTree.gen.ts" }),
    react(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      "@": path.resolve(root, "./src"),
    },
  },
  server: {
    host: true,
    port,
    strictPort: true,
    // 经 nginx 同源反代时，HMR 沿用浏览器访问的主机与端口，无需额外 clientPort。
    proxy: {
      // 独立运行时转给本地环境的 API 入口，入口自己去掉 /api 前缀。
      "/api": {
        target: "http://localhost:8080",
        changeOrigin: true,
      },
    },
  },
});
