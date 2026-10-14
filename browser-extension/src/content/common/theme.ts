// Brainbox Discord Auto Translator palette: white + fresh green (matches static/ui.css).
export const BB = {
  bg: '#F6FAF7', surface: '#FFFFFF', surface2: '#EEF6F0', mint: '#E2F5E8', border: '#DCE8E0', border2: '#C9DCCF',
  text: '#13241A', muted: '#5B6F62', faint: '#8C9C92', green: '#1F9D55', greenInk: '#167A42', greenSoft: '#C8EFD6',
  amber: '#A86A0C', amberSoft: '#FDF1DC', red: '#C2392F',
} as const;

/** The Brainbox "B" mark as inline SVG (no network, scales crisply). */
export const LOGO_SVG = `<svg class="logo" viewBox="0 0 24 24" aria-hidden="true"><rect x="1" y="1" width="22" height="22" rx="6.5" fill="${BB.green}"/><path d="M8 6.5h4.6c2.1 0 3.4 1.1 3.4 2.8 0 1.1-.6 2-1.6 2.4 1.3.4 2.1 1.4 2.1 2.7 0 2-1.5 3.1-3.8 3.1H8z M10.4 8.5v2.6h1.9c.9 0 1.4-.5 1.4-1.3s-.5-1.3-1.4-1.3z M10.4 13v2.6h2.2c1 0 1.6-.5 1.6-1.3s-.6-1.3-1.6-1.3z" fill="#fff" fill-rule="evenodd"/></svg>`;

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

const EASE = 'cubic-bezier(.2,.7,.2,1)';

// Motion budget for low-end PCs: only opacity/transform, short, never looping except the
// one "translating" dot; no blur/backdrop filters; everything off with reduced motion.
export const OVERLAY_CSS = `
:host{all:initial}
*{box-sizing:border-box}
.bb{font:13.5px/1.45 "gg sans","Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;color:${BB.text};-webkit-font-smoothing:antialiased}
.ic{display:inline-flex;width:16px;height:16px;flex:none}.ic svg{width:100%;height:100%}
.logo{width:18px;height:18px;flex:none}
.panel{position:fixed;z-index:2147483646;background:${BB.surface};border:1px solid ${BB.border};border-radius:14px;box-shadow:0 12px 32px rgba(0,0,0,.28),0 2px 6px rgba(0,0,0,.12)}
.hd{display:flex;align-items:center;gap:8px;color:${BB.muted};font-size:12px;font-weight:700;letter-spacing:.01em}
.hd .sp{flex:1}
.x{background:none;border:0;color:${BB.faint};padding:3px;border-radius:7px;cursor:pointer;display:inline-flex;transition:background-color .15s,color .15s}
.x:hover{color:${BB.text};background:${BB.surface2}}
button{font:inherit;font-size:12.5px;font-weight:650;border:0;border-radius:9px;padding:6px 12px;cursor:pointer;color:#fff;display:inline-flex;align-items:center;gap:6px;background:${BB.green};transition:background-color .15s,transform .12s ${EASE}}
button:hover{background:${BB.greenInk}}
button:active{transform:scale(.97)}
button:disabled{opacity:.55;cursor:default;transform:none}
button.ghost{background:${BB.surface};color:${BB.text};border:1px solid ${BB.border2}}
button.ghost:hover{background:${BB.surface2}}
button:focus-visible,.x:focus-visible{outline:2px solid ${BB.green};outline-offset:2px}
.row{display:flex;gap:8px;margin-top:10px;flex-wrap:wrap;align-items:center}
.muted{color:${BB.muted}}.faint{color:${BB.faint}}
.err{color:${BB.red}}
kbd{font:600 11px/1 ui-monospace,Consolas,monospace;color:${BB.muted};background:${BB.surface};border:1px solid ${BB.border2};border-bottom-width:2px;border-radius:5px;padding:2px 5px}
.in{animation:bbIn .2s ${EASE} both}

/* tooltip with the original */
.tip{max-width:440px;padding:12px 14px;pointer-events:auto}
.tip .orig{white-space:pre-wrap;word-break:break-word;margin-top:8px;font-size:14.5px;color:${BB.text}}
.chip{display:inline-flex;align-items:center;gap:4px;font-size:11px;font-weight:650;padding:2px 8px;border-radius:999px;background:${BB.surface2};color:${BB.muted};border:1px solid ${BB.border}}
.chip.ok{color:${BB.greenInk};background:${BB.mint};border-color:${BB.greenSoft}}

/* notices (top-right stack) */
.stack{position:fixed;top:64px;right:20px;z-index:2147483646;display:flex;flex-direction:column;gap:10px;width:360px;pointer-events:none}
.notice{position:relative;pointer-events:auto;padding:12px 14px;animation:bbIn .22s ${EASE} both}
.notice.warn{background:${BB.amberSoft};border-color:#F4D9A8}
.notice .t{font-weight:700;font-size:14px;color:${BB.text};margin:6px 0 2px}
.notice.warn .t{color:${BB.amber}}
.notice .b{color:${BB.muted}}
.bar{height:5px;border-radius:3px;background:${BB.surface2};overflow:hidden;margin-top:10px}
.bar>i{display:block;height:100%;width:0;background:${BB.green};transition:width .25s ${EASE}}
.bar.busy>i{width:35%;animation:bbSlide 1.2s ease-in-out infinite}
@keyframes bbIn{from{opacity:0;transform:translateY(-6px)}to{opacity:1;transform:none}}
@keyframes bbSlide{0%{transform:translateX(-100%)}100%{transform:translateX(300%)}}

/* reply pill */
.pill{display:flex;align-items:center;padding:3px;border-radius:999px;font-size:12.5px;gap:2px;transition:transform .15s ${EASE}}
.pill:hover{transform:translateY(-1px)}
.pill button{border-radius:999px;padding:4px 10px 4px 5px;background:transparent;color:${BB.text};font-weight:700}
.pill button:hover{background:${BB.surface2}}
.pill button.go{background:${BB.mint};border:1px solid ${BB.greenSoft};color:${BB.greenInk}}
.pill button.go:hover{background:${BB.greenSoft}}
.pill button.lang{padding:4px 6px;color:${BB.muted}}
.pill .st{width:7px;height:7px;border-radius:50%;background:${BB.green}}
.pill[data-state="busy"] .st{animation:bbPulse .7s ease-in-out infinite alternate}
.pill[data-state="warn"] .st{background:${BB.amber}}
.pill[data-state="idle"] .st{background:${BB.faint}}
@keyframes bbPulse{from{opacity:.25}to{opacity:1}}

/* reply preview */
.preview{padding:12px 14px;width:min(600px,62vw);animation:bbUp .2s ${EASE} both}
.preview .lbl{color:${BB.muted};font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.07em;margin-top:10px}
.preview .they{margin:3px 0 0;white-space:pre-wrap;font-size:15px;color:${BB.text};background:${BB.mint};border:1px solid ${BB.greenSoft};border-radius:9px;padding:7px 10px}
.preview .back{margin-top:3px;color:${BB.muted};white-space:pre-wrap;font-style:italic;padding:0 2px}
.preview .foot{display:flex;gap:8px;align-items:center;margin-top:12px;flex-wrap:wrap}
.preview .foot .sp{flex:1}
@keyframes bbUp{from{opacity:0;transform:translateY(6px)}to{opacity:1;transform:none}}

/* language menu */
.menu{width:260px;max-height:340px;display:flex;flex-direction:column;padding:8px;animation:bbUp .16s ${EASE} both}
.menu input{background:${BB.surface};color:${BB.text};border:1px solid ${BB.border2};border-radius:9px;padding:8px 10px;font:inherit;margin-bottom:6px;outline:none}
.menu input:focus{border-color:${BB.green};box-shadow:0 0 0 3px rgba(31,157,85,.16)}
.menu .list{overflow:auto}
.menu .sec{font-size:11px;font-weight:700;color:${BB.faint};text-transform:uppercase;letter-spacing:.07em;padding:6px 8px 2px}
.menu .opt{padding:6px 8px;border-radius:8px;cursor:pointer;display:flex;gap:8px;align-items:center;transition:background-color .1s}
.menu .opt:hover{background:${BB.surface2}}
.menu .opt.sel{background:${BB.mint};color:${BB.greenInk};font-weight:650}
.menu .opt .muted{margin-left:auto;font-size:12px}
@media (prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
`;

/** Styles injected into the page itself (attributes only — we never add elements to Discord's tree). */
export const PAGE_CSS = `
html:not([data-bb-marker="off"]) [data-bb-translated="1"]:not([data-bb-state="original"])::after{
  content:"";display:inline-block;width:6px;height:6px;margin-left:6px;border-radius:50%;vertical-align:middle;background:#3CC873;opacity:.9;pointer-events:none}
html:not([data-bb-marker="off"]) [data-bb-translated="1"][data-bb-state="original"]::after{
  content:"";display:inline-block;width:6px;height:6px;margin-left:6px;border-radius:50%;vertical-align:middle;border:1px solid #3CC873;opacity:.8;pointer-events:none}
[data-bb-translated="1"]{border-radius:4px}
[data-bb-translated="1"]:hover{background-color:rgba(60,200,115,.07)}
/* Freshly translated text fades in once (opacity only: cheap on any PC). */
[data-bb-fresh]{animation:bbFresh .35s ease-out}
@keyframes bbFresh{from{opacity:.35}to{opacity:1}}
@media (prefers-reduced-motion:reduce){[data-bb-fresh]{animation:none}}
`;
