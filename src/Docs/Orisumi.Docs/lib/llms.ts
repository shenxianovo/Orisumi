import { llms } from 'fumadocs-core/source';
import { posix } from 'node:path';
import { getMarkdownComponents } from '@/components/markdown';
import { linkToMarkdown } from './markdown-links';
import { source } from './source';

export const docsLlms = llms(source, {
  renderPage: async (page) => {
    const markdown = await page.data.getText('processed', { components: getMarkdownComponents() });
    const dir = posix.dirname(page.path);
    const resolve = (href: string) => {
      const target = source.getPageByHref(href, { dir });
      return target && (target.hash ? `${target.page.url}#${target.hash}` : target.page.url);
    };

    return `# ${page.data.title} (${page.url})

${linkToMarkdown(markdown, resolve)}`;
  },
});
