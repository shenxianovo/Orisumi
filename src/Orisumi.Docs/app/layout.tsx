import type { Metadata } from 'next';
import { RootProvider } from 'fumadocs-ui/provider/next';
import { i18nProvider } from 'fumadocs-ui/i18n';
import { translations } from '@/lib/translations';
import './global.css';

export const metadata: Metadata = {
  title: { default: 'Orisumi', template: '%s | Orisumi' },
  description: 'Orisumi 项目文档。',
};

export default function Layout({ children }: LayoutProps<'/'>) {
  return (
    <html lang="zh-CN" suppressHydrationWarning>
      <body className="flex min-h-screen flex-col">
        <RootProvider i18n={i18nProvider(translations)}>{children}</RootProvider>
      </body>
    </html>
  );
}
