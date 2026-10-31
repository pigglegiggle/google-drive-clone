import { useEffect, useState } from "react";
import { api } from "./api";
import type { Entry } from "./api";

export function MoveItem({
  entry,
  done,
}: {
  entry: Entry;
  done: (parentId: string | null) => void;
}) {
  const [path, setPath] = useState<{ id: string; name: string }[]>([]);
  const [folders, setFolders] = useState<Entry[]>([]);
  const [error, setError] = useState("");
  const [offset, setOffset] = useState(0);
  const [more, setMore] = useState(false);
  const parent = path.at(-1)?.id ?? null;
  useEffect(() => {
    const c = new AbortController();
    api<Entry[]>(
      `/entries?offset=${offset}${parent ? "&parent=" + parent : ""}`,
      { signal: c.signal },
    )
      .then((items) => {
        setMore(items.length > 100);
        setFolders(
          items
            .slice(0, 100)
            .filter((item) => item.kind === "folder" && item.id !== entry.id),
        );
      })
      .catch((e) => {
        if (!c.signal.aborted) setError(e.message);
      });
    return () => c.abort();
  }, [parent, offset, entry.id]);
  return (
    <div>
      <p>Choose a destination folder.</p>
      <nav className="breadcrumbs">
        <button
          onClick={() => {
            setPath([]);
            setOffset(0);
          }}
        >
          My files
        </button>
        {path.map((p, i) => (
          <button
            key={p.id}
            onClick={() => {
              setPath(path.slice(0, i + 1));
              setOffset(0);
            }}
          >
            / {p.name}
          </button>
        ))}
      </nav>
      {error && <p role="alert">{error}</p>}
      <div className="folder-picker">
        {folders.map((f) => (
          <button
            key={f.id}
            onClick={() => {
              setPath([...path, f]);
              setOffset(0);
            }}
          >
            {f.name} /
          </button>
        ))}
        {!folders.length && <p className="muted">No folders on this page.</p>}
      </div>
      <div className="pagination">
        <button disabled={!offset} onClick={() => setOffset(offset - 100)}>
          Previous
        </button>
        <button disabled={!more} onClick={() => setOffset(offset + 100)}>
          Next
        </button>
      </div>
      <button className="primary" onClick={() => done(parent)}>
        Move here
      </button>
    </div>
  );
}
