// EVERY Discord DOM assumption lives in this file. Discord's CSS class names are hashed
// (e.g. `messageContent_c19a55`) and change between builds, so we prefer ids, roles, aria-*
// and data-* attributes, and only use class *prefixes* ([class*="embedTitle"]) as a fallback.
// If Discord changes its markup, this is the only file that should need fixing.

/** One chat row: <li id="chat-messages-<channelId>-<messageId>" class="messageListItem_…">. */
export const MESSAGE_ITEM = 'li[id^="chat-messages-"]';
/** The scrollable message list (channel, thread sidebar, forum post). */
export const MESSAGE_LIST = '[data-list-id="chat-messages"], ol[role="list"][aria-label]';
/** Main message body: <div id="message-content-<messageId>" class="markup_… messageContent_…">. */
export const MESSAGE_CONTENT = '[id^="message-content-"]';
/** The quoted line above a reply. */
export const REPLY_CONTEXT = '[id^="message-reply-context-"]';
export const REPLY_TEXT = '[class*="repliedTextContent"]';
/** Embed text parts (link previews, bot embeds). */
export const EMBED_TEXT = [
  '[class*="embedAuthorName"]', '[class*="embedTitle"]', '[class*="embedDescription"]',
  '[class*="embedFieldName"]', '[class*="embedFieldValue"]', '[class*="embedFooterText"]',
].join(', ');
/** Forum channel post cards (title + first message preview). */
export const FORUM_TEXT = '[data-item-id] [class*="postTitleText"], [data-item-id] [class*="messageContent"]';
/** Slate editor of the message box. */
export const COMPOSER_TEXTBOX = '[role="textbox"][data-slate-editor="true"], div[role="textbox"][contenteditable="true"]';
/** Visual container of the message box (used only to position our button). */
export const COMPOSER_CONTAINER = '[class*="channelTextArea"], form';

/**
 * Elements whose content we never touch. Matching is done on elements inside message text.
 * Mentions (users, roles, channels), emoji (unicode emoji are <img class="emoji">), links,
 * code, spoilers, timestamps, the "(edited)" marker and anything interactive.
 */
export const PROTECTED = [
  'code', 'pre', 'kbd', 'a', 'img', 'svg', 'time', 'button', 'input', 'textarea', '[contenteditable="true"]',
  '.mention', '[class*="mention"]', '[class*="roleMention"]', '[class*="channelMention"]',
  '.emoji', '[data-type="emoji"]', '[class*="emojiContainer"]', '[class*="emoji_"]',
  '[class*="spoiler"]', '[aria-label="Spoiler"]',
  '[class*="timestamp"]', '[class*="edited"]', '[class*="codeContainer"]', '[class*="codeBlock"]',
  '[class*="inlineCode"]', '[data-bb-skip]', '[translate="no"]',
].join(', ');

export function isProtected(el: Element): boolean {
  return el.matches(PROTECTED);
}

/** "chat-messages-<channel>-<message>" → { channelId, messageId }. */
export function parseMessageItemId(id: string): { channelId: string; messageId: string } | null {
  const m = /^chat-messages-(\d+|[\w-]+?)-(\d+)$/.exec(id);
  return m ? { channelId: m[1]!, messageId: m[2]! } : null;
}

/** /channels/<guild|@me>/<channel>[/threads/<thread>] → ids. */
export function parseRoute(pathname: string): { guildId: string; channelId: string } | null {
  const m = /^\/channels\/(@me|\d+)\/(\d+)(?:\/threads\/(\d+))?/.exec(pathname);
  return m ? { guildId: m[1]!, channelId: m[3] ?? m[2]! } : null;
}

export interface Block { el: Element; key: string; kind: 'content' | 'reply' | 'embed' | 'forum' }

/** Translatable text blocks of one message row, each with a stable key. */
export function messageBlocks(item: Element): Block[] {
  const ids = parseMessageItemId(item.id);
  if (!ids) return [];
  const out: Block[] = [];
  item.querySelectorAll(MESSAGE_CONTENT).forEach((el) => {
    if (el.closest(REPLY_CONTEXT)) return;
    out.push({ el, key: `${ids.messageId}:content`, kind: 'content' });
  });
  item.querySelectorAll(`${REPLY_CONTEXT} ${REPLY_TEXT}, ${REPLY_CONTEXT} ${MESSAGE_CONTENT}`).forEach((el, i) => {
    if (out.some((b) => b.el === el || b.el.contains(el))) return;
    out.push({ el, key: `${ids.messageId}:reply:${i}`, kind: 'reply' });
  });
  item.querySelectorAll(EMBED_TEXT).forEach((el, i) => {
    if (el.closest(MESSAGE_CONTENT) && !el.closest('article')) return;
    // Skip nested matches (a description inside a matching wrapper).
    if (el.parentElement?.closest(EMBED_TEXT)) return;
    out.push({ el, key: `${ids.messageId}:embed:${i}`, kind: 'embed' });
  });
  return out;
}

export function forumBlocks(root: ParentNode): Block[] {
  const out: Block[] = [];
  root.querySelectorAll(FORUM_TEXT).forEach((el, i) => {
    const card = el.closest('[data-item-id]');
    out.push({ el, key: `forum:${card?.getAttribute('data-item-id') ?? i}:${i}`, kind: 'forum' });
  });
  return out;
}

export function findComposer(root: ParentNode = document): HTMLElement | null {
  return root.querySelector<HTMLElement>(COMPOSER_TEXTBOX);
}

/** Plain text of a Slate draft, one line per paragraph, without Slate's zero-width placeholders. */
export function readDraft(textbox: HTMLElement): string {
  const lines = textbox.querySelectorAll('[data-slate-node="element"]');
  const clean = (s: string): string => s.replace(/[﻿​]/g, '');
  if (lines.length) return [...lines].map((l) => clean(l.textContent ?? '')).join('\n');
  return clean(textbox.innerText ?? textbox.textContent ?? '');
}

const scrollerCache = new WeakMap<Element, Element | null>();
/** The scroll container of a message list (IntersectionObserver root, so rootMargin works). */
export function findScroller(item: Element): Element | null {
  const list = item.closest(MESSAGE_LIST) ?? item.parentElement;
  if (!list) return null;
  if (scrollerCache.has(list)) return scrollerCache.get(list) ?? null;
  let el: Element | null = list;
  let found: Element | null = null;
  while (el && el !== document.body) {
    const oy = getComputedStyle(el).overflowY;
    if (oy === 'auto' || oy === 'scroll' || /scroller/i.test(el.className.toString())) { found = el; break; }
    el = el.parentElement;
  }
  scrollerCache.set(list, found);
  return found;
}
