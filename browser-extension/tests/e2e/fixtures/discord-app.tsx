// A Discord look-alike used by the E2E tests. It copies the parts of Discord's real DOM that
// Brainbox depends on (ids, roles, aria/data attributes, hashed-looking class names), renders
// with React like Discord, virtualizes the message list (rows mount/unmount while scrolling)
// and uses a real Slate editor for the message box, so "what gets sent" is read from Slate's
// own state — exactly the trap the extension must avoid.
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { createEditor, Descendant, Editor, Node, Transforms } from 'slate';
import { Editable, Slate, withReact } from 'slate-react';
import { withHistory } from 'slate-history';

type Part =
  | { t: 'text'; v: string }
  | { t: 'mention'; v: string }
  | { t: 'emoji'; v: string }
  | { t: 'link'; v: string }
  | { t: 'code'; v: string }
  | { t: 'pre'; v: string }
  | { t: 'spoiler'; v: string }
  | { t: 'bold'; v: string };

interface Msg { id: string; author: string; parts: Part[]; edited?: boolean; replyTo?: string; embed?: { title: string; description: string } }

const CHANNEL = location.pathname.split('/')[3] ?? '222';
const params = new URLSearchParams(location.search);
const COUNT = Number(params.get('count') ?? 60);
const ROW_H = 88;
const OVERSCAN = 8;

const FR = [
  'On se retrouve sur le serveur après le dîner.', 'Quelqu’un a vu la nouvelle mise à jour du jeu ?', 'Je ne peux pas venir ce soir, désolé.',
  'Merci beaucoup pour ton aide hier.', 'La partie de demain commence à vingt heures.', 'Il faut absolument essayer la nouvelle carte.',
  'Mon micro ne marche plus depuis ce matin.', 'Bravo à toute l’équipe pour la victoire.', 'Qui veut faire une partie classée ?',
  'Le serveur est un peu lent aujourd’hui.', 'Je vais acheter le nouveau jeu ce week-end.', 'Tu as regardé le tournoi hier soir ?',
  'Nous cherchons encore deux joueurs pour l’équipe.', 'Pensez à lire les règles du serveur.', 'Je suis en retard, commencez sans moi.',
  'Cette musique est vraiment incroyable.', 'Quelle est la meilleure classe pour débuter ?', 'Je reviens dans dix minutes.',
  'Les inscriptions pour le tournoi sont ouvertes.', 'Il pleut beaucoup ici depuis trois jours.',
];
const JA = ['今日はみんなでゲームをしましょう。', '新しいアップデートはとても面白いです。', '明日の夜は時間がありますか？', 'ありがとうございます、助かりました。', 'サーバーが少し重いです。'];
const EN = ['lol nice', 'I agree with that', 'see you later guys', 'ok', 'gg wp'];

function filler(i: number): Msg {
  const id = String(100000 + i);
  const r = i % 10;
  const text = r < 6 ? `${FR[i % FR.length]} ${FR[(i * 7 + 3) % FR.length]}` : r < 8 ? `${JA[i % JA.length]}${JA[(i * 3 + 1) % JA.length]}` : EN[i % EN.length]!;
  return { id, author: ['Camille', 'Hugo', 'Yuki', 'Léa', 'Mateo'][i % 5]!, parts: [{ t: 'text', v: text }] };
}

const SPECIAL: Msg[] = [
  { id: '900001', author: 'Camille', parts: [{ t: 'text', v: 'Bonjour tout le monde, comment ça va ce soir ?' }] },
  { id: '900002', author: 'Hugo', parts: [{ t: 'text', v: 'Salut ' }, { t: 'mention', v: '@David' }, { t: 'text', v: ', tu viens jouer avec nous ce soir ? ' }, { t: 'emoji', v: '😀' }] },
  { id: '900003', author: 'Yuki', parts: [{ t: 'text', v: 'こんにちは、元気ですか？今日はとても暑いですね。' }] },
  { id: '900004', author: 'Sam', parts: [{ t: 'text', v: 'Hey, I just joined this server!' }] },
  { id: '900005', author: 'Léa', parts: [{ t: 'text', v: 'Regarde ce lien ' }, { t: 'link', v: 'https://example.com/docs' }, { t: 'text', v: ' et installe ' }, { t: 'code', v: 'npm install brainbox' }, { t: 'text', v: ' avant demain.' }] },
  { id: '900006', author: 'BotFR', parts: [{ t: 'text', v: 'Voici le code de la commande :' }, { t: 'pre', v: "const message = 'Bonjour';\nconsole.log(message);" }] },
  { id: '900007', author: 'Mateo', replyTo: '900001', parts: [{ t: 'text', v: 'Oui, je suis d’accord avec toi.' }] },
  { id: '900008', author: 'Léa', parts: [{ t: 'text', v: 'Le film était génial, ' }, { t: 'spoiler', v: 'le héros meurt à la fin' }, { t: 'text', v: ' vraiment.' }] },
  { id: '900009', author: 'ServerBot', parts: [], embed: { title: 'Mise à jour du serveur', description: 'Nous avons ajouté de nouveaux salons pour les joueurs.' } },
  { id: '900010', author: 'Mateo', parts: [{ t: 'text', v: '¿Alguien quiere jugar esta noche?' }] },
  { id: '900011', author: 'Hugo', parts: [{ t: 'emoji', v: '😂' }, { t: 'emoji', v: '😂' }] },
  { id: '900012', author: 'Camille', parts: [{ t: 'link', v: 'https://example.com/video' }] },
  { id: '900013', author: 'Camille', parts: [{ t: 'text', v: 'Je pense que c’est une ' }, { t: 'bold', v: 'très bonne' }, { t: 'text', v: ' idée.' }] },
];

function initialMessages(): Msg[] {
  const out: Msg[] = [];
  for (let i = 0; i < Math.max(0, COUNT - SPECIAL.length); i++) out.push(filler(i));
  return [...out, ...SPECIAL];
}

function Content({ m, id }: { m: Msg; id: string }): React.ReactElement {
  return (
    <div id={`message-content-${id}`} className="markup__75297 messageContent_c19a55">
      {m.parts.map((p, i) => {
        switch (p.t) {
          case 'text': return <React.Fragment key={i}>{p.v}</React.Fragment>;
          case 'bold': return <strong key={i}>{p.v}</strong>;
          case 'mention': return <span key={i} className="mention wrapper_f61d60 interactive" role="button" tabIndex={0}>{p.v}</span>;
          case 'emoji': return <span key={i} className="emojiContainer_bae8cb"><img className="emoji" alt={p.v} aria-label={p.v} draggable={false} src={`data:image/svg+xml,${encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" width="22" height="22"><text y="18" font-size="18">${p.v}</text></svg>`)}`} data-type="emoji" /></span>;
          case 'link': return <a key={i} className="anchor_edefb8 anchorUnderlineOnHover_edefb8" href={p.v} rel="noreferrer noopener" target="_blank" role="button" tabIndex={0}>{p.v}</a>;
          case 'code': return <code key={i} className="inlineCode_e50d7e">{p.v}</code>;
          case 'pre': return <pre key={i}><code className="scrollbarGhostHairline_c858cb hljs">{p.v}</code></pre>;
          case 'spoiler': return <span key={i} className="spoilerContent_aa9639 hidden_aa9639" role="button" tabIndex={0} aria-label="Spoiler" aria-expanded="false"><span className="spoilerInnerContainer_aa9639" aria-hidden="true">{p.v}</span></span>;
        }
      })}
      {m.edited && <span className="timestamp_c19a55 timestampInline_c19a55"><time aria-label="Edited Today at 21:04" dateTime="2026-10-13T21:04:00.000Z"><span className="edited_c19a55"> (edited)</span></time></span>}
    </div>
  );
}

function Row({ m, byId }: { m: Msg; byId: Map<string, Msg> }): React.ReactElement {
  const replied = m.replyTo ? byId.get(m.replyTo) : undefined;
  return (
    <li id={`chat-messages-${CHANNEL}-${m.id}`} className="messageListItem__5126c" aria-setsize={-1} style={{ height: ROW_H }}>
      <div className="message__5126c cozyMessage__5126c wrapper_c19a55" role="article" data-list-item-id={`chat-messages___chat-messages-${CHANNEL}-${m.id}`} tabIndex={-1} aria-labelledby={`message-username-${m.id} message-content-${m.id}`}>
        <div className="contents_c19a55">
          {replied && (
            <div id={`message-reply-context-${m.id}`} className="repliedMessage_c19a55" aria-label={`Replying to ${replied.author}`}>
              <span className="username_c19a55">@{replied.author}</span>
              <div className="repliedTextPreview_c19a55 clickable_c19a55" role="link" tabIndex={0}>
                <div className="repliedTextContent_c19a55 markup__75297 messageContent_c19a55" id={`message-content-${replied.id}`}>{replied.parts.filter((p) => p.t === 'text').map((p) => p.v).join('')}</div>
              </div>
            </div>
          )}
          <h3 className="header_c19a55" aria-labelledby={`message-username-${m.id}`}><span id={`message-username-${m.id}`} className="headerText_c19a55"><span className="username_c19a55 desaturateUserColors_c7819f" role="button" tabIndex={0}>{m.author}</span></span>{' '}<span className="timestamp_c19a55 timestampInline_c19a55"><time aria-label="Today at 20:41" dateTime="2026-10-13T20:41:00.000Z">Today at 20:41</time></span></h3>
          <Content m={m} id={m.id} />
        </div>
        {m.embed && (
          <div id={`message-accessories-${m.id}`} className="container_b558d0">
            <article className="embedWrapper_b558d0 embedFull__623de embed__623de markup__75297">
              <div className="gridContainer__623de"><div className="grid__623de">
                <div className="embedTitle__623de embedMargin__623de">{m.embed.title}</div>
                <div className="embedDescription__623de embedMargin__623de">{m.embed.description}</div>
              </div></div>
            </article>
          </div>
        )}
      </div>
    </li>
  );
}

declare global {
  interface Window {
    __fixture: { edit(id: string, text: string): void; sent: string[]; mounts: number; scrollToTop(): void; scrollToBottom(): void; editorText(): string; count: number };
  }
}
const sent: string[] = [];
let mounts = 0;

function App(): React.ReactElement {
  const [messages, setMessages] = useState<Msg[]>(initialMessages);
  const [range, setRange] = useState<[number, number]>([0, 0]);
  const scroller = useRef<HTMLDivElement>(null);
  const editor = useMemo(() => withHistory(withReact(createEditor())), []);
  const initialValue: Descendant[] = useMemo(() => [{ type: 'line', children: [{ text: '' }] } as unknown as Descendant], []);
  const byId = useMemo(() => new Map(messages.map((m) => [m.id, m])), [messages]);

  const updateRange = useCallback(() => {
    const el = scroller.current;
    if (!el) return;
    const start = Math.max(0, Math.floor(el.scrollTop / ROW_H) - OVERSCAN);
    const end = Math.min(messages.length, Math.ceil((el.scrollTop + el.clientHeight) / ROW_H) + OVERSCAN);
    setRange((r) => (r[0] === start && r[1] === end ? r : [start, end]));
  }, [messages.length]);

  useEffect(() => { const el = scroller.current!; el.scrollTop = el.scrollHeight; updateRange(); }, []);
  useEffect(updateRange, [messages.length, updateRange]);
  useEffect(() => { mounts += range[1] - range[0]; }, [range]);

  useEffect(() => {
    window.__fixture = {
      sent,
      get mounts() { return mounts; },
      count: messages.length,
      edit: (id, text) => setMessages((ms) => ms.map((m) => (m.id === id ? { ...m, parts: [{ t: 'text', v: text }], edited: true } : m))),
      scrollToTop: () => { scroller.current!.scrollTop = 0; },
      scrollToBottom: () => { scroller.current!.scrollTop = scroller.current!.scrollHeight; },
      editorText: () => editor.children.map((n) => Node.string(n)).join('\n'),
    } as Window['__fixture'];
  }, [editor, messages.length]);

  const sendDraft = (): void => {
    // Exactly what a chat app does: read the editor *model*, not the DOM.
    const text = editor.children.map((n) => Node.string(n)).join('\n').trim();
    if (!text) return;
    sent.push(text);
    setMessages((ms) => [...ms, { id: String(950000 + sent.length), author: 'David', parts: [{ t: 'text', v: text }] }]);
    Transforms.delete(editor, { at: { anchor: Editor.start(editor, []), focus: Editor.end(editor, []) } });
    requestAnimationFrame(() => { scroller.current!.scrollTop = scroller.current!.scrollHeight; });
  };

  const [start, end] = range;
  return (
    <div className="app_a3002d" id="app-mount">
      <nav className="guilds_c48ade" aria-label="Servers sidebar"><div className="guild">FR</div></nav>
      <section className="chat_f75fb0" aria-label="Channel">
        <div className="title_f75fb0"><h1 className="titleWrapper_fc4f04"># général</h1></div>
        <main className="chatContent_f75fb0">
          <div className="messagesWrapper__36d07">
            <div ref={scroller} className="scroller__36d07 auto_d125d2 scrollerBase_d125d2" onScroll={updateRange} style={{ overflowY: 'auto', height: 'calc(100vh - 150px)' }}>
              <ol className="scrollerInner__36d07" data-list-id="chat-messages" aria-label="Messages in général" role="list" style={{ paddingTop: start * ROW_H, paddingBottom: (messages.length - end) * ROW_H, margin: 0 }}>
                {messages.slice(start, end).map((m) => <Row key={m.id} m={m} byId={byId} />)}
              </ol>
            </div>
          </div>
          <form>
            <div className="channelTextArea_f75fb0 channelTextArea_a7d72e">
              <div className="scrollableContainer_d0696b"><div className="inner_d0696b">
                <div className="textArea_d0696b textAreaSlate_d0696b slateContainer_ec4baf">
                  <Slate editor={editor} initialValue={initialValue}>
                    <Editable
                      aria-label="Message #général"
                      className="markup__75297 editor_a552a6 slateTextArea_ec4baf fontSize16Padding__74017"
                      placeholder="Message #général"
                      onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendDraft(); } }}
                      renderElement={({ attributes, children }) => <div {...attributes}>{children}</div>}
                    />
                  </Slate>
                </div>
                <div className="buttons_d0696b"><button type="button" aria-label="Send a gift">🎁</button></div>
              </div></div>
            </div>
          </form>
        </main>
      </section>
    </div>
  );
}

createRoot(document.getElementById('root')!).render(<App />);
