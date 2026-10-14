import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';
import { describe, it, expect } from 'vitest';

describe('integrated browser scene', () => {
  it('escapes metadata, honors capabilities and restores the lyrics surface', async () => {
    const script = await readFile(new URL('../../Media/Web/Lyrics/browser.js', import.meta.url), 'utf8');
    const dom = new JSDOM('<!doctype html><html><body><div id="layout"></div></body></html>', { runScripts: 'outside-only' });
    const messages = [];
    dom.window.chrome = { webview: { postMessage: value => messages.push(value) } };
    dom.window.eval(script);
    const receive = payload => dom.window.taskbarMedia.receive({ version: 1, type: 'browser', payload });
    receive({ active: true, title: '<img onerror=alert(1)>', artist: 'Artist', canPrevious: false, canNext: true, canPlayPause: true, isPlaying: true });
    const scene = dom.window.document.getElementById('browserScene');
    expect(scene.querySelector('img')).toBeNull();
    expect(scene.textContent).toContain('<img onerror=alert(1)>');
    const buttons = scene.querySelectorAll('button');
    expect(buttons[0].disabled).toBe(true);
    expect(buttons[1].textContent).toBe('⏸');
    buttons[1].click();
    expect(messages).toEqual([{ action: 'playPause' }]);
    expect(scene.style.color).toBe('var(--primary,white)');
    expect(dom.window.document.getElementById('layout').style.visibility).toBe('hidden');
    receive({ active: false });
    expect(scene.style.display).toBe('none');
    expect(dom.window.document.getElementById('layout').style.visibility).toBe('');
    dom.window.close();
  });
});
