import assert from 'node:assert/strict';
import { test } from 'node:test';
import { anchorsOf, frontmatter, jsonBlocks, linksOf, slugify } from '../lib/docs-check.ts';

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

test('frontmatter reads title and description', () => {
  assert.deepEqual(frontmatter('---\ntitle: "Orisumi"\ndescription: 重写中\n---\n'), { title: 'Orisumi', description: '重写中' });
  assert.equal(frontmatter('# 没有 frontmatter\n'), null);
});

test('JSON examples must parse', () => {
  assert.equal(jsonBlocks('a.md', '```json\n{"a": 1}\n```\n```json\n{"a": }\n```\n').length, 1);
  assert.equal(jsonBlocks('a.md', '```json title="meta.json" {2}\n{"a": }\n```\n').length, 1);
});
