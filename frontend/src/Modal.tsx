import { useEffect, useRef } from "react";
import type { ReactNode } from "react";

export function Modal({
  title,
  close,
  children,
}: {
  title: string;
  close: () => void;
  children: ReactNode;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    ref.current?.showModal();
  }, []);
  return (
    <dialog ref={ref} onCancel={close}>
      <header>
        <h2>{title}</h2>
        <button aria-label="Close dialog" onClick={close}>
          Close
        </button>
      </header>
      {children}
    </dialog>
  );
}
