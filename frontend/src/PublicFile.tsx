import { useEffect, useState } from "react";
import { api, bytes, date } from "./api";

export function PublicFile({ token }: { token: string }) {
  const [file, setFile] = useState<{
    name: string;
    size: number;
    sha256: string;
    expires_at: string;
  }>();
  const [error, setError] = useState("");
  useEffect(() => {
    api<typeof file>(`/public/${token}`)
      .then(setFile)
      .catch((e) => setError(e.message));
  }, [token]);
  return (
    <main className="auth">
      <div className="brand">
        Drive<span>Shared file</span>
      </div>
      {error ? (
        <p role="alert">This link is unavailable, expired, or revoked.</p>
      ) : file ? (
        <>
          <h1>{file.name}</h1>
          <p>
            {bytes(file.size)} · Link expires {date(file.expires_at)}
          </p>
          <a className="button primary" href={`/api/public/${token}/download`}>
            Download file
          </a>
          <details>
            <summary>File checksum</summary>
            <code>{file.sha256}</code>
          </details>
        </>
      ) : (
        <p>Loading shared file…</p>
      )}
      <p>
        <a href="/">Open Drive</a>
      </p>
    </main>
  );
}
