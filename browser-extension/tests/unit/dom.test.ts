import { beforeEach, describe, expect, it } from 'vitest';
import { blockText, collectSegments, matchLeadingCase, writeSegment } from '../../src/shared/segments';
import { TextRewriter } from '../../src/content/common/rewriter';
import { isProtected, messageBlocks, parseMessageItemId, parseRoute, readDraft } from '../../src/content/discord/discordSelectors';
import { isSkippedElement } from '../../src/content/page/pageTranslator';

const DISCORD_ROW = `
<li id="chat-messages-222-900002" class="messageListItem__5126c">
  <div role="article" data-list-item-id="chat-messages___chat-messages-222-900002">
    <div id="message-reply-context-900002" class="repliedMessage_c19a55"><span class="username_c19a55">@Camille</span>
      <div class="repliedTextPreview_c19a55"><div class="repliedTextContent_c19a55 markup__75297" id="message-content-900001">Bonjour à tous</div></div></div>
    <h3><span class="username_c19a55">Hugo</span> <span class="timestamp_c19a55"><time>Today at 20:41</time></span></h3>
    <div id="message-content-900002" class="markup__75297 messageContent_c19a55">Salut <span class="mention wrapper_f61d60" role="button">@David</span>, tu viens <strong>ce soir</strong> ? <span class="emojiContainer_bae8cb"><img class="emoji" alt="😀"></span> <a href="https://x.y">https://x.y</a> lance <code class="inlineCode_e50d7e">npm i</code> <span class="spoilerContent_aa9639" aria-label="Spoiler"><span>secret</span></span><span class="timestamp_c19a55 timestampInline_c19a55"><time> (edited)</time></span></div>
  </div>
  <article class="embed__623de"><div class="embedTitle__623de">Titre</div><div class="embedDescription__623de">Une description</div></article>
</li>`;

describe('Discord selectors', () => {
  beforeEach(() => { document.body.innerHTML = `<ol data-list-id="chat-messages">${DISCORD_ROW}</ol>`; });

  it('parses ids and routes', () => {
    expect(parseMessageItemId('chat-messages-222-900002')).toEqual({ channelId: '222', messageId: '900002' });
    expect(parseMessageItemId('something-else')).toBeNull();
    expect(parseRoute('/channels/111/222')).toEqual({ guildId: '111', channelId: '222' });
    expect(parseRoute('/channels/@me/333')).toEqual({ guildId: '@me', channelId: '333' });
    expect(parseRoute('/channels/111/222/threads/444')).toEqual({ guildId: '111', channelId: '444' });
    expect(parseRoute('/store')).toBeNull();
  });

  it('finds content, reply preview and embed blocks with stable keys', () => {
    const blocks = messageBlocks(document.querySelector('li')!);
    expect(blocks.map((b) => `${b.key}|${b.kind}`)).toEqual(['900002:content|content', '900002:reply:0|reply', '900002:embed:0|embed', '900002:embed:1|embed']);
  });

  it('segments skip mentions/emoji/links/code/spoilers/timestamps and split around them', () => {
    const el = document.getElementById('message-content-900002')!;
    const segs = collectSegments(el, { skip: isProtected });
    expect(segs.map((s) => s.text)).toEqual(['Salut ', ', tu viens ce soir ? ', ' lance ']);
    expect(segs[1]!.nodes).toHaveLength(3); // "tu viens", <strong>ce soir</strong>, " ?" stay one sentence
    expect(blockText(segs)).toBe('Salut\n, tu viens ce soir ?\nlance');
  });

  it('writing only changes Text.data: element structure and protected nodes are identical', () => {
    const el = document.getElementById('message-content-900002')!;
    const protectedBefore = [...el.querySelectorAll('.mention, img, a, code, [aria-label=Spoiler], time')].map((e) => e.outerHTML);
    const elementsBefore = [...el.querySelectorAll('*')];
    const rw = new TextRewriter();
    const segs = collectSegments(el, { skip: isProtected });
    writeSegment(segs[0]!, 'Hi', (n, v) => rw.write(n, v));
    writeSegment(segs[1]!, 'Are you coming tonight?', (n, v) => rw.write(n, v));
    expect(el.textContent).toContain('Hi @David, are you coming tonight?');
    expect([...el.querySelectorAll('*')]).toEqual(elementsBefore);            // same element objects
    expect([...el.querySelectorAll('.mention, img, a, code, [aria-label=Spoiler], time')].map((e) => e.outerHTML)).toEqual(protectedBefore);
    // Re-walking a translated block recovers the original text.
    const again = collectSegments(el, { skip: isProtected, originalOf: (n) => rw.originalOf(n) });
    expect(again.map((s) => s.text)).toEqual(['Salut ', ', tu viens ce soir ? ', ' lance ']);
    expect(rw.restoreAll()).toBeGreaterThan(0);
    expect(el.textContent).toContain('Salut @David, tu viens ce soir ?');
  });

  it('detects edits: a node changed by the app is no longer ours', () => {
    const el = document.getElementById('message-content-900001')!;
    const rw = new TextRewriter();
    const [seg] = collectSegments(el, { skip: isProtected });
    writeSegment(seg!, 'Hello everyone', (n, v) => rw.write(n, v));
    const node = seg!.nodes[0]!;
    expect(rw.isOurs(node)).toBe(true);
    node.data = 'Bonsoir à tous';                                              // React applies an edit
    expect(rw.isOurs(node)).toBe(false);
    expect(collectSegments(el, { skip: isProtected, originalOf: (n) => rw.originalOf(n) })[0]!.text).toBe('Bonsoir à tous');
  });

  it('reads a Slate draft without zero-width placeholders', () => {
    document.body.innerHTML = '<div role="textbox" data-slate-editor="true"><div data-slate-node="element"><span data-slate-string="true">Hello</span></div><div data-slate-node="element"><span data-slate-zero-width="n">﻿</span></div><div data-slate-node="element">world</div></div>';
    expect(readDraft(document.querySelector('[role=textbox]')!)).toBe('Hello\n\nworld');
  });
});

describe('page walker skip rules', () => {
  it('skips code, inputs, editors, translate=no, notranslate, our overlay', () => {
    document.body.innerHTML = '<pre></pre><code></code><textarea></textarea><input><div contenteditable="true"></div><div contenteditable="false"></div><p translate="no"></p><p class="notranslate"></p><div id="brainbox-overlay"></div><p></p>';
    const res = [...document.body.children].map((e) => isSkippedElement(e));
    expect(res).toEqual([true, true, true, true, true, false, true, true, true, false]);
  });
  it('links/buttons become their own segments on web pages', () => {
    document.body.innerHTML = '<p id="p">Voir <a href="#">nos produits</a> et <b>nos</b> offres</p>';
    const segs = collectSegments(document.getElementById('p')!, { skip: isSkippedElement, boundary: (el) => el.tagName === 'A' });
    expect(segs.map((s) => s.text)).toEqual(['Voir ', 'nos produits', ' et nos offres']);
  });
});

describe('case matching', () => {
  it.each([
    ['et installe', 'Install', 'install'],
    ['Bonjour', 'Hello', 'Hello'],
    ['vraiment.', 'Really.', 'really.'],
    ['je pense', 'I think', 'I think'],
    ['et le PC', 'NASA rocks', 'NASA rocks'],
    ['123', 'Abc', 'Abc'],
  ])('%j → %j becomes %j', (src, tr, want) => expect(matchLeadingCase(src, tr)).toBe(want));
});
