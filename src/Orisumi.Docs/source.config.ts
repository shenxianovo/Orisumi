import { defineConfig } from 'fumadocs-mdx/config';
import { remarkMdxMermaid } from 'fumadocs-core/mdx-plugins';
import { remarkGitChangelog } from 'fumadocs-page-history/remark';

export default defineConfig({
  mdxOptions: {
    remarkPlugins: [
      remarkMdxMermaid,
      [
        remarkGitChangelog,
        {
          repository: { url: 'https://github.com/shenxianovo/Orisumi' },
          display: {
            locale: 'zh-CN',
            showContributors: true,
            defaultExpanded: true,
          },
        },
      ],
    ],
  },
});
