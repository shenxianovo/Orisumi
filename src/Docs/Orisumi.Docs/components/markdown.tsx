import { asMarkdown, md } from 'fumadocs-core/server';
import type { ReactNode } from 'react';

function Callout({ title, children }: { title?: ReactNode; children?: ReactNode }) {
  asMarkdown();
  return title ? md.linePrefix('> ')`**${title}**\n\n${children}` : md.linePrefix('> ')`${children}`;
}

const asTightList = (items: string) => items.trim().replace(/\n{2,}/g, '\n');

async function Cards({ children }: { children?: ReactNode }) {
  asMarkdown();
  return `${asTightList(await md`${children}`)}\n\n`;
}

async function Card({ title, href, children }: { title: ReactNode; href?: string; children?: ReactNode }) {
  asMarkdown();
  const name = href ? md`[${title}](${href})` : md`**${title}**`;
  const body = (await md`${children}`).trim();
  return md`- ${name}${body && `：${body}`}\n`;
}

export function getMarkdownComponents() {
  return { Callout, Cards, Card };
}
