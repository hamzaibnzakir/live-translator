// Brainbox palette — same values as the Windows app (BrainboxTheme.xaml).
export const BB = {
  violet: '#8B5CF6', cyan: '#22D3EE', bg: '#0D0F14', surface: '#151821', surface2: '#1C202B',
  border: '#262B38', text: '#F3F4F8', muted: '#8A90A2', faint: '#5B6173', green: '#34D399', amber: '#FBBF24',
} as const;

export const OVERLAY_CSS = `
:host{all:initial}
*{box-sizing:border-box}
.bb{font:13px/1.45 "Segoe UI Variable Display","Segoe UI",system-ui,sans-serif;color:${BB.text}}
.panel{position:fixed;z-index:2147483646;background:${BB.surface};border:1px solid ${BB.border};border-radius:12px;
  box-shadow:0 10px 30px rgba(0,0,0,.45),0 0 0 1px rgba(139,92,246,.18),0 0 22px rgba(139,92,246,.18)}
.tip{max-width:420px;padding:10px 12px;pointer-events:auto}
.tip .hd,.banner .hd{display:flex;align-items:center;gap:6px;color:${BB.muted};font-size:11.5px;margin-bottom:4px}
.dot{width:8px;height:8px;border-radius:50%;background:linear-gradient(135deg,${BB.violet},${BB.cyan});box-shadow:0 0 8px ${BB.violet};flex:none}
.orig{white-space:pre-wrap;word-break:break-word}
.row{display:flex;gap:6px;margin-top:8px;flex-wrap:wrap}
button{font:inherit;font-size:12.5px;border:0;border-radius:8px;padding:4px 10px;cursor:pointer;color:#fff;
  background:linear-gradient(135deg,${BB.violet},#6d4ad8)}
button.ghost{background:${BB.surface2};color:#c9cce0;border:1px solid ${BB.border}}
button:focus-visible{outline:2px solid ${BB.cyan};outline-offset:1px}
.banner{top:72px;right:24px;width:340px;padding:12px 14px}
.bar{height:4px;border-radius:2px;background:${BB.surface2};overflow:hidden;margin-top:8px}
.bar>i{display:block;height:100%;width:0;background:linear-gradient(90deg,${BB.violet},${BB.cyan});transition:width .2s}
.pill{display:flex;align-items:center;gap:2px;padding:2px;border-radius:999px;font-size:12px}
.pill button{border-radius:999px;padding:3px 10px;background:transparent;color:${BB.text}}
.pill button.go{background:linear-gradient(135deg,${BB.violet},#6d4ad8)}
.pill button:hover{filter:brightness(1.15)}
.preview{padding:10px 12px;width:min(560px,60vw)}
.preview .lbl{color:${BB.muted};font-size:11.5px}
.preview .they{margin:2px 0 6px;white-space:pre-wrap}
.preview .back{color:#c9cce0;white-space:pre-wrap;font-style:italic}
.menu{width:240px;max-height:320px;display:flex;flex-direction:column;padding:6px}
.menu input{background:${BB.bg};color:${BB.text};border:1px solid ${BB.border};border-radius:8px;padding:6px 8px;font:inherit;margin-bottom:4px}
.menu .list{overflow:auto}
.menu .opt{padding:5px 8px;border-radius:6px;cursor:pointer}
.menu .opt:hover,.menu .opt.sel{background:${BB.surface2}}
.muted{color:${BB.muted}}
.err{color:#ff8a8a}
`;

/** Styles injected into the page itself (attributes only — we never add elements to Discord's tree). */
export const PAGE_CSS = `
html:not([data-bb-marker="off"]) [data-bb-translated="1"]:not([data-bb-state="original"])::after{
  content:"";display:inline-block;width:6px;height:6px;margin-left:6px;border-radius:50%;vertical-align:middle;
  background:linear-gradient(135deg,${BB.violet},${BB.cyan});box-shadow:0 0 6px ${BB.violet};opacity:.9;pointer-events:none}
html:not([data-bb-marker="off"]) [data-bb-translated="1"][data-bb-state="original"]::after{
  content:"";display:inline-block;width:6px;height:6px;margin-left:6px;border-radius:50%;vertical-align:middle;
  border:1px solid ${BB.violet};opacity:.8;pointer-events:none}
`;
