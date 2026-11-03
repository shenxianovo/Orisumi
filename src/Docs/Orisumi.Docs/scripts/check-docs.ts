import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { basename, dirname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  anchorsOf, frontmatter, jsonBlocks, lineAt, linksOf, mermaidLinksOf, requirementFindings, statementsOf, type Finding, type Link,
} from '../lib/docs-check.ts';

const root = execFileSync('git', ['rev-parse', '--show-toplevel'], {
  cwd: fileURLToPath(new URL('.', import.meta.url)), encoding: 'utf8',
}).trim();
const docs = 'src/Docs/Orisumi.Docs/content/docs';
const isProjectDocument = (file: string) => file.startsWith(`${docs}/`)
  || ['README.md', 'AGENTS.md'].includes(basename(file)) || file.startsWith('.agents/');

const tracked = execFileSync('git', ['-c', 'core.quotepath=off', 'ls-files', '-co', '--exclude-standard'], {
  cwd: root, encoding: 'utf8',
}).split('\n').filter((file) => file && !file.includes('node_modules/') && existsSync(join(root, file)));
const files = tracked.filter((file) => /\.mdx?$/.test(file)).filter(isProjectDocument);

const texts = new Map(files.map((file) => [file, readFileSync(join(root, file), 'utf8')]));
const read = (file: string) => texts.get(file) ?? readFileSync(join(root, file), 'utf8');
const findings: Finding[] = [];
const anchorCache = new Map<string, Set<string>>();
const anchors = (file: string) => {
  if (!anchorCache.has(file)) anchorCache.set(file, anchorsOf(read(file)));
  return anchorCache.get(file)!;
};

function resolveLink(from: string, target: string): { file: string; anchor: string } | null | 'skip' {
  const [path, anchor = ''] = target.split('#');
  if (/^[a-z]+:/i.test(path) || path.startsWith('/llms') || path.startsWith('/og/')) return 'skip';
  if (!path) return { file: from, anchor };
  if (path.startsWith('/')) {
    for (const candidate of [`${docs}${path}.mdx`, `${docs}${path}/index.mdx`]) {
      if (existsSync(join(root, candidate))) return { file: candidate, anchor };
    }
    return null;
  }
  const file = normalize(join(dirname(from), decodeURIComponent(path)));
  return existsSync(join(root, file)) ? { file, anchor } : null;
}

function checkLinks(file: string, text: string, links: Link[], checks: { missing: string; anchor: string }) {
  for (const link of links) {
    const resolved = resolveLink(file, link.target);
    if (resolved === 'skip') continue;
    if (!resolved) {
      findings.push({ file, line: lineAt(text, link.index), check: checks.missing, message: `${link.target} does not exist` });
      continue;
    }
    const isDocument = /\.mdx?$/.test(resolved.file);
    if (resolved.anchor && isDocument && !anchors(resolved.file).has(decodeURIComponent(resolved.anchor))) {
      findings.push({ file, line: lineAt(text, link.index), check: checks.anchor, message: `${link.target} has no anchor #${resolved.anchor}` });
    }
  }
}

for (const [file, text] of texts) {
  checkLinks(file, text, linksOf(text), { missing: 'link', anchor: 'anchor' });
  checkLinks(file, text, mermaidLinksOf(text), { missing: 'mermaid-link', anchor: 'mermaid-link' });

  findings.push(...jsonBlocks(file, text));

  if (file.startsWith(`${docs}/`) && file.endsWith('.mdx')) {
    const fields = frontmatter(text);
    for (const field of ['title', 'description']) {
      if (!fields?.[field]) findings.push({ file, line: 1, check: 'frontmatter', message: `missing ${field}` });
    }
  }
}

const requirementPages = new Map([...texts].filter(([file]) => file.startsWith(`${docs}/requirements/`) && file.endsWith('.mdx')));
findings.push(...requirementFindings([...requirementPages].flatMap(([file, text]) => statementsOf(file, text)), requirementPages));

const metaDirs = new Set<string>();
const listed = new Set<string>();
const wildcard = new Set<string>();
function walk(directory: string): string[] {
  return readdirSync(join(root, directory)).flatMap((name) => {
    const path = `${directory}/${name}`;
    return statSync(join(root, path)).isDirectory() ? walk(path) : [path];
  });
}
const docFiles = walk(docs);
for (const metaFile of docFiles.filter((file) => basename(file) === 'meta.json')) {
  const directory = dirname(metaFile);
  metaDirs.add(directory);
  const pages: string[] = JSON.parse(read(metaFile)).pages ?? [];
  for (const entry of pages) {
    if (entry === '...' || entry === 'z...a') { wildcard.add(directory); continue; }
    if (/^---.*---$/.test(entry) || entry.startsWith('!') || entry.startsWith('[')) continue;
    const target = normalize(join(directory, entry));
    if (!existsSync(join(root, `${target}.mdx`)) && !existsSync(join(root, target))) {
      findings.push({ file: metaFile, line: 1, check: 'sidebar', message: `"${entry}" does not exist` });
    }
    listed.add(target);
  }
}
const folderIncluded = (directory: string): boolean => directory === docs || listed.has(directory)
  || wildcard.has(dirname(directory)) || (!metaDirs.has(dirname(directory)) && folderIncluded(dirname(directory)));
for (const file of docFiles.filter((path) => path.endsWith('.mdx'))) {
  const page = file.slice(0, -'.mdx'.length);
  const directory = dirname(file);
  const covered = listed.has(page) || wildcard.has(directory)
    || (basename(page) === 'index' && listed.has(directory))
    || (!metaDirs.has(directory) && folderIncluded(directory));
  if (!covered) findings.push({ file, line: 1, check: 'sidebar', message: 'no meta.json lists this page' });
}

for (const finding of findings) console.log(`${finding.file}:${finding.line}: [${finding.check}] ${finding.message}`);
console.log(findings.length ? `\n${findings.length} problem(s) in ${files.length} documents.` : `Checked ${files.length} documents.`);
process.exitCode = findings.length ? 1 : 0;
