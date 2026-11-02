import { expect, test, type Page } from "@playwright/test";

// 默认的 e2e 不连后端：用路由拦截给出空的列表和 404，检查外壳和空状态。真实数据下的视图和记录页由 tests/aspire/edge.spec.ts 检查。

const EMPTY_PAGE = { observations: [], next: null };

/** 列表接口返回空页，批量读取事物都读不到，其余读取返回 404。 */
async function emptyApi(page: Page) {
  // 只拦截 /api 下的请求；Vite 的 /src/api/*.ts 模块照常加载。
  await page.route(
    (url) => url.pathname.startsWith("/api/"),
    (route) => {
      const { pathname } = new URL(route.request().url());
      if (pathname === "/api/observations") return route.fulfill({ json: EMPTY_PAGE });
      if (pathname === "/api/search") return route.fulfill({ json: EMPTY_PAGE });
      if (pathname === "/api/schema-revisions") return route.fulfill({ json: { schemaRevisions: [], next: null } });
      if (pathname === "/api/thing-types") return route.fulfill({ json: { thingTypes: [] } });
      if (pathname === "/api/observer-instances") return route.fulfill({ json: { instances: [] } });
      if (pathname === "/api/hosts") return route.fulfill({ json: { hosts: [] } });
      if (pathname === "/api/things") return route.fulfill({ json: { things: [], next: null } });
      if (pathname === "/api/things/read") {
        const { thingIds } = route.request().postDataJSON() as { thingIds: string[] };
        return route.fulfill({ json: { results: thingIds.map((thingId) => ({ thingId, status: "not_found" })) } });
      }
      return route.fulfill({ status: 404, json: { title: "Not Found", status: 404 } });
    },
  );
}

test.beforeEach(async ({ page }) => {
  await emptyApi(page);
});

test.describe("外壳", () => {
  test("外观菜单切换主题，写到 html 上", async ({ page }) => {
    await page.goto("/");
    await page.getByRole("button", { name: "外观" }).click();
    await page.getByRole("button", { name: "深", exact: true }).click();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    await page.getByRole("button", { name: "外观" }).click();
    await page.getByRole("button", { name: "系统", exact: true }).click();
    await expect(page.locator("html")).not.toHaveAttribute("data-theme");
  });

  test("⌘K 打开查找：搜索文字，ID 找不到时提示", async ({ page }) => {
    await page.goto("/");
    // 快捷键在页面渲染后才开始监听。
    await expect(page.getByText("今天还没有记录")).toBeVisible();
    await page.keyboard.press("ControlOrMeta+k");
    const input = page.getByLabel("查找文字或 ID");
    await expect(input).toBeFocused();
    await input.fill("hello");
    await input.press("Enter");
    await expect(page.getByText("没有找到“hello”")).toBeVisible();
    await input.fill("0192b056-7300-7000-8000-000000000001");
    await input.press("Enter");
    await expect(page.getByRole("alert")).toHaveText("找不到这个 ID");
  });
});

test.describe("空状态", () => {
  test("日页面今天没有 Observation", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByText("今天还没有记录")).toBeVisible();
  });

  test("事物页面按类型分组", async ({ page }) => {
    await page.goto("/things");
    await expect(page.getByRole("heading", { name: "事物" })).toBeVisible();
    await expect(page.getByRole("region", { name: "设备" }).getByText("这里还没有")).toBeVisible();
  });

  test("理解页面还没有判断", async ({ page }) => {
    await page.goto("/understanding");
    await expect(page.getByText("还没有判断")).toBeVisible();
  });

  test("Observer 页面还没有 Observer", async ({ page }) => {
    await page.goto("/observers");
    await expect(page.getByText("还没有 Observer")).toBeVisible();
    await expect(page.getByText("0 个 Observer，0 个正在写入")).toBeVisible();
  });
});
