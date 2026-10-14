import { createServer } from 'node:http';
import { test, expect } from './harness';

test('AI polish: Test button works with the switch off, and explains a stopped server', async ({ bb }) => {
  // A tiny OpenAI-compatible server, like LM Studio's.
  const server = createServer((req, res) => {
    res.setHeader('Content-Type', 'application/json');
    if (req.url === '/v1/models') { res.end(JSON.stringify({ data: [{ id: 'qwen2.5-7b-instruct' }] })); return; }
    if (req.url === '/v1/chat/completions') { res.end(JSON.stringify({ choices: [{ message: { content: 'Hi everyone, how’s it going?' } }] })); return; }
    res.statusCode = 404; res.end('{}');
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', () => r()));
  const port = (server.address() as { port: number }).port;

  const page = await bb.context.newPage();
  await page.goto(`chrome-extension://${bb.extensionId}/options.html#ai`);
  await expect(page.locator('#polish-enabled')).not.toBeChecked();
  await page.fill('#polish-endpoint', `http://127.0.0.1:${port}/v1`);
  await page.click('#polish-test');
  await expect(page.locator('#polish-result')).toHaveText('✓ qwen2.5-7b-instruct: Hi everyone, how’s it going?');

  server.close();
  await page.click('#polish-test');
  await expect(page.locator('#polish-result')).toContainText('Start Server');
  await page.close();
});
