import { expect, test } from "@playwright/test";
import { Backend, uuid } from "./backend";

// 应用：产品和未归属的本机应用都列在事物页；线索归属是模型的判断，在理解页否定。

const ref = { type: "reference", target: "thing", description: "", required: true, nullable: false };

function seed() {
  const backend = new Backend();
  const foreground = backend.schema("foreground-state-observers", "前台状态 Observer", "observation");
  const recognizer = backend.schema("application-identity-observers", "应用识别 Observer", "observation");
  const installs = backend.schema("local-applications", "本机应用", "thing");
  const clues = backend.schema("application-identifiers", "应用线索", "thing");
  const products = backend.schema("application-products", "应用产品", "thing");
  backend.schema("devices", "设备", "thing");
  backend.schema("names", "名字", "observation");
  const memberships = backend.schema("application-memberships", "线索归属", "observation", {
    identifierId: ref,
    productId: ref,
  });
  const descriptions = backend.schema("local-application-descriptions", "本机应用描述", "observation", {
    localApplicationId: ref,
    identifierIds: { type: "array", items: ref, description: "", required: true, nullable: false },
  });
  const capture = backend.program(foreground, { platform: "macos" });
  const identity = backend.program(recognizer, { model: "openai/test" });

  const clue = (value: string) => backend.thing(clues.entityId, { scope: null, scheme: "macos.bundle-id", value });
  const qq = clue("com.tencent.qq");
  const lark = clue("com.electron.lark");
  const installOf = (identifier: { entityId: string }, value: string, path: string) => {
    const install = backend.thing(installs.entityId, { scope: "device", scheme: "macos.installation", value });
    backend.observe(descriptions, capture.thing, {
      localApplicationId: install.entityId,
      deviceId: uuid(),
      identifierIds: [identifier.entityId],
      executablePath: path,
    });
    return install;
  };
  const install = installOf(qq, "com.tencent.qq\n/Applications/QQ.app", "/Applications/QQ.app/Contents/MacOS/QQ");
  installOf(lark, "com.electron.lark\n/Applications/Lark.app", "/Applications/Lark.app/Contents/MacOS/Lark");
  const product = backend.thing(products.entityId, null, "QQ");
  const membership = backend.observe(
    memberships,
    identity.thing,
    { identifierId: qq.entityId, productId: product.entityId },
    { formation: "inferred", observerVersionId: identity.description.versionId },
  );
  return { backend, install, product, membership };
}

test("产品和未归属的本机应用都列出，归属的本机应用不再单独列出，未归属的写应用包标识", async ({ page }) => {
  await seed().backend.serve(page);
  await page.goto("/things");
  const applications = page.getByRole("region", { name: "应用" });
  await expect(applications.getByRole("link")).toHaveCount(2);
  await expect(applications.getByRole("link", { name: /QQ/ })).toBeVisible();
  await expect(applications.getByRole("link", { name: /com\.electron\.lark/ })).toBeVisible();
  await expect(page.getByText("/Applications/Lark.app")).toHaveCount(0);
});

test("按名字筛选事物", async ({ page }) => {
  await seed().backend.serve(page);
  await page.goto("/things");
  await page.getByLabel("按名字筛选").fill("lark");
  await expect(page.getByRole("region", { name: "应用" }).getByRole("link")).toHaveCount(1);
});

test("本机应用的档案连到产品，线上的归属是推断", async ({ page }) => {
  const { backend, install } = seed();
  await backend.serve(page);
  await page.goto(`/things/${install.entityId}`);
  await expect(page.getByRole("heading", { name: "QQ", exact: true })).toBeVisible();
  const related = page.locator("section", { has: page.getByRole("heading", { name: "和它有关的" }) });
  await expect(related.getByRole("link", { name: /应用产品/ })).toBeVisible();
  await related.getByRole("button", { name: "凭什么" }).last().click();
  await expect(page).toHaveURL(/stack=/);
  await expect(page.getByRole("complementary", { name: "面板" }).getByText("由谁记下")).toBeVisible();
});

test("否定一条线索归属后，线索回到未归属，可以撤回", async ({ page }) => {
  await seed().backend.serve(page);
  await page.goto("/understanding");
  const row = page.getByRole("listitem").filter({ hasText: "com.tencent.qq" });
  await expect(row.getByText("应用识别 Observer · 推断")).toBeVisible();
  await row.getByRole("button", { name: "否定" }).click();
  await row.getByLabel("否定的说明").fill("不是这个产品");
  await row.getByRole("button", { name: "否定" }).last().click();
  await expect(row.getByText("已否定")).toBeVisible();

  await page.goto("/things");
  await expect(
    page.getByRole("region", { name: "应用" }).getByRole("link", { name: /com\.tencent\.qq/ }),
  ).toBeVisible();

  await page.goto("/understanding");
  await page.getByRole("button", { name: /^全部/ }).click();
  await row.getByRole("button", { name: "撤回" }).click();
  await expect(row.getByRole("button", { name: "确认" })).toBeVisible();
});

test("打开理解页面后，侧栏不再写新判断的数量", async ({ page }) => {
  await seed().backend.serve(page);
  await page.goto("/");
  await expect(page.getByRole("link", { name: "模型新认定 1 件 →" })).toBeVisible();
  await page.getByRole("link", { name: "模型新认定 1 件 →" }).click();
  await expect(page.getByRole("button", { name: "待看 1" })).toBeVisible();
  await expect(page.getByText("1 件新判断")).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole("button", { name: "待看 0" })).toBeVisible();
  await expect(page.getByText("都看过了")).toBeVisible();
});

test("在档案里写一个名字，它成为事物的名字", async ({ page }) => {
  const { backend, install } = seed();
  await backend.serve(page);
  await page.goto(`/things/${install.entityId}`);
  await page.getByRole("button", { name: "写一个名字" }).click();
  await page.getByLabel("名字", { exact: true }).fill("我的 QQ");
  await page.getByLabel("名字", { exact: true }).press("Enter");
  await expect(page.getByRole("button", { name: /我的 QQ/ })).toBeVisible();
  await expect(page.getByText("用户本人 这样称呼它")).toBeVisible();
});
