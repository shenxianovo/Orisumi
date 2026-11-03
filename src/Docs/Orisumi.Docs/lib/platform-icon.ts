export function getPlatformIcon(browser: { userAgent: string; maxTouchPoints: number }) {
  return /Macintosh/.test(browser.userAgent) && browser.maxTouchPoints <= 1 ? 'macos' : 'windows';
}
