import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { SiteIcon } from '@/components/site-icon';
import { appName, repositoryUrl } from './shared';

export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: (
        <>
          <SiteIcon />
          {appName}
        </>
      ),
    },
    githubUrl: repositoryUrl,
  };
}
