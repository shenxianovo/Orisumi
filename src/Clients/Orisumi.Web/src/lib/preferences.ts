// 只在本机浏览器里记住的偏好。读写失败时使用默认值。

export type Theme = "system" | "light" | "dark";

const THEME_KEY = "orisumi.theme";
const SEEN_KEY = "orisumi.understanding.seen";
const SEEN_EVENT = "orisumi:understanding-seen";

function read(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // 存不下时只影响下次打开。
  }
}

export function readTheme(): Theme {
  const value = read(THEME_KEY);
  return value === "light" || value === "dark" ? value : "system";
}

export function applyTheme(theme: Theme): void {
  if (theme === "system") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", theme);
}

export function saveTheme(theme: Theme): void {
  write(THEME_KEY, theme);
  applyTheme(theme);
}

/** 上次查看理解页面的时刻，没有看过时为 0。 */
export function readSeen(): number {
  return Number(read(SEEN_KEY)) || 0;
}

export function saveSeen(at: number): void {
  write(SEEN_KEY, String(at));
  window.dispatchEvent(new Event(SEEN_EVENT));
}

export function subscribeSeen(listener: () => void): () => void {
  window.addEventListener(SEEN_EVENT, listener);
  return () => window.removeEventListener(SEEN_EVENT, listener);
}
