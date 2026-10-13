import * as esbuild from 'esbuild';
const here = new URL('.', import.meta.url).pathname;
await esbuild.build({ entryPoints: [`${here}discord-app.tsx`], bundle: true, outfile: `${here}dist/discord-app.js`, format: 'iife', jsx: 'automatic', define: { 'process.env.NODE_ENV': '"production"' }, minify: true, logLevel: 'warning' });
console.log('fixtures built');
