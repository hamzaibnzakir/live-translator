// Optional "Polish with AI": one request to the user's own local OpenAI-compatible server
// (LM Studio / Ollama). Only ever called when the user clicks the button.
import { languageName } from '../shared/languages';
import type { Settings } from '../shared/settings';

/** fetch() with errors a person can act on (server not started, permission not granted…). */
async function call(url: string, init?: RequestInit): Promise<Response> {
  const origin = new URL(url).origin;
  if (!(await chrome.permissions.contains({ origins: [`${origin}/*`] }))) {
    throw new Error(`Brainbox isn't allowed to reach ${origin} yet. Click Test (or the switch) again and choose Allow.`);
  }
  try {
    return await fetch(url, init);
  } catch {
    throw new Error(`Can't reach ${origin}. Start the server: LM Studio → Developer → Start Server (port 1234), or run "ollama serve" (port 11434).`);
  }
}

export async function polish(s: Settings, text: string, from: string, to: string, original?: string, test?: { endpoint: string; model: string }): Promise<{ text: string; model: string }> {
  // The Settings "Test" button checks the connection even while the feature is switched off.
  if (!test && !s.polish.enabled) throw new Error('Polish with AI is turned off. Turn it on in Settings → AI polish.');
  const base = (test?.endpoint || s.polish.endpoint).trim().replace(/\/$/, '');
  let model = test ? test.model : s.polish.model;
  if (!model) {
    const r = await call(`${base}/models`);
    if (!r.ok) throw new Error(`Local AI server not reachable (${r.status})`);
    const list = (await r.json()) as { data?: { id: string }[] };
    model = list.data?.[0]?.id ?? '';
    if (!model) throw new Error('The server is running but has no model. In LM Studio, load a model (top bar) and try again.');
  }
  const src = original ?? text;
  const res = await call(`${base}/chat/completions`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      model,
      temperature: 0.2,
      messages: [
        { role: 'system', content: `You translate chat messages from ${languageName(from)} to ${languageName(to)}. Keep slang, tone and emoji natural. Keep @mentions, #channels, :emoji:, links and code exactly as they are. Reply with the translation only.` },
        { role: 'user', content: original ? `Message: ${src}\nMachine translation (may be clumsy): ${text}` : src },
      ],
    }),
  });
  if (!res.ok) {
    const detail = await res.text().then((t) => t.slice(0, 160)).catch(() => '');
    throw new Error(`Local AI server answered ${res.status}${detail ? `: ${detail}` : ''}`);
  }
  const data = (await res.json()) as { choices?: { message?: { content?: string } }[] };
  const out = data.choices?.[0]?.message?.content?.trim();
  if (!out) throw new Error('Empty answer from local AI server');
  return { text: out.replace(/^["“]|["”]$/g, ''), model };
}
