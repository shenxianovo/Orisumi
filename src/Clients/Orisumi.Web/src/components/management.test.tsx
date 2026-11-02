import { afterEach, expect, test, vi } from "vitest";
import { render, screen, waitFor, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from "@tanstack/react-router";
import { HostsPage } from "@/routes/hosts";
import type { Action, Host, Instance, Operation, Package } from "@/api/management";

const host: Host = {
  hostId: "019e1bbb-ec00-7000-8000-000000000001",
  name: "离线设备",
  kind: "device",
  revoked: false,
  connected: false,
  lastSeenAt: "2026-01-01T00:00:00Z",
};
const initial: Instance = {
  thingId: "019e1bbb-ec00-7000-8000-000000000002",
  hostId: host.hostId,
  packageId: "example",
  name: "测试 Observer",
  enabled: true,
  settings: {},
  revision: 2,
  removed: false,
  reportedAt: "2026-01-01T00:00:00Z",
  status: { state: "running", enabled: true, appliedRevision: 1, pendingChanges: 0 },
};
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderHosts() {
  const query = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const router = createRouter({
    routeTree: createRootRoute({ component: HostsPage }),
    history: createMemoryHistory({ initialEntries: ["/"] }),
  });
  render(
    <QueryClientProvider client={query}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return query;
}

test("离线设备的旧运行报告显示未知，启用和设置仍可保存，在线操作不可用", async () => {
  let instance = structuredClone(initial);
  const writes: unknown[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === "/api/hosts") return Response.json({ hosts: [host] });
      if (path === "/api/observer-instances") return Response.json({ instances: [instance] });
      if (init?.method === "PATCH") {
        const body = JSON.parse(String(init.body)) as Partial<Instance>;
        writes.push(body);
        instance = { ...instance, ...body, revision: instance.revision + 1 };
        return Response.json(instance);
      }
      throw new Error(`Unexpected request: ${path}`);
    }),
  );
  const query = renderHosts();
  const user = userEvent.setup();
  await screen.findByRole("button", { name: /测试 Observer/ });
  expect(screen.getByText("状态未知")).toBeInTheDocument();
  expect(screen.getByText("等待同步")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "安装" })).toBeDisabled();
  await user.click(screen.getByRole("switch", { name: "启用 测试 Observer" }));
  await waitFor(() => expect(writes).toEqual([{ enabled: false }]));
  await user.click(screen.getByRole("button", { name: /测试 Observer/ }));
  expect(screen.getByRole("button", { name: "更新" })).toBeDisabled();
  expect(screen.getByText("设备连接后才能操作")).toBeInTheDocument();
  const settings = screen.getByLabelText("设置");
  await user.clear(settings);
  await user.paste("[1]");
  expect(screen.getByText("要写成一个 JSON 对象")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "保存" })).toBeDisabled();
  await user.clear(settings);
  await user.paste('{"intervalSeconds":30}');
  await user.click(screen.getByRole("button", { name: "保存" }));
  await waitFor(() => expect(writes).toEqual([{ enabled: false }, { settings: { intervalSeconds: 30 } }]));
  query.clear();
});

test("原实例已删除时可以新建实例，安装请求只带 action 和 packageId", async () => {
  const online: Host = { ...host, name: "在线设备", connected: true };
  const pkg: Package = {
    id: initial.packageId,
    name: "测试包",
    version: "1.0.0",
    compatible: true,
    installedVersion: null,
  };
  const operationsPath = `/api/hosts/${online.hostId}/operations`;
  const operations: Operation[] = [];
  const requests: unknown[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === "/api/hosts") return Response.json({ hosts: [online] });
      if (path === "/api/observer-instances") return Response.json({ instances: [{ ...initial, removed: true }] });
      if (path === operationsPath && init?.method === "POST") {
        const body = JSON.parse(String(init.body)) as { action: Action; packageId?: string };
        requests.push(body);
        const operation: Operation = {
          operationId: `operation-${operations.length}`,
          hostId: online.hostId,
          action: body.action,
          thingId: null,
          packageId: body.packageId ?? null,
          state: "succeeded",
          error: null,
          result: body.action === "catalog" ? { packages: [pkg] } : { thingId: "new-instance" },
        };
        operations.push(operation);
        return Response.json(operation, { status: 202 });
      }
      const operation = operations.find(({ operationId }) => path === `${operationsPath}/${operationId}`);
      if (operation) return Response.json(operation);
      throw new Error(`Unexpected request: ${path}`);
    }),
  );
  const query = renderHosts();
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "安装" }));
  await user.click(await screen.findByRole("button", { name: "新建实例" }));
  expect(await screen.findByText("已安装")).toBeInTheDocument();
  expect(requests).toEqual([{ action: "catalog" }, { action: "install", packageId: pkg.id }]);
  query.clear();
});
