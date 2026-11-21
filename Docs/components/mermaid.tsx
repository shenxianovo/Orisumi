'use client';

import { use, useEffect, useId, useState } from 'react';
import { useTheme } from 'next-themes';

// Renders ```mermaid blocks (converted by remarkMdxMermaid) on the client, following the site theme.
export function Mermaid({ chart }: { chart: string }) {
  const [mounted, setMounted] = useState(false);
  useEffect(() => setMounted(true), []);
  return mounted ? <Chart chart={chart} /> : null;
}

const cache = new Map<string, Promise<unknown>>();
function cached<T>(key: string, load: () => Promise<T>): Promise<T> {
  let promise = cache.get(key) as Promise<T> | undefined;
  if (!promise) cache.set(key, (promise = load()));
  return promise;
}

function Chart({ chart }: { chart: string }) {
  const id = useId();
  const { resolvedTheme } = useTheme();
  const { default: mermaid } = use(cached('mermaid', () => import('mermaid')));
  mermaid.initialize({
    startOnLoad: false,
    securityLevel: 'loose',
    fontFamily: 'inherit',
    themeCSS: 'margin: 1.5rem auto 0;',
    theme: resolvedTheme === 'dark' ? 'dark' : 'default',
  });
  const { svg, bindFunctions } = use(
    cached(`${resolvedTheme}:${chart}`, () => mermaid.render(id, chart.replaceAll('\\n', '\n'))),
  );
  return <div ref={(container) => { if (container) bindFunctions?.(container); }} dangerouslySetInnerHTML={{ __html: svg }} />;
}
