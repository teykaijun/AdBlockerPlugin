/** The endpoint AdBlocker for Windows serves on this PC (windows/src/AdBlocker/Core/LocalApi.cs). */
export const API = 'http://127.0.0.1:45353/v1';

const TIMEOUT_MS = 1_500;

/** Whether `latest` ("1.4.1") is a higher version number than `current`. */
export function isNewerVersion(latest, current) {
  const parts = (version) => String(version).trim().replace(/^v/, '').split(/[.\-+]/).map((p) => Number.parseInt(p, 10) || 0);
  const a = parts(latest);
  const b = parts(current);
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const left = a[i] ?? 0;
    const right = b[i] ?? 0;
    if (left !== right) return left > right;
  }
  return false;
}

/** Fetches `API + path` as JSON. Rejects when AdBlocker isn't running or refuses the request. */
export async function get(path) {
  const response = await fetch(`${API}${path}`, { cache: 'no-store', signal: AbortSignal.timeout(TIMEOUT_MS) });
  if (!response.ok) throw new Error(`AdBlocker answered ${response.status}`);
  return response.json();
}
