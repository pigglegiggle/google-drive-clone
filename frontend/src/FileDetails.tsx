import { api, bytes, date, json } from "./api";
import type { Entry, Version } from "./api";
import type { Action, ModalKind } from "./types";

interface Props {
  selected: Entry;
  view: string;
  versions: Version[];
  setSelected: (entry: Entry | undefined) => void;
  setModal: (modal: ModalKind) => void;
  setShareLink: (link: string) => void;
  chooseUpload: (entry?: Entry) => void;
  action: Action;
}
export function FileDetails({
  selected,
  view,
  versions,
  setSelected,
  setModal,
  setShareLink,
  chooseUpload,
  action,
}: Props) {
  return (
    <aside className="details" aria-label="File details">
      <header>
        <h2>Details</h2>
        <button onClick={() => setSelected(undefined)}>Close</button>
      </header>
      <h3>{selected.name}</h3>
      <p className="muted">
        {selected.kind === "file"
          ? bytes(versions[0]?.size ?? selected.size)
          : "Folder"}{" "}
        · {selected.role}
      </p>
      {view === "trash" ? (
        <div className="actions">
          <button
            onClick={() =>
              action(async () => {
                await api(`/entries/${selected.id}/restore`, json("POST"));
                setSelected(undefined);
              }, "Item restored")
            }
          >
            Restore
          </button>
          <button className="danger" onClick={() => setModal("delete")}>
            Delete permanently
          </button>
        </div>
      ) : (
        <>
          <div className="actions">
            {selected.kind === "file" && (
              <a
                className="button primary"
                href={`/api/entries/${selected.id}/download`}
              >
                Download
              </a>
            )}
            {selected.kind === "file" && selected.role !== "viewer" && (
              <button onClick={() => chooseUpload(selected)}>
                Upload new version
              </button>
            )}
            {selected.role === "owner" && (
              <>
                <button onClick={() => setModal("rename")}>Rename</button>
                <button onClick={() => setModal("move")}>Move</button>
                {selected.kind === "file" && (
                  <button
                    onClick={() => {
                      setShareLink("");
                      setModal("share");
                    }}
                  >
                    Share
                  </button>
                )}
                <button
                  onClick={() =>
                    action(async () => {
                      await api(`/entries/${selected.id}/trash`, json("POST"));
                      setSelected(undefined);
                    }, "Moved to Trash")
                  }
                >
                  Move to Trash
                </button>
              </>
            )}
          </div>
          {selected.kind === "file" && (
            <>
              <h3>Version history</h3>
              <p className="muted small">
                Restoring makes that version current.
              </p>
              <ol className="versions">
                {versions.map((v, i) => (
                  <li key={v.id}>
                    <div>
                      <strong>
                        {i === 0 ? "Current version" : "Previous version"}
                      </strong>
                      <span>{date(v.created_at)}</span>
                      <span>
                        {bytes(v.size)} · {v.created_by}
                      </span>
                    </div>
                    <a
                      href={`/api/entries/${selected.id}/download?version=${v.id}`}
                    >
                      Download
                    </a>
                    {i > 0 && selected.role !== "viewer" && (
                      <button
                        onClick={() =>
                          action(
                            () =>
                              api(
                                `/entries/${selected.id}/versions/${v.id}/restore`,
                                json("POST"),
                              ),
                            "Version restored",
                          )
                        }
                      >
                        Restore version
                      </button>
                    )}
                    <details>
                      <summary>SHA-256</summary>
                      <code>{v.sha256}</code>
                    </details>
                  </li>
                ))}
              </ol>
            </>
          )}
        </>
      )}
    </aside>
  );
}
