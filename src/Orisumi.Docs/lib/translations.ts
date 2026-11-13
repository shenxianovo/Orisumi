import { defineTranslations } from 'fumadocs-core/i18n';
import { uiTranslations } from 'fumadocs-ui/i18n';
import { gitChangelogTranslations, zhCN as historyZhCN } from 'fumadocs-page-history/i18n';

export const translations = defineTranslations()
  .extend(uiTranslations())
  .extend(gitChangelogTranslations())
  .add({
    displayName: '简体中文',
    'Search(search trigger)': '搜索文档',
    'Search(search dialog)': '搜索文档',
    'Open Search(search trigger)(aria-label)': '打开搜索',
    'Close Search(search dialog)(aria-label)': '关闭搜索',
    'No results found(search dialog)': '没有找到结果',
    'On this page(table of contents)': '本页内容',
    'No Headings(table of contents)': '暂无标题',
    'Table of Contents(inline table of contents)': '目录',
    'Next Page(pagination)': '下一页',
    'Previous Page(pagination)': '上一页',
    'Copy Markdown(page actions)': '复制 Markdown',
    'Copied Markdown(page actions)': '已复制 Markdown',
    'Open(page actions)': '打开',
    'View as Markdown(page actions)': '查看 Markdown',
    'Open in GitHub(page actions)': '在 GitHub 查看',
    'Copy Text(code block)(aria-label)': '复制代码',
    'Copied Text(code block)(aria-label)': '已复制代码',
    'Light(theme switcher)(aria-label)': '浅色',
    'Dark(theme switcher)(aria-label)': '深色',
    'System(theme switcher)(aria-label)': '跟随系统',
    'Toggle Theme(theme switcher)(aria-label)': '切换主题',
    'Skip to Content(skip link)': '跳到正文',
  })
  .preset(historyZhCN());
