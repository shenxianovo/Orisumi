import { createMDX } from 'fumadocs-mdx/next';

const withMDX = createMDX();

/** @type {import('next').NextConfig} */
const config = {
  reactStrictMode: true,
  agentRules: false,
  // The docs root has no page of its own; each tab is a root folder.
  async redirects() {
    return [{ source: '/docs', destination: '/docs/overview', permanent: false }];
  },
};

export default withMDX(config);
