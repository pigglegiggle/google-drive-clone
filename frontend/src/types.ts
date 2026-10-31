export interface Transfer {
  key: string;
  id?: string;
  name: string;
  size: number;
  sent: number;
  label: string;
  active: boolean;
  controller: AbortController;
}
export interface Sharing {
  links: { id: string; expires_at: string }[];
  grants: { email: string; role: string }[];
}

export type ModalKind =
  | "folder"
  | "rename"
  | "move"
  | "share"
  | "delete"
  | null;
export type Action = (
  run: () => Promise<unknown>,
  message: string,
  close?: boolean,
) => Promise<void>;
