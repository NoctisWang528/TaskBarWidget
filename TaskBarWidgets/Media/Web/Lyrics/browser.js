(() => {
  const scene = document.createElement('div');
  scene.id = 'browserScene';
  scene.style.cssText = 'display:none;position:fixed;inset:0;align-items:center;gap:8px;padding:0 10px;box-sizing:border-box;font-family:var(--font-family);font-size:var(--font-size);font-weight:var(--font-weight);color:var(--primary,white);background:var(--surface-color)';
  const metadata = document.createElement('div');
  metadata.style.cssText = 'min-width:0;flex:1';
  const title = document.createElement('div');
  const source = document.createElement('div');
  title.style.cssText = 'overflow:hidden;white-space:nowrap;text-overflow:ellipsis;font-weight:600';
  source.style.cssText = 'overflow:hidden;white-space:nowrap;text-overflow:ellipsis;opacity:.7;font-size:10px';
  metadata.append(title, source); scene.append(metadata);
  const controls = {};
  for (const [action, label, glyph] of [['previous', '上一首', '⏮'], ['playPause', '播放或暂停', '▶'], ['next', '下一首', '⏭']]) {
    const button = document.createElement('button');
    button.type = 'button'; button.ariaLabel = label; button.textContent = glyph;
    button.dataset.mediaAction = action;
    button.style.cssText = 'border:0;background:transparent;color:inherit;padding:3px;cursor:pointer;font-size:14px';
    button.addEventListener('click', event => {
      event.preventDefault(); event.stopPropagation();
      window.chrome?.webview?.postMessage({action});
    });
    controls[action] = button; scene.append(button);
  }
  document.body.append(scene);
  window.taskbarMedia = { receive(message) {
    if (message.version !== 1 || message.type !== 'browser') return;
    const state = message.payload;
    scene.style.display = state.active ? 'flex' : 'none';
    document.getElementById('layout').style.visibility = state.active ? 'hidden' : '';
    title.textContent = state.title || '浏览器媒体'; source.textContent = state.artist || state.source;
    controls.playPause.textContent = state.isPlaying ? '⏸' : '▶';
    controls.previous.disabled = !state.canPrevious;
    controls.next.disabled = !state.canNext;
    controls.playPause.disabled = !state.canPlayPause;
    for (const button of Object.values(controls)) button.style.opacity = button.disabled ? '.35' : '1';
  }};
})();
