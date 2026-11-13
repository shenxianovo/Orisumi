import { defineConfig } from 'fumadocs-mdx/config';
import { remarkGitChangelog } from 'fumadocs-page-history/remark';

export default defineConfig({
  mdxOptions: {
    remarkPlugins: [
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
