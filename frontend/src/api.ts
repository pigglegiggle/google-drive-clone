export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}

export async function api<T>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
  const response = await fetch("/api" + path, {
    ...options,
    headers: {
      "X-Drive-Request": "1",
      ...(options.body && typeof options.body === "string"
        ? { "Content-Type": "application/json" }
        : {}),
      ...options.headers,
    },
    credentials: "same-origin",
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new ApiError(
      response.status,
      problem.error || `Request failed (${response.status}).`,
    );
  }
  return response.status === 204 ? (undefined as T) : response.json();
}
export const json = (method: string, body?: unknown): RequestInit => ({
  method,
  ...(body === undefined ? {} : { body: JSON.stringify(body) }),
});
export const bytes = (value: number) => {
  if (value === 0) return "0 B";
  const unit = Math.min(Math.floor(Math.log(value) / Math.log(1024)), 3);
  return `${(value / 1024 ** unit).toFixed(unit ? 1 : 0)} ${["B", "KiB", "MiB", "GiB"][unit]}`;
};
export const date = (value: string) =>
  new Date(value).toLocaleString("en-GB", {
    dateStyle: "medium",
    timeStyle: "short",
  });

export interface Entry {
  id: string;
  name: string;
  kind: "file" | "folder";
  owner_id: string;
  owner_email: string;
  parent_id: string | null;
  role: "owner" | "viewer" | "editor";
  size: number;
  updated_at: string;
  version_id?: string;
  sha256?: string;
}
export interface Upload {
  id: string;
  name: string;
  size: number;
  sha256: string;
  state: string;
  entry_id: string | null;
  parent_id: string | null;
  result_entry_id: string | null;
  expires_at: string;
}
export interface UploadStatus extends Upload {
  part_size: number;
  parts: { number: number; size: number; sha256: string }[];
}
export interface User {
  id: string;
  email: string;
}
export interface Stats {
  quota: number;
  used: number;
  reserved: number;
}
export interface Version {
  id: string;
  size: number;
  sha256: string;
  created_at: string;
  created_by: string;
}
