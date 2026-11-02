/** Pure checks behind `pnpm docs:check`. File access lives in scripts/check-docs.ts. */

export type Finding = { file: string; line: number; check: string; message: string };

/** Heading anchors follow github-slugger, which fumadocs and GitHub both use. */
export function slugify(heading: string): string {
  return heading.trim().toLowerCase().replace(/[^\p{L}\p{M}\p{N}\p{Pc}\- ]/gu, '').replace(/ /g, '-');
}

export function lineAt(text: string, index: number): number {
  let line = 1;
  for (let i = 0; i < index; i++) if (text.charCodeAt(i) === 10) line++;
  return line;
}

/** Blank out fenced code blocks and inline code, keeping offsets and line numbers intact. */
export function maskCode(text: string): string {
  const blank = (match: string) => match.replace(/[^\n]/g, ' ');
  return text.replace(/^```[^\n]*\n[\s\S]*?^```[ \t]*$/gm, blank).replace(/`[^`\n]*`/g, blank);
}

/** Every anchor a page offers: explicit `[#id]` markers and slugs of the remaining headings. */
export function anchorsOf(text: string): Set<string> {
  const anchors = new Set<string>();
  for (const match of maskCode(text).matchAll(/^#{1,6}[ \t]+(.+?)[ \t]*$/gm)) {
    const explicit = match[1].match(/\[#([^\]]+)\]$/);
    anchors.add(explicit ? explicit[1] : slugify(match[1]));
  }
  return anchors;
}

export type Link = { target: string; text: string; index: number };

export function linksOf(text: string): Link[] {
  const masked = maskCode(text);
  return [...masked.matchAll(/\[([^\]\n]*)\]\(([^)\s]+)\)/g)].map((match) => ({
    text: text.slice(match.index! + 1, match.index! + 1 + match[1].length),
    target: match[2],
    index: match.index!,
  }));
}

export function frontmatter(text: string): Record<string, string> | null {
  const match = text.match(/^---\n([\s\S]*?)\n---\n/);
  if (!match) return null;
  return Object.fromEntries([...match[1].matchAll(/^(\w+):\s*"?(.*?)"?\s*$/gm)].map((field) => [field[1], field[2]]));
}

export function jsonBlocks(file: string, text: string): Finding[] {
  const findings: Finding[] = [];
  for (const match of text.matchAll(/^```json(?:[ \t][^\n]*)?\n([\s\S]*?)^```[ \t]*$/gm)) {
    try {
      JSON.parse(match[1]);
    } catch (error) {
      findings.push({ file, line: lineAt(text, match.index!), check: 'json', message: (error as Error).message });
    }
  }
  return findings;
}
