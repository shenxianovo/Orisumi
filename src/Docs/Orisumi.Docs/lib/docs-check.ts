
export type Finding = { file: string; line: number; check: string; message: string };

export function slugify(heading: string): string {
  return heading.trim().toLowerCase().replace(/[^\p{L}\p{M}\p{N}\p{Pc}\- ]/gu, '').replace(/ /g, '-');
}

export function lineAt(text: string, index: number): number {
  let line = 1;
  for (let i = 0; i < index; i++) if (text.charCodeAt(i) === 10) line++;
  return line;
}

export function maskCode(text: string): string {
  const blank = (match: string) => match.replace(/[^\n]/g, ' ');
  return text.replace(/^```[^\n]*\n[\s\S]*?^```[ \t]*$/gm, blank).replace(/`[^`\n]*`/g, blank);
}

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

export function mermaidLinksOf(text: string): Link[] {
  const links: Link[] = [];
  for (const block of text.matchAll(/^```mermaid(?:[ \t][^\n]*)?\n([\s\S]*?)^```[ \t]*$/gm)) {
    const start = block.index! + block[0].indexOf('\n') + 1;
    for (const match of block[1].matchAll(/^[ \t]*click[ \t]+(\S+)[ \t]+href[ \t]+(["'])(.*?)\2/gm)) {
      links.push({ text: match[1], target: match[3], index: start + match.index! });
    }
  }
  return links;
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

export const levels = ['公理', '假设', '推论'] as const;
export type Level = (typeof levels)[number];
export type Statement = { page: string; name: string; level: Level; basis: string[]; line: number };

export function statementsOf(page: string, text: string): Statement[] {
  const masked = maskCode(text);
  const statements: Statement[] = [];
  let level: Level | null = null;
  let current: Statement | null = null;
  let offset = 0;
  for (const line of masked.split('\n')) {
    const section = line.match(/^##[ \t]+(.+?)[ \t]*$/);
    const heading = line.match(/^###[ \t]+(.+?)[ \t]*$/);
    if (section) {
      level = (levels as readonly string[]).includes(section[1]) ? section[1] as Level : null;
      current = null;
    } else if (heading && level) {
      current = { page, name: heading[1].replace(/[ \t]*\[#[^\]]+\]$/, ''), level, basis: [], line: lineAt(text, offset) };
      statements.push(current);
    } else if (current && line.startsWith('依据：')) {
      current.basis.push(...linksOf(line).map((link) => decodeURIComponent(link.target.split('#')[1] ?? '')));
    }
    offset += line.length + 1;
  }
  return statements;
}

type Graph = { line: number; labels: Map<string, string>; edges: [string, string][]; hypotheses: string[]; clicks: Map<string, string> };

export function mermaidGraphsOf(text: string): Graph[] {
  return [...text.matchAll(/^```mermaid(?:[ \t][^\n]*)?\n([\s\S]*?)^```[ \t]*$/gm)].map((block) => {
    const body = block[1];
    const labels = new Map([...body.matchAll(/^[ \t]*([\w-]+)[ \t]*\[[ \t]*"?([^"\]]+?)"?[ \t]*\][ \t]*$/gm)].map((match) => [match[1], match[2]]));
    const edges = [...body.matchAll(/^[ \t]*([\w-]+)[ \t]*-->[ \t]*([\w-]+)[ \t]*$/gm)].map((match) => [match[1], match[2]] as [string, string]);
    const hypotheses = [...body.matchAll(/^[ \t]*class[ \t]+([\w,-]+)[ \t]+hypothesis[ \t]*$/gm)].flatMap((match) => match[1].split(','));
    const clicks = new Map([...body.matchAll(/^[ \t]*click[ \t]+([\w-]+)[ \t]+href[ \t]+(["'])(.*?)\2/gm)].map((match) => [match[1], match[3]]));
    return { line: lineAt(text, block.index!), labels, edges, hypotheses, clicks };
  });
}

const requirementFinding = (file: string, line: number, message: string): Finding => ({ file, line, check: 'requirements', message });

export function requirementFindings(statements: Statement[], texts: Map<string, string>): Finding[] {
  const byName = new Map<string, Statement>();
  const duplicates = statements.flatMap((statement) => {
    const existing = byName.get(statement.name);
    if (!existing) byName.set(statement.name, statement);
    return existing ? [requirementFinding(statement.page, statement.line, `${statement.name} is already a statement in ${existing.page}`)] : [];
  });
  const cycles = cyclesOf(byName).map((cycle) => {
    const first = byName.get(cycle[0])!;
    return requirementFinding(first.page, first.line, `依据 forms a cycle: ${cycle.join(' → ')}`);
  });
  const graphs = [...texts].flatMap(([page, text]) => mermaidGraphsOf(text).flatMap((graph) =>
    graphFindings(page, graph, statements.filter((statement) => statement.page === page), byName)));
  return [...duplicates, ...statements.flatMap((statement) => basisFindings(statement, byName)), ...cycles, ...graphs];
}

function basisFindings(statement: Statement, byName: Map<string, Statement>): Finding[] {
  const report = (message: string) => requirementFinding(statement.page, statement.line, message);
  const findings: Finding[] = [];
  if (statement.level === '推论' && statement.basis.length === 0) findings.push(report(`推论 ${statement.name} needs 依据`));
  if (statement.level !== '推论' && statement.basis.length > 0) {
    findings.push(report(`${statement.level} ${statement.name} is a starting point and takes no 依据`));
  }
  const unknown = statement.basis.filter((basis) => !byName.has(basis));
  return [...findings, ...unknown.map((basis) => report(`依据 of ${statement.name} is not a statement: ${basis}`))];
}

function cyclesOf(byName: Map<string, Statement>): string[][] {
  const cycles: string[][] = [];
  const state = new Map<string, 'visiting' | 'done'>();
  const visit = (name: string, path: string[]) => {
    if (state.get(name) === 'done') return;
    if (state.get(name) === 'visiting') {
      cycles.push([...path.slice(path.indexOf(name)), name]);
      return;
    }
    state.set(name, 'visiting');
    for (const basis of byName.get(name)?.basis ?? []) visit(basis, [...path, name]);
    state.set(name, 'done');
  };
  for (const name of byName.keys()) visit(name, []);
  return cycles;
}

const missingFrom = <T>(expected: Iterable<T>, actual: Set<T>) => [...expected].filter((item) => !actual.has(item));

function graphFindings(page: string, graph: Graph, own: Statement[], byName: Map<string, Statement>): Finding[] {
  const label = (id: string) => graph.labels.get(id) ?? id;
  const nodes = new Set(graph.labels.values());
  const drawn = new Set(graph.edges.map(([from, to]) => `${label(from)} → ${label(to)}`));
  const stated = new Set(own.flatMap((statement) => statement.basis.map((basis) => `${basis} → ${statement.name}`)));
  const dashed = new Set(graph.hypotheses.map(label));
  const hypotheses = own.filter((statement) => statement.level === '假设').map((statement) => statement.name);
  const messages = [
    ...missingFrom(own.map((statement) => statement.name), nodes).map((name) => `${name} is missing from the graph`),
    ...[...nodes].filter((name) => !byName.has(name)).map((name) => `${name} in the graph is not a statement`),
    ...missingFrom(stated, drawn).map((edge) => `the graph lacks the edge ${edge}`),
    ...missingFrom(drawn, stated).map((edge) => `the edge ${edge} has no matching 依据`),
    ...missingFrom(hypotheses, dashed).map((name) => `假设 ${name} needs class hypothesis`),
    ...[...dashed].filter((name) => byName.get(name)?.level !== '假设').map((name) => `${name} has class hypothesis but is not a 假设`),
    ...[...graph.labels].filter(([id, name]) => graph.clicks.get(id) !== `#${name}`)
      .map(([id, name]) => `${name} needs click ${id} href "#${name}"`),
  ];
  return messages.map((message) => ({ file: page, line: graph.line, check: 'requirements-graph', message }));
}
