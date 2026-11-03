import { createMDX } from 'fumadocs-mdx/next';
import { PHASE_DEVELOPMENT_SERVER } from 'next/constants.js';

const withMDX = createMDX();

const config = {
  reactStrictMode: true,
  trailingSlash: true,
  images: { unoptimized: true },
};

const pageExtensionsWithDevelopmentApis = ['tsx', 'ts', 'jsx', 'js', 'dev.ts'];
const pageMarkdownToExportRoute = { source: '/:path((?!llms\\.mdx/).+)\\.md', destination: '/llms.mdx/:path/content.md' };

export default (phase) => withMDX(phase === PHASE_DEVELOPMENT_SERVER
  ? {
    ...config,
    pageExtensions: pageExtensionsWithDevelopmentApis,
    rewrites: async () => [pageMarkdownToExportRoute],
  }
  : { ...config, output: 'export' });
