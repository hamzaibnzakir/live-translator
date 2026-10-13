// Optional "Polish with AI": one request to the user's own local OpenAI-compatible server
// (LM Studio / Ollama). Only ever called when the user clicks the button.
import { languageName } from '../shared/languages';
import type { Settings } from '../shared/settings';

export async function polish(s: Settings, text: string, from: string, to: string, original?: string): Promise<{ text: string; model: string }> {
  if (!s.polish.enabled) throw new Error('Polish with AI is turned off in settings');
  const base = s.polish.endpoint.replace(/\/$/, '');
  let model = s.polish.model;
  if (!model) {
    const r = await fetch(`${base}/models`);
    if (!r.ok) throw new Error(`Local AI server not reachable (${r.status})`);
    const list = (await r.json()) as { data?: { id: string }[] };
    model = list.data?.[0]?.id ?? '';
    if (!model) throw new Error('No model loaded in your local AI server');
  }
  const src = original ?? text;
  const res = await fetch(`${base}/chat/completions`, {
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
  if (!res.ok) throw new Error(`Local AI server error ${res.status}`);
  const data = (await res.json()) as { choices?: { message?: { content?: string } }[] };
  const out = data.choices?.[0]?.message?.content?.trim();
  if (!out) throw new Error('Empty answer from local AI server');
  return { text: out.replace(/^["“]|["”]$/g, ''), model };
}
