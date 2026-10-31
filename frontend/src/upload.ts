import { sha256 } from "hash-wasm";
import { api, ApiError, json } from "./api";
import type { UploadStatus } from "./api";

export function fingerprint(
  file: File,
  signal: AbortSignal,
  progress: (read: number) => void,
): Promise<string> {
  return new Promise((resolve, reject) => {
    const worker = new Worker(new URL("./hash.worker.ts", import.meta.url), {
      type: "module",
    });
    const finish = () => {
      worker.terminate();
      signal.removeEventListener("abort", abort);
    };
    const abort = () => {
      finish();
      reject(new DOMException("Upload paused", "AbortError"));
    };
    signal.addEventListener("abort", abort, { once: true });
    worker.onmessage = ({ data }) => {
      if (data.error) {
        finish();
        reject(new Error(data.error));
      } else if (data.hash) {
        finish();
        resolve(data.hash);
      } else progress(data.progress);
    };
    worker.onerror = () => {
      finish();
      reject(new Error("Unable to calculate the file checksum."));
    };
    if (signal.aborted) abort();
    else worker.postMessage(file);
  });
}

async function retry(action: () => Promise<unknown>, signal: AbortSignal) {
  for (let attempt = 0; ; attempt++) {
    try {
      return await action();
    } catch (error) {
      if (
        signal.aborted ||
        attempt >= 3 ||
        (error instanceof ApiError &&
          error.status < 500 &&
          error.status !== 429)
      )
        throw error;
      await new Promise<void>((resolve, reject) => {
        const abort = () => {
          clearTimeout(timer);
          reject(new DOMException("Paused", "AbortError"));
        };
        const timer = setTimeout(
          () => {
            signal.removeEventListener("abort", abort);
            resolve();
          },
          500 * 2 ** attempt,
        );
        signal.addEventListener("abort", abort, { once: true });
      });
    }
  }
}

export async function sendParts(
  file: File,
  id: string,
  signal: AbortSignal,
  progress: (sent: number) => void,
) {
  const status = await api<UploadStatus>(`/uploads/${id}`, { signal });
  if (status.state === "complete") return { id: status.result_entry_id! };
  const received = new Set(status.parts.map((part) => part.number));
  let confirmed = status.parts.reduce((total, part) => total + part.size, 0);
  progress(confirmed);
  const remaining = Array.from(
    { length: Math.ceil(file.size / status.part_size) },
    (_, i) => i + 1,
  ).filter((n) => !received.has(n));
  let cursor = 0;
  let failure: unknown;
  await Promise.all(
    Array.from({ length: Math.min(3, remaining.length) }, async () => {
      while (!failure && cursor < remaining.length && !signal.aborted) {
        const number = remaining[cursor++];
        try {
          const chunk = file.slice(
            (number - 1) * status.part_size,
            number * status.part_size,
          );
          const digest = await sha256(
            new Uint8Array(await chunk.arrayBuffer()),
          );
          await retry(
            () =>
              api(`/uploads/${id}/parts/${number}`, {
                method: "PUT",
                body: chunk,
                signal,
                headers: {
                  "Content-Type": "application/octet-stream",
                  "X-Part-SHA256": digest,
                },
              }),
            signal,
          );
          confirmed += chunk.size;
          progress(confirmed);
        } catch (error) {
          failure = error;
        }
      }
    }),
  );
  if (failure) throw failure;
  if (signal.aborted) throw new DOMException("Paused", "AbortError");
  return await api<{ id: string }>(`/uploads/${id}/complete`, {
    ...json("POST"),
    signal,
  });
}
