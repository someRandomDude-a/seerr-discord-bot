import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

async function renderer() {
  const dom = new JSDOM('<body></body>');
  const source = (await readFile(new URL('../../media_bot/panel_assets/chat.js', import.meta.url), 'utf8')).replaceAll('export function', 'function');
  const context = vm.createContext({ document: dom.window.document, URL });
  vm.runInContext(source + '\nglobalThis.ui = { discordContent, chatMessage, chatAttachments };', context);
  return { dom, ui: context.ui };
}

test('Discord markdown and user/role/channel tags are rendered without trusting message HTML', async () => {
  const { dom, ui } = await renderer();
  try {
    const content = ui.discordContent('**Bold** *italic* __under__ ~~strike~~ `code` <@123> <@&456> <#789> <img src=x onerror=alert(1)>',
      { 'user:123': 'Viewer', 'role:456': 'Admin', 'channel:789': 'general' });
    assert.equal(content.querySelector('strong').textContent, 'Bold');
    assert.deepEqual([...content.querySelectorAll('.discord-tag')].map(el => el.textContent), ['@Viewer', '@Admin', '#general']);
    assert.equal(content.querySelector('img'), null); assert.ok(content.textContent.includes('<img src='));
    const unsafe = ui.discordContent('[click](javascript:alert) [site](https://example.com)');
    assert.equal(unsafe.querySelectorAll('a').length, 1); assert.equal(unsafe.querySelector('a').rel, 'noopener noreferrer');
  } finally { dom.window.close(); }
});

test('images/files/embeds use authenticated same-origin routes, never untrusted external image URLs', async () => {
  const { dom, ui } = await renderer();
  try {
    const message = ui.chatMessage({ message_id: '123', author_name: 'Viewer', created_at: 1, content: 'Hello', bot: true,
      attachments: [{ key: 'attachment0', filename: 'Photo.png', image: true, size: 100 }],
      embeds: [{ title: '<script>unsafe</script>', description: '**Description**', image: 'embed0image' }] });
    assert.deepEqual([...message.querySelectorAll('img')].map(image => image.getAttribute('src')), ['/api/admin/media/123/attachment0', '/api/admin/media/123/embed0image']);
    assert.equal(message.querySelector('script'), null); assert.equal(message.querySelector('.chat-badge').textContent, 'BOT');
    const files = ui.chatAttachments([{ upload: 'upload-id', filename: 'Document.pdf', size: 1024 }]);
    assert.equal(files.querySelector('a').getAttribute('href'), '/api/admin/files/upload-id');
  } finally { dom.window.close(); }
});
