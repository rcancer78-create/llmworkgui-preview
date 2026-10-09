// Another application's saved login is not an authentication interface.
// Retain the entry point fail-closed until a documented public integration exists.
export function loadSession() {
  throw new Error('Grok Bot integration is disabled. Use Grok through the official Cursor client.');
}
