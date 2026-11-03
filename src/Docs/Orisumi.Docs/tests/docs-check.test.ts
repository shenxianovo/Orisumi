import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  anchorsOf, frontmatter, jsonBlocks, lineAt, linksOf, mermaidLinksOf, requirementFindings, slugify, statementsOf,
} from '../lib/docs-check.ts';

test('slugs keep CJK text and drop punctuation like github-slugger', () => {
  assert.equal(slugify('API 遥测验证'), 'api-遥测验证');
  assert.equal(slugify('Observers · Integration Guide'), 'observers--integration-guide');
});

test('explicit heading ids replace the slug, and headings in code blocks are ignored', () => {
  const anchors = anchorsOf('## 事实时间 [#fact-time]\n### 时间\n```md\n## 不是标题\n```\n');
  assert.deepEqual([...anchors], ['fact-time', '时间']);
});

test('links inside code are not links', () => {
  const links = linksOf('[A](./a.mdx) `[B](./b.mdx)`\n```\n[C](./c.mdx)\n```\n');
  assert.deepEqual(links.map((link) => link.target), ['./a.mdx']);
});

test('mermaid click links point at the click line, and only mermaid blocks count', () => {
  const page = '## 观测\n```mermaid\nflowchart TD\n  observe["观测"]\n  click observe href "#观测"\n'
    + "  click user href '#用户'\n  click site href \"https://orisumi.dev\"\n```\n"
    + '```text\nclick other href "#其他"\n```\n';
  const links = mermaidLinksOf(page);
  assert.deepEqual(links.map((link) => [link.text, link.target, lineAt(page, link.index)]),
    [['observe', '#观测', 5], ['user', '#用户', 6], ['site', 'https://orisumi.dev', 7]]);
  const anchors = anchorsOf(page);
  assert.ok(anchors.has('观测'));
  assert.ok(!anchors.has('用户'));
});

test('frontmatter reads title and description', () => {
  assert.deepEqual(frontmatter('---\ntitle: "Orisumi"\ndescription: 重写中\n---\n'), { title: 'Orisumi', description: '重写中' });
  assert.equal(frontmatter('# 没有 frontmatter\n'), null);
});

test('JSON examples must parse', () => {
  assert.equal(jsonBlocks('a.md', '```json\n{"a": 1}\n```\n```json\n{"a": }\n```\n').length, 1);
  assert.equal(jsonBlocks('a.md', '```json title="meta.json" {2}\n{"a": }\n```\n').length, 1);
});

const graphPage = (overrides: { graph?: string; body?: string } = {}) => [
  '```mermaid',
  'flowchart TD',
  overrides.graph ?? [
    '  observe["观测"]', '  loss["数据流失"]', '  collect["收集一切"]',
    '  observe --> collect', '  loss --> collect',
    '  class loss hypothesis',
    '  click observe href "#观测"', '  click loss href "#数据流失"', '  click collect href "#收集一切"',
  ].join('\n'),
  '```',
  '## 公理', '### 观测', '观测是出发点。',
  '## 假设', '### 数据流失', '数据会丢。',
  '## 推论',
  overrides.body ?? '### 收集一切\n尽可能收集。\n\n依据：[观测](#观测)、[数据流失](#数据流失)。',
  '',
].join('\n');

const check = (text: string) => {
  const statements = statementsOf('belief.mdx', text);
  return requirementFindings(statements, new Map([['belief.mdx', text]])).map((finding) => finding.message);
};

test('statements carry their level, line and 依据', () => {
  const statements = statementsOf('belief.mdx', graphPage());
  assert.deepEqual(statements.map((statement) => [statement.name, statement.level, statement.basis]),
    [['观测', '公理', []], ['数据流失', '假设', []], ['收集一切', '推论', ['观测', '数据流失']]]);
  assert.equal(statements[0].line, 14);
});

test('a consistent dependency graph has no findings', () => {
  assert.deepEqual(check(graphPage()), []);
});

test('推论 need 依据 that name existing statements, and starting points take none', () => {
  assert.deepEqual(check(graphPage({ body: '### 收集一切\n尽可能收集。' })), [
    '推论 收集一切 needs 依据',
    'the edge 观测 → 收集一切 has no matching 依据',
    'the edge 数据流失 → 收集一切 has no matching 依据',
  ]);
  assert.ok(check(graphPage({ body: '### 收集一切\n依据：[不存在](#不存在)。' })).includes('依据 of 收集一切 is not a statement: 不存在'));
  const axiomWithBasis = graphPage().replace('观测是出发点。', '观测是出发点。\n\n依据：[数据流失](#数据流失)。');
  assert.ok(check(axiomWithBasis).includes('公理 观测 is a starting point and takes no 依据'));
});

test('依据 must not form a cycle', () => {
  const findings = check(graphPage({ body: '### 收集一切\n依据：[观测](#观测)、[数据流失](#数据流失)、[再收集](#再收集)。\n\n### 再收集\n依据：[收集一切](#收集一切)。' }));
  assert.ok(findings.some((message) => message.startsWith('依据 forms a cycle')));
});

test('the graph draws every statement, every 依据, the hypotheses and the clicks', () => {
  const graph = [
    '  observe["观测"]', '  loss["数据流失"]', '  collect["收集一切"]', '  extra["多余"]',
    '  observe --> collect', '  extra --> collect',
    '  class observe hypothesis',
    '  click observe href "#观测"', '  click loss href "#数据流失"',
  ].join('\n');
  assert.deepEqual(check(graphPage({ graph })).sort(), [
    '多余 in the graph is not a statement',
    'the graph lacks the edge 数据流失 → 收集一切',
    'the edge 多余 → 收集一切 has no matching 依据',
    '假设 数据流失 needs class hypothesis',
    '观测 has class hypothesis but is not a 假设',
    '收集一切 needs click collect href "#收集一切"',
    '多余 needs click extra href "#多余"',
  ].sort());
});
