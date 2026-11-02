import { describe, expect, it } from "vitest";
import { splitServerEvents } from "./sse";

describe("splitServerEvents", () => {
  it("切出完整的事件，没读完的留到下一次", () => {
    const { events, rest } = splitServerEvents('event: episode\ndata: {"title":"写 Web"}\n\nevent: sa');
    expect(events).toEqual([{ event: "episode", data: '{"title":"写 Web"}' }]);
    expect(rest).toBe("event: sa");
  });

  it("多行 data 用换行连起来，注释行不是事件", () => {
    const { events } = splitServerEvents(": keep-alive\n\ndata: 第一行\ndata: 第二行\n\n");
    expect(events).toEqual([{ event: "message", data: "第一行\n第二行" }]);
  });

  it("接受 CRLF 换行", () => {
    const { events, rest } = splitServerEvents('event: saved\r\ndata: {"observationIds":[]}\r\n\r\n');
    expect(events).toEqual([{ event: "saved", data: '{"observationIds":[]}' }]);
    expect(rest).toBe("");
  });
});
