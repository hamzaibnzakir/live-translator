// Brainbox palette — same values as the Windows app (BrainboxTheme.xaml).
export const BB = {
  violet: '#8B5CF6', cyan: '#22D3EE', bg: '#0D0F14', surface: '#151821', surface2: '#1C202B',
  border: '#262B38', text: '#F3F4F8', muted: '#8A90A2', faint: '#5B6173', green: '#34D399', amber: '#FBBF24', red: '#F87171',
} as const;

/** The Brainbox "B" mark as inline SVG (no network, scales crisply). */
export const LOGO_SVG = `<svg class="logo" viewBox="0 0 24 24" aria-hidden="true"><defs><linearGradient id="bbg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${BB.violet}"/><stop offset="1" stop-color="${BB.cyan}"/></linearGradient></defs><rect x="1" y="1" width="22" height="22" rx="6.5" fill="url(#bbg)"/><path d="M8 6.5h4.6c2.1 0 3.4 1.1 3.4 2.8 0 1.1-.6 2-1.6 2.4 1.3.4 2.1 1.4 2.1 2.7 0 2-1.5 3.1-3.8 3.1H8z M10.4 8.5v2.6h1.9c.9 0 1.4-.5 1.4-1.3s-.5-1.3-1.4-1.3z M10.4 13v2.6h2.2c1 0 1.6-.5 1.6-1.3s-.6-1.3-1.6-1.3z" fill="#fff" fill-rule="evenodd"/></svg>`;

const ICONS = {
  close: '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>',
  chevron: '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4.5 6.5L8 10l3.5-3.5" stroke="currentColor" stroke-width="1.6" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  swap: '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 5h9l-2.5-2.5M13 11H4l2.5 2.5" stroke="currentColor" stroke-width="1.6" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  undo: '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M5 4L2.5 6.5 5 9M2.5 6.5H10a3.5 3.5 0 010 7H7" stroke="currentColor" stroke-width="1.6" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  warn: '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2l6.5 11.5h-13z" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/><path d="M8 6.5v3.2M8 11.6v.1" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>',
  download: '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.5v7.5M4.8 7L8 10.2 11.2 7M3 13h10" stroke="currentColor" stroke-width="1.6" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  spark: '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 1.5l1.6 4.1 4.2 1.4-4.2 1.4L8 12.5 6.4 8.4 2.2 7l4.2-1.4z" fill="currentColor"/></svg>',
};
export const icon = (name: keyof typeof ICONS): string => `<span class="ic">${ICONS[name]}</span>`;

export const OVERLAY_CSS = `
:host{all:initial}
*{box-sizing:border-box}
.bb{font:13.5px/1.45 "gg sans","Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;color:${BB.text};-webkit-font-smoothing:antialiased}
.ic{display:inline-flex;width:16px;height:16px;flex:none}.ic svg{width:100%;height:100%}
.logo{width:18px;height:18px;flex:none;filter:drop-shadow(0 0 6px rgba(139,92,246,.55))}
.panel{position:fixed;z-index:2147483646;background:rgba(21,24,33,.97);border:1px solid ${BB.border};border-radius:14px;
  box-shadow:0 18px 40px rgba(0,0,0,.5),0 0 0 1px rgba(139,92,246,.12),0 0 28px -6px rgba(139,92,246,.35);backdrop-filter:blur(10px)}
.hd{display:flex;align-items:center;gap:8px;color:${BB.muted};font-size:12px;font-weight:600;letter-spacing:.02em}
.hd .sp{flex:1}
.x{background:none;border:0;color:${BB.muted};padding:2px;border-radius:6px;cursor:pointer;display:inline-flex}
.x:hover{color:${BB.text};background:${BB.surface2}}
button{font:inherit;font-size:12.5px;font-weight:600;border:0;border-radius:8px;padding:6px 12px;cursor:pointer;color:#fff;display:inline-flex;align-items:center;gap:6px;
  background:linear-gradient(135deg,${BB.violet},#6d4ad8);box-shadow:0 1px 0 rgba(255,255,255,.12) inset}
button:hover{filter:brightness(1.12)}
button:disabled{opacity:.55;cursor:default;filter:none}
button.ghost{background:${BB.surface2};color:#d4d7e8;border:1px solid ${BB.border};box-shadow:none}
button:focus-visible,.x:focus-visible{outline:2px solid ${BB.cyan};outline-offset:2px}
.row{display:flex;gap:8px;margin-top:10px;flex-wrap:wrap;align-items:center}
.muted{color:${BB.muted}}.faint{color:${BB.faint}}
.err{color:${BB.red}}
kbd{font:600 11px/1 ui-monospace,monospace;color:#c9cce0;background:${BB.surface2};border:1px solid ${BB.border};border-bottom-width:2px;border-radius:5px;padding:2px 5px}

/* tooltip with the original */
.tip{max-width:440px;padding:12px 14px;pointer-events:auto}
.tip .orig{white-space:pre-wrap;word-break:break-word;margin-top:6px;font-size:14.5px;color:#e6e8f2}
.tip .meta{display:flex;gap:6px;margin-top:2px}
.chip{display:inline-flex;align-items:center;gap:4px;font-size:11px;font-weight:600;padding:2px 8px;border-radius:999px;background:${BB.surface2};color:#c9cce0;border:1px solid ${BB.border}}
.chip.ok{color:${BB.green};border-color:rgba(52,211,153,.35)}.chip.warn{color:${BB.amber};border-color:rgba(251,191,36,.35)}

/* notices (top-right stack) */
.stack{position:fixed;top:64px;right:20px;z-index:2147483646;display:flex;flex-direction:column;gap:10px;width:360px;pointer-events:none}
.notice{position:relative;pointer-events:auto;padding:12px 14px;overflow:hidden;animation:bbIn .18s ease-out}
.notice.warn .t{color:${BB.amber}}
.notice .t{font-weight:700;font-size:13.5px;color:${BB.text};margin:4px 0 2px}
.notice .b{color:#c3c6d6}
.bar{height:5px;border-radius:3px;background:${BB.surface2};overflow:hidden;margin-top:10px}
.bar>i{display:block;height:100%;width:0;background:linear-gradient(90deg,${BB.violet},${BB.cyan});transition:width .25s}
.bar.busy>i{width:35%;animation:bbSlide 1.1s ease-in-out infinite}
@keyframes bbIn{from{opacity:0;transform:translateY(-6px)}to{opacity:1;transform:none}}
@keyframes bbSlide{0%{margin-left:-35%}100%{margin-left:100%}}

/* reply pill */
.pill{display:flex;align-items:center;padding:3px;border-radius:999px;font-size:12.5px;gap:2px}
.pill button{border-radius:999px;padding:4px 10px 4px 6px;background:transparent;color:${BB.text};box-shadow:none;font-weight:700;letter-spacing:.02em}
.pill button.go{background:linear-gradient(135deg,rgba(139,92,246,.35),rgba(34,211,238,.22));border:1px solid rgba(139,92,246,.45)}
.pill button.go:hover{background:linear-gradient(135deg,rgba(139,92,246,.55),rgba(34,211,238,.35))}
.pill button.lang{padding:4px 6px;color:${BB.muted}}
.pill .st{width:7px;height:7px;border-radius:50%;background:${BB.green};box-shadow:0 0 6px ${BB.green}}
.pill[data-state="busy"] .st{background:${BB.cyan};animation:bbPulse .8s ease-in-out infinite alternate}
.pill[data-state="warn"] .st{background:${BB.amber};box-shadow:0 0 6px ${BB.amber}}
.pill[data-state="idle"] .st{background:${BB.faint};box-shadow:none}
@keyframes bbPulse{from{opacity:.35}to{opacity:1}}

/* reply preview */
.preview{padding:12px 14px;width:min(600px,62vw)}
.preview .lbl{color:${BB.muted};font-size:11.5px;font-weight:600;text-transform:uppercase;letter-spacing:.06em;margin-top:8px}
.preview .they{margin:3px 0 0;white-space:pre-wrap;font-size:15px;color:${BB.text}}
.preview .back{margin-top:3px;color:#b9bdd0;white-space:pre-wrap;font-style:italic}
.preview .foot{display:flex;gap:8px;align-items:center;margin-top:12px;flex-wrap:wrap}
.preview .foot .sp{flex:1}

/* language menu */
.menu{width:260px;max-height:340px;display:flex;flex-direction:column;padding:8px}
.menu input{background:${BB.bg};color:${BB.text};border:1px solid ${BB.border};border-radius:9px;padding:8px 10px;font:inherit;margin-bottom:6px;outline:none}
.menu input:focus{border-color:${BB.violet};box-shadow:0 0 0 3px rgba(139,92,246,.2)}
.menu .list{overflow:auto}
.menu .sec{font-size:11px;font-weight:700;color:${BB.faint};text-transform:uppercase;letter-spacing:.06em;padding:6px 8px 2px}
.menu .opt{padding:6px 8px;border-radius:7px;cursor:pointer;display:flex;gap:8px;align-items:center}
.menu .opt:hover,.menu .opt.sel{background:${BB.surface2}}
.menu .opt .muted{margin-left:auto;font-size:12px}
@media (prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
`;

/** Styles injected into the page itself (attributes only — we never add elements to Discord's tree). */
export const PAGE_CSS = `
html:not([data-bb-marker="off"]) [data-bb-translated="1"]:not([data-bb-state="original"])::after{
  content:"";display:inline-block;width:6px;height:6px;margin-left:6px;border-radius:50%;vertical-align:middle;
  background:linear-gradient(135deg,${BB.violet},${BB.cyan});box-shadow:0 0 6px ${BB.violet};opacity:.9;pointer-events:none}
html:not([data-bb-marker="off"]) [data-bb-translated="1"][data-bb-state="original"]::after{
  content:"";display:inline-block;width:6px;height:6px;margin-left:6px;border-radius:50%;vertical-align:middle;
  border:1px solid ${BB.violet};opacity:.8;pointer-events:none}
[data-bb-translated="1"]{transition:background-color .2s}
[data-bb-translated="1"]:hover{background-color:rgba(139,92,246,.06);border-radius:4px}
`;
