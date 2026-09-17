/** The endpoint AdBlocker for Windows serves on this PC (windows/src/AdBlocker/Core/LocalApi.cs). */
export const API = 'http://127.0.0.1:45353/v1';

const TIMEOUT_MS = 1_500;

/** Fetches `API + path` as JSON. Rejects when AdBlocker isn't running or refuses the request. */
export async function get(path) {
  const response = await fetch(`${API}${path}`, { cache: 'no-store', signal: AbortSignal.timeout(TIMEOUT_MS) });
  if (!response.ok) throw new Error(`AdBlocker answered ${response.status}`);
  return response.json();
}
