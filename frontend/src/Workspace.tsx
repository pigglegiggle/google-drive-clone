import { useEffect, useRef, useState } from "react";
import { api, ApiError, bytes, date, json } from "./api";
import type { Entry, Stats, Upload, User, Version } from "./api";
import type { Transfer, Sharing, ModalKind } from "./types";
import { fingerprint, sendParts } from "./upload";
import { Modal } from "./Modal";
import { MoveItem } from "./MoveItem";
import { FileDetails } from "./FileDetails";
import { SharingPanel } from "./SharingPanel";

export function Workspace({
  user,
  logout,
}: {
  user: User;
  logout: () => void;
}) {
  const [entries, setEntries] = useState<Entry[]>([]);
  const [stats, setStats] = useState<Stats>();
  const [pending, setPending] = useState<Upload[]>([]);
  const [view, setView] = useState("files");
  const [path, setPath] = useState<{ id: string; name: string }[]>([]);
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [revision, setRevision] = useState(0);
  const [selected, setSelected] = useState<Entry>();
  const [versions, setVersions] = useState<Version[]>([]);
  const [sharing, setSharing] = useState<Sharing>();
  const [modal, setModal] = useState<
    "folder" | "rename" | "move" | "share" | "delete" | null
  >(null);
  const [shareLink, setShareLink] = useState("");
  const [transfers, setTransfers] = useState<Transfer[]>([]);
  const controls = useRef(new Set<AbortController>());
  const alive = useRef(true);
  const resume = useRef<Upload | null>(null);
  const target = useRef<Entry | null>(null);
  const picker = useRef<HTMLInputElement>(null);
  const parent = path.at(-1)?.id ?? null;
  const refresh = () => setRevision((x) => x + 1);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
      controls.current.forEach((c) => c.abort());
    };
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError("");
    const params = new URLSearchParams({
      view,
      search: query,
      offset: String(offset),
    });
    if (parent) params.set("parent", parent);
    Promise.all([
      api<Entry[]>("/entries?" + params, { signal: controller.signal }),
      api<Stats>("/stats", { signal: controller.signal }),
      api<Upload[]>("/uploads", { signal: controller.signal }),
    ])
      .then(([items, usage, uploads]) => {
        setEntries(items);
        setStats(usage);
        setPending(uploads);
      })
      .catch((e) => {
        if (!controller.signal.aborted) {
          if (e instanceof ApiError && e.status === 401) logout();
          else setError(e.message);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [view, parent, query, offset, revision]);
  useEffect(() => {
    setVersions([]);
    setSharing(undefined);
    if (!selected || selected.kind !== "file" || view === "trash") return;
    const controller = new AbortController();
    api<Version[]>(`/entries/${selected.id}/versions`, {
      signal: controller.signal,
    })
      .then(setVersions)
      .catch((e) => {
        if (!controller.signal.aborted) setError(e.message);
      });
    if (selected.role === "owner")
      api<Sharing>(`/entries/${selected.id}/shares`, {
        signal: controller.signal,
      })
        .then(setSharing)
        .catch((e) => {
          if (!controller.signal.aborted) setError(e.message);
        });
    return () => controller.abort();
  }, [selected?.id, revision, view]);

  useEffect(() => {
    setShareLink("");
  }, [selected?.id]);

  async function action(
    run: () => Promise<unknown>,
    message: string,
    close = true,
  ) {
    setError("");
    try {
      await run();
      setNotice(message);
      refresh();
      if (close) setModal(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }
  function navigate(nextView: string) {
    setView(nextView);
    setPath([]);
    setSearch("");
    setQuery("");
    setOffset(0);
    setSelected(undefined);
  }
  function open(entry: Entry) {
    if (entry.kind === "folder" && view !== "trash") {
      setPath([...path, { id: entry.id, name: entry.name }]);
      setSearch("");
      setQuery("");
      setOffset(0);
      setSelected(undefined);
    } else setSelected(entry);
  }
  function chooseUpload(entry?: Entry, upload?: Upload) {
    target.current = entry ?? null;
    resume.current = upload ?? null;
    if (picker.current) {
      picker.current.multiple = !entry && !upload;
      picker.current.click();
    }
  }
  async function uploadFiles(files: File[]) {
    const existing = resume.current;
    const destination = target.current;
    resume.current = null;
    target.current = null;
    for (const file of files) {
      if (!alive.current) return;
      const controller = new AbortController();
      controls.current.add(controller);
      const key = crypto.randomUUID();
      const update = (patch: Partial<Transfer>) =>
        setTransfers((all) =>
          all.map((t) => (t.key === key ? { ...t, ...patch } : t)),
        );
      setTransfers((all) => [
        ...all,
        {
          key,
          id: existing?.id,
          name: file.name,
          size: file.size,
          sent: 0,
          label: "Checking file…",
          active: true,
          controller,
        },
      ]);
      try {
        const digest = await fingerprint(file, controller.signal, (read) =>
          update({
            label: `Checking file · ${bytes(read)} / ${bytes(file.size)}`,
          }),
        );
        if (
          existing &&
          (file.size !== existing.size || digest !== existing.sha256)
        )
          throw new Error(
            "Select the original file: its size and SHA-256 must match the paused upload.",
          );
        const started =
          existing ??
          (await api<{ id: string }>("/uploads", {
            ...json("POST", {
              name: file.name,
              size: file.size,
              sha256: digest,
              contentType: file.type || "application/octet-stream",
              parentId: parent,
              entryId: destination?.id,
            }),
            signal: controller.signal,
          }));
        update({ id: started.id, label: "Uploading…" });
        await sendParts(file, started.id, controller.signal, (sent) =>
          update({
            sent,
            label: sent === file.size ? "Verifying file…" : "Uploading…",
          }),
        );
        update({ sent: file.size, label: "Complete", active: false });
        setNotice(`${file.name} uploaded and verified.`);
      } catch (e) {
        update({
          label: controller.signal.aborted
            ? "Paused — select the file below to resume"
            : (e as Error).message,
          active: false,
        });
      } finally {
        controls.current.delete(controller);
        refresh();
      }
    }
  }

  const activeIds = new Set(transfers.filter((t) => t.active).map((t) => t.id));
  const selectedId = selected?.id;
  return (
    <div className="workspace">
      <aside className="sidebar">
        <div className="brand">
          Drive<span>File storage</span>
        </div>
        <button className="primary upload-main" onClick={() => chooseUpload()}>
          Upload files
        </button>
        <nav aria-label="Main navigation">
          {[
            ["files", "My files"],
            ["shared", "Shared with me"],
            ["trash", "Trash"],
          ].map(([key, label]) => (
            <button
              key={key}
              className={view === key ? "active" : ""}
              onClick={() => navigate(key)}
            >
              {label}
            </button>
          ))}
        </nav>
        <div className="storage">
          <strong>Storage</strong>
          <progress
            aria-label="Storage usage"
            value={(stats?.used ?? 0) + (stats?.reserved ?? 0)}
            max={stats?.quota ?? 1}
          />
          <p>
            {bytes(stats?.used ?? 0)} of {bytes(stats?.quota ?? 0)}
          </p>
          {!!stats?.reserved && (
            <small>{bytes(stats.reserved)} reserved by uploads</small>
          )}
          <small>File versions and Trash count toward storage.</small>
        </div>
        <div className="account">
          <span title={user.email}>{user.email}</span>
          <button
            className="link"
            onClick={() =>
              action(async () => {
                await api("/auth/logout", json("POST"));
                logout();
              }, "Signed out")
            }
          >
            Sign out
          </button>
        </div>
      </aside>
      <main className="content">
        <header className="topbar">
          <form
            className="search"
            onSubmit={(e) => {
              e.preventDefault();
              setQuery(search);
              setOffset(0);
            }}
          >
            <input
              aria-label="Search your files"
              placeholder="Search your files"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              maxLength={180}
            />
            <button disabled={view !== "files"}>Search</button>
          </form>
          <button onClick={refresh} disabled={loading}>
            Refresh
          </button>
        </header>
        <div className="page-heading">
          <div>
            <h1>
              {view === "shared"
                ? "Shared with me"
                : view === "trash"
                  ? "Trash"
                  : query
                    ? `Results for “${query}”`
                    : (path.at(-1)?.name ?? "My files")}
            </h1>
            <p className="muted">
              {view === "trash"
                ? "Restore items or delete them permanently."
                : view === "shared"
                  ? "Files other people have shared with your account."
                  : "Keep your files organized and share them when you need to."}
            </p>
          </div>
          {view === "files" && (
            <button onClick={() => setModal("folder")}>New folder</button>
          )}
        </div>
        {view === "files" && (
          <nav className="breadcrumbs" aria-label="Folder path">
            <button
              onClick={() => {
                setPath([]);
                setQuery("");
                setSearch("");
                setOffset(0);
              }}
            >
              My files
            </button>
            {path.map((part, i) => (
              <span key={part.id}>
                {" "}
                /{" "}
                <button
                  onClick={() => {
                    setPath(path.slice(0, i + 1));
                    setOffset(0);
                  }}
                >
                  {part.name}
                </button>
              </span>
            ))}
          </nav>
        )}
        {error && (
          <div role="alert" className="message error">
            {error}
            <button onClick={() => setError("")} aria-label="Dismiss error">
              Dismiss
            </button>
          </div>
        )}
        {notice && (
          <div role="status" className="message">
            {notice}
            <button onClick={() => setNotice("")}>Dismiss</button>
          </div>
        )}
        <div className={"file-layout" + (selected ? " with-details" : "")}>
          <section aria-label="Files">
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Size</th>
                    <th>Modified</th>
                    <th>Access</th>
                  </tr>
                </thead>
                <tbody>
                  {loading ? (
                    <tr>
                      <td colSpan={4} className="empty">
                        Loading files…
                      </td>
                    </tr>
                  ) : entries.length ? (
                    entries.slice(0, 100).map((entry) => (
                      <tr
                        key={entry.id}
                        className={selectedId === entry.id ? "selected" : ""}
                      >
                        <td>
                          <button
                            className="filename"
                            aria-label={entry.name}
                            onClick={() => open(entry)}
                          >
                            <span className={"file-mark " + entry.kind}>
                              {entry.kind === "folder" ? (
                                <svg viewBox="0 0 24 24" aria-hidden="true">
                                  <path d="M3 6h7l2 2h9v12H3z" />
                                </svg>
                              ) : (
                                entry.name
                                  .split(".")
                                  .at(-1)
                                  ?.slice(0, 3)
                                  .toUpperCase() || "FILE"
                              )}
                            </span>
                            <span>{entry.name}</span>
                          </button>
                          {entry.kind === "folder" && view !== "trash" && (
                            <button
                              className="folder-options link"
                              aria-label={`Details for ${entry.name}`}
                              onClick={() => setSelected(entry)}
                            >
                              Details
                            </button>
                          )}
                        </td>
                        <td>
                          {entry.kind === "folder" ? "—" : bytes(entry.size)}
                        </td>
                        <td>{date(entry.updated_at)}</td>
                        <td>
                          <span className="badge">{entry.role}</span>
                        </td>
                      </tr>
                    ))
                  ) : (
                    <tr>
                      <td colSpan={4} className="empty">
                        <strong>
                          {query
                            ? "No matching files"
                            : view === "trash"
                              ? "Trash is empty"
                              : view === "shared"
                                ? "No shared files yet"
                                : "This folder is empty"}
                        </strong>
                        <p>
                          {view === "files" && !query
                            ? "Upload a file or create a folder to get started."
                            : "Your files will appear here."}
                        </p>
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
            <div className="pagination">
              <span>
                {offset + (entries.length ? 1 : 0)}–
                {offset + Math.min(entries.length, 100)}
              </span>
              <button
                disabled={!offset || loading}
                onClick={() => setOffset(Math.max(0, offset - 100))}
              >
                Previous
              </button>
              <button
                disabled={entries.length <= 100 || loading}
                onClick={() => setOffset(offset + 100)}
              >
                Next
              </button>
            </div>
          </section>
          {selected && (
            <FileDetails
              selected={selected}
              view={view}
              versions={versions}
              setSelected={setSelected}
              setModal={setModal}
              setShareLink={setShareLink}
              chooseUpload={chooseUpload}
              action={action}
            />
          )}
        </div>
        {(pending.length > 0 || transfers.length > 0) && (
          <section className="uploads">
            <header>
              <h2>Uploads</h2>
              <span className="muted small">
                Up to 5 GiB per file · 3 parallel parts per file
              </span>
            </header>
            {transfers.map((t) => (
              <div className="transfer" key={t.key}>
                <div>
                  <strong>{t.name}</strong>
                  <span>{t.label}</span>
                  <progress
                    aria-label={`Upload progress for ${t.name}`}
                    value={t.sent}
                    max={t.size || 1}
                  />
                </div>
                <span>
                  {bytes(t.sent)} / {bytes(t.size)}
                </span>
                {t.active ? (
                  <button onClick={() => t.controller.abort()}>Pause</button>
                ) : (
                  <button
                    onClick={() =>
                      setTransfers((all) =>
                        all.filter((item) => item.key !== t.key),
                      )
                    }
                  >
                    Dismiss
                  </button>
                )}
              </div>
            ))}
            {pending
              .filter((u) => !activeIds.has(u.id))
              .map((u) => (
                <div className="transfer" key={u.id}>
                  <div>
                    <strong>{u.name}</strong>
                    <span>
                      {u.state === "failed"
                        ? "Verification failed — cancel and upload again"
                        : "Select the original file to resume"}{" "}
                      · {bytes(u.size)}
                    </span>
                    <small>Expires {date(u.expires_at)}</small>
                  </div>
                  {u.state === "active" && (
                    <button onClick={() => chooseUpload(undefined, u)}>
                      Resume upload
                    </button>
                  )}
                  <button
                    onClick={() =>
                      action(
                        () => api(`/uploads/${u.id}`, json("DELETE")),
                        "Upload cancelled",
                      )
                    }
                  >
                    Cancel upload
                  </button>
                </div>
              ))}
          </section>
        )}
        <input
          ref={picker}
          className="visually-hidden"
          type="file"
          aria-label="Choose files to upload"
          onChange={(e) => {
            const files = Array.from(e.target.files ?? []);
            e.target.value = "";
            void uploadFiles(files);
          }}
        />
      </main>
      {modal && (
        <Modal
          title={
            modal === "folder"
              ? "New folder"
              : modal === "rename"
                ? "Rename item"
                : modal === "move"
                  ? "Move item"
                  : modal === "delete"
                    ? "Delete permanently?"
                    : "Share file"
          }
          close={() => setModal(null)}
        >
          {error && (
            <p role="alert" className="error">
              {error}
            </p>
          )}
          {(modal === "folder" || modal === "rename") && (
            <form
              onSubmit={(e) => {
                e.preventDefault();
                const name = new FormData(e.currentTarget).get("name");
                void action(
                  async () => {
                    if (modal === "folder")
                      await api(
                        "/folders",
                        json("POST", { name, parentId: parent }),
                      );
                    else {
                      await api(
                        `/entries/${selected!.id}`,
                        json("PATCH", { name, parentId: selected!.parent_id }),
                      );
                      setSelected(undefined);
                    }
                  },
                  modal === "folder" ? "Folder created" : "Item renamed",
                );
              }}
            >
              <label>
                Name
                <input
                  name="name"
                  required
                  maxLength={180}
                  autoFocus
                  defaultValue={modal === "rename" ? selected?.name : ""}
                />
              </label>
              <button className="primary">Save</button>
            </form>
          )}
          {modal === "move" && selected && (
            <MoveItem
              entry={selected}
              done={(parentId) =>
                action(async () => {
                  await api(
                    `/entries/${selected.id}`,
                    json("PATCH", { name: selected.name, parentId }),
                  );
                  setSelected(undefined);
                }, "Item moved")
              }
            />
          )}
          {modal === "delete" && (
            <>
              <p>
                This permanently deletes <strong>{selected?.name}</strong>
                {selected?.kind === "folder"
                  ? " and all its contents"
                  : " and all its versions"}
                . This cannot be undone.
              </p>
              <button
                className="danger"
                onClick={() =>
                  action(async () => {
                    await api(`/entries/${selected!.id}`, json("DELETE"));
                    setSelected(undefined);
                  }, "Item deleted permanently")
                }
              >
                Delete permanently
              </button>
            </>
          )}
          {modal === "share" && selected && (
            <SharingPanel
              selected={selected}
              sharing={sharing}
              shareLink={shareLink}
              setShareLink={setShareLink}
              action={action}
            />
          )}
        </Modal>
      )}
    </div>
  );
}
