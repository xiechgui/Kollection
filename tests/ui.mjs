// DOM + real HTTP regression tests. No browser rendering is claimed by this suite.
import assert from 'node:assert/strict';
import { readFile, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { JSDOM, VirtualConsole } from 'jsdom';

const root = resolve(import.meta.dirname, '..');
const temp = await mkdtemp(join(tmpdir(), 'kollection-ui-'));
const base = 'http://127.0.0.1:15279';
const server = spawn(process.env.DOTNET_EXE || 'dotnet', [join(root, 'src/Collection.Web/bin/Release/net10.0/Collection.Web.dll')], {
  cwd: join(root, 'src/Collection.Web'),
  env: { ...process.env, COLLECTION_PORT: '15279', COLLECTION_DATA: temp },
  stdio: 'ignore'
});
const errors = [], failures = [];
let dom;
async function waitFor(check) {
  const end = Date.now() + 10000;
  while (Date.now() < end) {
    if (await check()) return;
    await new Promise(r => setTimeout(r, 20));
  }
  throw Error('Timed out waiting for UI/API state');
}
try {
  await waitFor(async () => { try { return (await fetch(base + '/api/state')).ok; } catch { return false; } });
  const html = await readFile(join(root, 'src/Collection.Web/wwwroot/index.html'), 'utf8');
  const js = await readFile(join(root, 'src/Collection.Web/wwwroot/app.js'), 'utf8');
  const vc = new VirtualConsole();
  vc.on('jsdomError', e => errors.push(e.message));
  dom = new JSDOM(html, { url: base, runScripts: 'outside-only', virtualConsole: vc });
  const w = dom.window, doc = w.document;
  w.structuredClone = structuredClone;
  // LAN HTTP is not a secure context: exercise the getRandomValues fallback.
  w.crypto.randomUUID = undefined;
  w.confirm = () => true;
  w.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  w.HTMLDialogElement.prototype.close = function () { this.open = false; };
  w.fetch = (url, opts) => fetch(new URL(url, base), opts);
  w.eval(js);
  const get = s => { const el = doc.querySelector(s); assert.ok(el, s); return el; };
  const input = (s, value) => { const el = get(s); el.value = value; el.dispatchEvent(new w.Event('input', { bubbles: true })); };
  const change = (s, value) => { const el = get(s); el.value = value; el.dispatchEvent(new w.Event('change', { bubbles: true })); };
  const check = s => { const el = get(s); el.checked = true; el.dispatchEvent(new w.Event('change', { bubbles: true })); };
  const state = async () => (await fetch(base + '/api/state')).json();
  async function save() {
    const revision = (await state()).revision;
    get('#save').click();
    await waitFor(async () => (await state()).revision > revision);
    await waitFor(() => !get('#save').disabled);
  }
  async function test(name, action) {
    try { await action(); console.log('PASS:', name); }
    catch (e) { failures.push(name + ': ' + e.message); console.error('FAIL:', name, e.message); }
  }
  await waitFor(() => doc.querySelector('[data-filter="movie"]'));
  await test('Mobile navigation and unsaved detail protection', async () => {
    get('#mobile-menu').click(); assert.ok(doc.body.classList.contains('menu-open'));
    assert.equal(get('#mobile-menu').getAttribute('aria-expanded'), 'true');
    get('#menu-close').click(); assert.ok(!doc.body.classList.contains('menu-open'));
    get('#new').click(); input('#item-name', 'Unsaved phone item');
    assert.ok(doc.body.classList.contains('detail-open'));
    w.confirm=()=>false; get('#detail-back').click();
    assert.equal(get('#item-name').value, 'Unsaved phone item');
    w.confirm=()=>true; get('#detail-back').click();
    assert.ok(!doc.body.classList.contains('detail-open'));
    assert.equal((await state()).items.length, 0);
  });
  await test('Create actor and movie; reference and reverse navigation', async () => {
    get('#new').click(); input('#item-name', 'UI actor'); check('[data-tag-check="actor"]'); await save();
    const actor = (await state()).items.find(i => i.name === 'UI actor');
    get('#new').click(); input('#item-name', 'UI movie'); check('[data-tag-check="movie"]');
    change('[data-ref="actor"]', actor.id); input('[data-value="rating"]', '9'); await save();
    get(`[data-jump="${actor.id}"]`).click();
    assert.equal(get('#item-name').value, 'UI actor');
    assert.ok(get('#detail').textContent.includes('UI movie'));
    const movie = (await state()).items.find(i => i.name === 'UI movie');
    get(`#detail [data-jump="${movie.id}"]`).click();
    assert.equal(get('[data-value="rating"]').value, '9');
  });
  await test('Clearing a suggested attribute removes its stored value', async () => {
    get('[data-remove="rating"]').click(); await save();
    const movie = (await state()).items.find(i => i.name === 'UI movie');
    assert.equal(Object.hasOwn(movie.values, 'rating'), false);
    assert.equal(Object.hasOwn(movie.values, 'title'), false);
  });
  await test('Import remains visible after an unrelated search', async () => {
    const file = join(temp, 'ui-import.txt'); await writeFile(file, 'UI test fixture');
    input('#search', 'does-not-match-anything');
    get('#import').click(); input('#import-path', file);
    get('#modal-form').dispatchEvent(new w.Event('submit', { bubbles: true, cancelable: true }));
    await waitFor(() => !get('#modal').open);
    assert.equal(get('#search').value, '');
    assert.ok(get('#content').textContent.includes('ui-import.txt'));
  });
  // Clear search to let subsequent checks run independently on the old implementation.
  input('#search', '');
  await test('New manual item in staging opens the formal collection', async () => {
    get('#new').click(); input('#item-name', 'Manual from staging'); await save();
    assert.equal(get('#title').textContent, '全部收藏');
    assert.ok(get('#content').textContent.includes('Manual from staging'));
  });
  await test('Review import edits and confirm into collection', async () => {
    get('[data-view="drafts"]').click();
    const draft = (await state()).items.find(i => i.draft);
    get(`[data-item="${draft.id}"]`).click(); input('#item-name', 'Reviewed text');
    get('#approve').click();
    await waitFor(async () => !(await state()).items.find(i => i.id === draft.id).draft);
    await waitFor(() => get('#title').textContent === '全部收藏');
    assert.ok(get('#content').textContent.includes('Reviewed text'));
  });
  assert.deepEqual(errors, [], 'Unhandled DOM errors');
  assert.deepEqual(failures, [], 'UI regression failures');
} finally {
  dom?.window.close();
  if (server.exitCode === null) {
    const exited = new Promise(r => server.once('exit', r)); server.kill(); await exited;
  }
  await rm(temp, { recursive: true, force: true });
}
