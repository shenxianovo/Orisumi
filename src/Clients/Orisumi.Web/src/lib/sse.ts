export interface ServerEvent {
  event: string;
  data: string;
}

/** 把一段累积的文字切成完整的事件，返回事件和没读完的剩余部分。 */
export function splitServerEvents(buffer: string): { events: ServerEvent[]; rest: string } {
  const blocks = buffer.replaceAll("\r\n", "\n").split("\n\n");
  const rest = blocks.pop()!;
  const events = blocks.flatMap((block) => {
    let event = "message";
    const data: string[] = [];
    for (const line of block.split("\n")) {
      if (line.startsWith("event:")) event = line.slice(6).trim();
      else if (line.startsWith("data:")) data.push(line.slice(5).replace(/^ /, ""));
    }
    return data.length ? [{ event, data: data.join("\n") }] : [];
  });
  return { events, rest };
}

/** 逐个读出 Server-Sent Events。 */
export async function* parseServerEvents(body: ReadableStream<Uint8Array>): AsyncGenerator<ServerEvent> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  for (;;) {
    const { done, value } = await reader.read();
    if (done) return;
    const { events, rest } = splitServerEvents(buffer + decoder.decode(value, { stream: true }));
    buffer = rest;
    yield* events;
  }
}
