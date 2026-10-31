import { useStack } from "./context";

/** 陈述后面的“凭什么”：从右边推出这份 Observation 的面板。 */
export function Why({ entityId }: { entityId: string }) {
  const stack = useStack();
  return (
    <button
      type="button"
      onClick={(event) => {
        event.stopPropagation();
        event.preventDefault();
        stack.open(entityId, -1);
      }}
      className="text-fg-3 hover:text-fg cursor-pointer underline-offset-2 hover:underline"
    >
      凭什么
    </button>
  );
}
