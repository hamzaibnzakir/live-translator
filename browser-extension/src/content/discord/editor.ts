// Putting text into Discord's Slate editor so that Slate's *internal model* changes, not just
// the DOM. This matters: Discord sends what is in the model. Measured against a real Slate
// editor (tests/e2e/fixtures/discord-app.tsx):
//   - execCommand('insertText'/'delete') edits the DOM natively and leaves the model stale
//     (DOM "Bonjour", model "Hello world") → would send the old draft. Never used.
//   - A synthetic paste event has no native default action, so the DOM only changes when
//     Slate applies it to its model (insertData) and re-renders. Reading the DOM back is
//     therefore a valid check that the model changed.
//   - Slate syncs its selection from the DOM on a throttled `selectionchange` (~100 ms), so
//     we wait before pasting or the paste lands at the old caret (append instead of replace).
import { readDraft } from './discordSelectors';

const sleep = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms));
const norm = (s: string): string => s.replace(/[﻿​]/g, '').replace(/\r/g, '').replace(/[ \t]+\n/g, '\n').trim();
/** Longer than Slate's selectionchange throttle. */
export const SELECTION_SYNC_MS = 160;

function selectAll(textbox: HTMLElement): void {
  textbox.focus();
  const sel = window.getSelection();
  if (!sel) return;
  const range = document.createRange();
  range.selectNodeContents(textbox);
  sel.removeAllRanges();
  sel.addRange(range);
}

function transfer(text: string): DataTransfer {
  const dt = new DataTransfer();
  dt.setData('text/plain', text);
  return dt;
}

function pasteEvent(textbox: HTMLElement, text: string): void {
  textbox.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer(text), bubbles: true, cancelable: true, composed: true }));
}

function beforeInputPaste(textbox: HTMLElement, text: string): void {
  textbox.dispatchEvent(new InputEvent('beforeinput', { inputType: 'insertFromPaste', dataTransfer: transfer(text), bubbles: true, cancelable: true, composed: true }));
}

async function waitFor(cond: () => boolean, ms: number): Promise<boolean> {
  const end = Date.now() + ms;
  while (Date.now() < end) { if (cond()) return true; await sleep(16); }
  return cond();
}

export type InsertMethod = 'paste' | 'beforeinput' | 'failed';

/** Replaces the whole draft with `text`. Returns which method worked (verified by reading back). */
export async function replaceDraft(textbox: HTMLElement, text: string): Promise<InsertMethod> {
  const want = norm(text);
  const ok = (): boolean => norm(readDraft(textbox)) === want;
  for (const [method, fire] of [['paste', pasteEvent], ['beforeinput', beforeInputPaste]] as const) {
    selectAll(textbox);
    await sleep(SELECTION_SYNC_MS);
    fire(textbox, text);
    if (await waitFor(ok, 400)) return method;
    // A partial result (e.g. appended) is repaired by the next attempt, which replaces everything.
  }
  return 'failed';
}
