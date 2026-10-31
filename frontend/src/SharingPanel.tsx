import { api, date, json } from "./api";
import type { Entry } from "./api";
import type { Action, Sharing } from "./types";

interface Props {
  selected: Entry;
  sharing: Sharing | undefined;
  shareLink: string;
  setShareLink: (link: string) => void;
  action: Action;
}
export function SharingPanel({
  selected,
  sharing,
  shareLink,
  setShareLink,
  action,
}: Props) {
  return (
    <div className="share-panel">
      <h3>People with access</h3>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          const f = new FormData(e.currentTarget);
          void action(
            () =>
              api(
                `/entries/${selected.id}/grants`,
                json("PUT", {
                  email: f.get("email"),
                  role: f.get("role"),
                }),
              ),
            "Access updated",
            false,
          );
        }}
      >
        <label>
          Email
          <input
            name="email"
            type="email"
            placeholder="Registered account email"
            required
          />
        </label>
        <label>
          Permission
          <select name="role">
            <option value="viewer">Viewer — download files</option>
            <option value="editor">Editor — upload and restore versions</option>
          </select>
        </label>
        <button>Grant access</button>
      </form>
      {sharing?.grants.map((g) => (
        <div className="share-row" key={g.email}>
          <span>
            {g.email}
            <small>{g.role}</small>
          </span>
          <button
            onClick={() =>
              action(
                () =>
                  api(
                    `/entries/${selected.id}/grants`,
                    json("PUT", { email: g.email, role: "remove" }),
                  ),
                "Access removed",
                false,
              )
            }
          >
            Remove
          </button>
        </div>
      ))}
      <h3>Download link</h3>
      <p className="muted small">
        Anyone with the link can download the current version until it expires
        or you revoke it.
      </p>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          const hours = Number(new FormData(e.currentTarget).get("hours"));
          void action(
            async () => {
              const result = await api<{ token: string }>(
                `/entries/${selected.id}/shares`,
                json("POST", { hours }),
              );
              setShareLink(`${location.origin}/share/${result.token}`);
            },
            "Link created",
            false,
          );
        }}
      >
        <label>
          Expires after
          <select name="hours">
            <option value="24">1 day</option>
            <option value="168">7 days</option>
            <option value="720">30 days</option>
          </select>
        </label>
        <button>Create link</button>
      </form>
      {shareLink && (
        <label>
          Share this link
          <input
            readOnly
            value={shareLink}
            onFocus={(e) => e.target.select()}
          />
          <button
            onClick={() =>
              action(
                () => navigator.clipboard.writeText(shareLink),
                "Link copied",
                false,
              )
            }
          >
            Copy link
          </button>
        </label>
      )}
      {sharing?.links.map((link) => (
        <div className="share-row" key={link.id}>
          <span>Expires {date(link.expires_at)}</span>
          <button
            onClick={() =>
              action(
                () =>
                  api(
                    `/entries/${selected.id}/shares/${link.id}`,
                    json("DELETE"),
                  ),
                "Link revoked",
                false,
              )
            }
          >
            Revoke
          </button>
        </div>
      ))}
      <small>
        Previously created link tokens are not stored. Create a new link if you
        lose one.
      </small>
    </div>
  );
}
