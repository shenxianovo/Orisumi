import { Link } from "@tanstack/react-router";
import type { Observation } from "@/api/types";
import { Chip, Dot } from "@/components/ui";
import { useObservation, useSchemaRevision } from "@/hooks/queries";
import { tintOf } from "@/lib/categories";
import { UNREADABLE } from "@/lib/observers";
import { clock, parseTime } from "@/lib/time";

/** Observation 的类型名。 */
export function useObservationType(observation: Observation | undefined) {
  const schema = useSchemaRevision(observation?.schemaRevisionId);
  if (schema.data) return { name: schema.data.content.displayName, resourceName: schema.data.content.resourceName };
  return { name: schema.isError ? "Observation" : null, resourceName: null };
}

/** 指向一份 Observation 的标签，点击进入它的页面。放在别的链接里时用 plain，只显示不链接。 */
export function ObservationChip({
  observationId,
  strong,
  plain,
}: {
  observationId: string;
  strong?: boolean;
  plain?: boolean;
}) {
  const observation = useObservation(observationId);
  const type = useObservationType(observation.data);

  if (observation.isError) return <Chip dashed>{UNREADABLE}</Chip>;
  if (!observation.data) return <Chip className="text-fg-3">…</Chip>;

  const chip = (
    <Chip strong={!!strong}>
      <Dot color={tintOf(type.resourceName)} />
      {type.name ?? "…"}
      <span className="text-fg-3">{factTime(observation.data)}</span>
    </Chip>
  );
  if (plain) return chip;
  return (
    <Link to="/observations/$observationId" params={{ observationId }} className="hover:[&>span]:border-line-3">
      {chip}
    </Link>
  );
}

/** Observation 的事实时间，例如“14:00–16:00”；只知道一个边界时写这一刻。 */
function factTime(content: { startAt: string | null; endAt: string | null }): string {
  const start = parseTime(content.startAt);
  const end = parseTime(content.endAt);
  if (start !== null && end !== null) return `${clock(start)}–${clock(end)}`;
  const point = start ?? end;
  return point === null ? "时间未知" : clock(point);
}
