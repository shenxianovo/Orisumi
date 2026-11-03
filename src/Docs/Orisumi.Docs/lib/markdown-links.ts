export function markdownUrl(url: string): string {
  const [path, anchor] = url.split('#');
  return `${path}.md${anchor ? `#${anchor}` : ''}`;
}

export function linkToMarkdown(markdown: string, resolve: (href: string) => string | undefined): string {
  return markdown.replace(/\]\(([^)\s]+)\)/g, (link, href: string) => {
    if (/^([a-z]+:|#)/i.test(href)) return link;
    const url = resolve(href);
    return url ? `](${markdownUrl(url)})` : link;
  });
}
