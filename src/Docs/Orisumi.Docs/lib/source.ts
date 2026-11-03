import { loader } from 'fumadocs-core/source';
import { lucideIconsPlugin } from 'fumadocs-core/source/lucide-icons';
import { docsRoute } from './routes';
import { defineDocs } from 'fumadocs-mdx/macro';
import { metaSchema, pageSchema } from 'fumadocs-core/source/schema';

const docs = defineDocs({
  dir: 'content/docs',
  docs: {
    schema: pageSchema,
    postprocess: {
      includeProcessedMarkdown: { output: 'function' },
      extractLinkReferences: true,
    },
  },
  meta: {
    schema: metaSchema,
  },
});

export const source = loader(
  {
    docs: docs.toFumadocsSource(),
  },
  {
    baseUrl: docsRoute,
    plugins: [lucideIconsPlugin()],
  },
);

export interface Section {
  slug: string;
  name: string;
}

type TreeNode = ReturnType<typeof source.getPageTree>['children'][number];

function firstUrl(node: TreeNode): string | undefined {
  if (node.type === 'page') return node.url;
  if (node.type === 'folder') return node.index?.url ?? node.children.map(firstUrl).find(Boolean);
}

export function getSections(): Section[] {
  return source.getPageTree().children.flatMap((node) => {
    const url = node.type === 'folder' && node.root ? firstUrl(node) : undefined;
    return url ? [{ slug: url.split('/')[1], name: String(node.name) }] : [];
  });
}
