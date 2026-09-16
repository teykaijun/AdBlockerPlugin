/** Request/response helper for extension pages talking to the service worker. */
export async function call(type, payload = {}) {
  const response = await chrome.runtime.sendMessage({ type, ...payload });
  if (!response?.ok) throw new Error(response?.error ?? `No response for "${type}"`);
  return response.result;
}
