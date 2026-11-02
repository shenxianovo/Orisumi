import { useEffect, useRef, useState, type ReactNode } from "react";
import { Button } from "@/components/ui";

/** 删除确认：children 写明会一起移除什么。 */
export function DeleteDialog({
  open,
  title,
  onClose,
  onConfirm,
  busy,
  error,
  children,
}: {
  open: boolean;
  title: string;
  onClose: () => void;
  onConfirm: () => void;
  busy: boolean;
  error: string | null;
  children: ReactNode;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [shown, setShown] = useState(false);

  useEffect(() => {
    const element = dialog.current;
    if (!element) return;
    if (open && !element.open) {
      element.showModal?.();
      setShown(true);
    } else if (!open && element.open) {
      element.close();
      setShown(false);
    }
  }, [open]);

  return (
    <dialog
      ref={dialog}
      onClose={onClose}
      aria-labelledby="delete-title"
      className="bg-card text-fg border-line m-auto w-[min(420px,calc(100%-32px))] rounded-xl border p-5"
    >
      {shown && (
        <>
          <h2 id="delete-title" className="text-[15px] font-medium">
            {title}
          </h2>
          <div className="text-fg-2 mt-2 text-[13px]">{children}</div>
          {error && (
            <p role="alert" className="text-danger mt-2 text-xs">
              {error}
            </p>
          )}
          <div className="mt-4 flex justify-end gap-2">
            <Button onClick={onClose}>取消</Button>
            <Button danger onClick={onConfirm} disabled={busy}>
              删除
            </Button>
          </div>
        </>
      )}
    </dialog>
  );
}
