// Functions injected into pages with chrome.scripting.executeScript. They must be self-contained.

export function showSelectionCard(original: string, translation: string, label: string, error: string | null): void {
  const HOST_ID = 'brainbox-selection-card';
  document.getElementById(HOST_ID)?.remove();
  const sel = window.getSelection();
  const rect = sel && sel.rangeCount ? sel.getRangeAt(0).getBoundingClientRect() : null;
  const host = document.createElement('div');
  host.id = HOST_ID;
  host.style.cssText = 'position:fixed;z-index:2147483647;top:0;left:0;';
  const root = host.attachShadow({ mode: 'open' });
  const top = Math.min(window.innerHeight - 160, (rect ? rect.bottom : 80) + 8);
  const left = Math.max(8, Math.min(window.innerWidth - 368, rect ? rect.left : 80));
  root.innerHTML = `<style>
    .card{position:fixed;top:${top}px;left:${left}px;width:340px;max-height:260px;overflow:auto;background:#151821;color:#F3F4F8;
      border:1px solid #262B38;border-radius:12px;padding:12px 14px;font:14px/1.45 system-ui,sans-serif;
      box-shadow:0 8px 30px rgba(0,0,0,.45),0 0 0 1px rgba(139,92,246,.25),0 0 18px rgba(139,92,246,.25)}
    .hd{display:flex;align-items:center;gap:6px;font-size:12px;color:#8A90A2;margin-bottom:6px}
    .dot{width:8px;height:8px;border-radius:50%;background:#8B5CF6;box-shadow:0 0 8px #8B5CF6}
    .t{white-space:pre-wrap;word-break:break-word}.err{color:#ff8080}
    .row{display:flex;gap:8px;margin-top:10px;justify-content:flex-end}
    button{background:#8B5CF6;color:white;border:0;border-radius:8px;padding:5px 12px;font:inherit;font-size:13px;cursor:pointer}
    button.ghost{background:#1C202B;color:#c9cce0}</style>
    <div class="card" role="dialog" aria-label="Brainbox translation"><div class="hd"><span class="dot"></span><span class="lbl"></span></div>
    <div class="t"></div><div class="row"><button class="ghost close">Close</button><button class="copy">Copy</button></div></div>`;
  (root.querySelector('.lbl') as HTMLElement).textContent = label;
  const t = root.querySelector('.t') as HTMLElement;
  t.textContent = error ?? translation;
  if (error) t.classList.add('err');
  t.title = original;
  const copy = root.querySelector('.copy') as HTMLButtonElement;
  copy.onclick = () => {
    const done = (): void => { copy.textContent = 'Copied ✓'; };
    navigator.clipboard.writeText(translation).then(done, () => {
      const ta = document.createElement('textarea'); ta.value = translation; document.body.appendChild(ta); ta.select();
      document.execCommand('copy'); ta.remove(); done();
    });
  };
  (root.querySelector('.close') as HTMLButtonElement).onclick = () => host.remove();
  document.addEventListener('keydown', function esc(e) { if (e.key === 'Escape') { host.remove(); document.removeEventListener('keydown', esc); } });
  document.documentElement.appendChild(host);
}

export function showToast(message: string): void {
  const el = document.createElement('div');
  el.textContent = message;
  el.style.cssText = 'position:fixed;z-index:2147483647;bottom:24px;left:50%;transform:translateX(-50%);background:#151821;color:#F3F4F8;'
    + 'border:1px solid #262B38;border-radius:999px;padding:8px 16px;font:14px system-ui,sans-serif;box-shadow:0 0 18px rgba(139,92,246,.35)';
  document.documentElement.appendChild(el);
  setTimeout(() => el.remove(), 2200);
}
