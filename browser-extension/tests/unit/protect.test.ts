import { describe, expect, it } from 'vitest';
import { splitProtected, translateProtected, translatableParts } from '../../src/shared/protect';

const upper = async (parts: string[]) => parts.map((p) => p.toUpperCase());

describe('markdown & mention preservation in drafts', () => {
  it('keeps mentions, channels, emoji, links, code and markdown markers exactly', async () => {
    const draft = 'hey @Hugo, check #rules and <@123456> :smile: **really** `npm test` https://x.dev/a?b=1 ||spoiler|| <t:1700000000:R>';
    const out = await translateProtected(draft, upper);
    for (const tok of ['@Hugo', '#rules', '<@123456>', ':smile:', '**', '`npm test`', 'https://x.dev/a?b=1', '||', '<t:1700000000:R>']) expect(out).toContain(tok);
    expect(out).toContain('HEY');
    expect(out).toContain('REALLY');
    expect(out).toContain('SPOILER');
  });
  it('never sends code blocks or custom emoji to the engine', () => {
    const pieces = splitProtected('look ```js\nconst a = 1;\n``` and <:pepe:987> ok?');
    expect(translatableParts(pieces)).toEqual(['look', 'and', 'ok?']);
  });
  it('keeps quote / list / heading prefixes and line structure', async () => {
    const out = await translateProtected('> quoted line\n- item one\n# title', upper);
    expect(out).toBe('> QUOTED LINE\n- ITEM ONE\n# TITLE');
  });
  it('snake_case is text, not markup', () => {
    expect(translatableParts(splitProtected('use my_var please'))).toEqual(['use my_var please']);
  });
  it('a draft with nothing translatable is returned unchanged', async () => {
    expect(await translateProtected(':wave: @here https://a.b', upper)).toBe(':wave: @here https://a.b');
  });
});
