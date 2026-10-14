(() => {
  document.title = 'TaskBarWidgets 设置';
  for (const button of document.querySelectorAll('[data-nav]')) button.setAttribute('aria-label', button.dataset.tooltip);
  const hide = element => element?.classList.add('host-owned-setting');
  for (const key of ['showLyricsOnStartup', 'autoHideWhenNoPlayback', 'startWithWindows', 'autoCheckUpdates', 'windowWidth', 'horizontalAnchor', 'xOffset', 'yOffset']) {
    for (const control of document.querySelectorAll(`[data-setting="${key}"]`)) hide(control.closest('.setting-row'));
  }
  hide(document.querySelector('.window-mode-options')?.closest('fieldset'));
  hide(document.querySelector('[data-display-mode]')?.closest('.panel'));
  hide(document.getElementById('checkUpdateButton')?.closest('.panel'));
  hide(document.querySelector('[data-setting="startWithWindows"]')?.closest('.panel'));
  document.querySelector('[data-page="displayArea"] .panel-head p').textContent = '调整歌词区域的封面与布局。共用字体、背景与位置请在“任务栏与布局”配置。';
  const show = document.querySelector('[data-show-lyrics-window]');
  if (show) show.textContent = '显示媒体视图';

  let schema = null;
  let pending = new Map();
  let requestNumber = 0;
  let activeRequest = null;
  let timeout;
  let fonts = [];
  let itemDrag = null;
  const post = (type, value = {}) => window.taskbarLyricsBridge.post({ type, value });
  const element = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };
  const currentValue = field => field && (pending.has(field.key) ? pending.get(field.key) : field.value);
  const byKey = key => schema?.fields.find(field => field.key === key);
  const value = (key, fallback) => currentValue(byKey(key)) ?? fallback;

  function orderedItems() {
    const items = new Map();
    for (const field of schema?.fields ?? []) {
      const match = /^items\.([^.]+)\./.exec(field.key);
      if (!match) continue;
      if (!items.has(match[1])) items.set(match[1], []);
      items.get(match[1]).push(field);
    }
    return [...items.values()].sort((a, b) => Number(currentValue(a.find(f => f.key.endsWith('.order')))) - Number(currentValue(b.find(f => f.key.endsWith('.order')))));
  }

  function renderPreview(itemOrder = orderedItems()) {
    const preview = document.getElementById('nativeTaskbarPreview');
    preview.replaceChildren();
    if (!schema) return;
    const horizontal = value('taskbar.horizontal_arrange', !value('taskbar.doubleRow', true));
    const items = itemOrder.filter(item => currentValue(item.find(field => field.key.endsWith('.show'))));
    preview.dataset.rows = horizontal ? '1' : '2';
    preview.style.fontFamily = value('taskbar.fontName', 'Segoe UI');
    preview.style.fontSize = `${value('taskbar.fontSize', 12)}pt`;
    preview.style.fontWeight = value('taskbar.fontBold', false) ? '700' : '400';
    preview.style.fontStyle = value('taskbar.fontItalic', false) ? 'italic' : 'normal';
    preview.style.textDecoration = [value('taskbar.fontUnderline', false) && 'underline', value('taskbar.fontStrikeOut', false) && 'line-through'].filter(Boolean).join(' ') || 'none';
    preview.style.columnGap = `${value('taskbar.item_space', 8)}px`;
    preview.style.rowGap = `${Math.max(0, value('taskbar.vertical_margin', 0))}px`;
    const background = value('taskbar.back_color', '#202020');
    const opacity = value('taskbar.transparent', false) ? 0 : value('taskbar.backgroundOpacity', 100) / 100;
    const rgb = [1, 3, 5].map(start => parseInt(background.slice(start, start + 2), 16));
    preview.style.backgroundColor = `rgba(${rgb.join(',')},${opacity})`;
    const baseLabelColor = currentValue(schema.fields.find(field => /^items\.[^.]+\.labelColor$/.test(field.key))) ?? '#ffffff';
    const independent = value('taskbar.specify_each_item_color', false);
    let row = 1, column = 1;
    for (const [index, item] of items.entries()) {
      const labelField = item.find(field => field.key.endsWith('.label'));
      const itemNode = element('div', 'native-preview-item');
      const label = element('span', 'native-preview-label', currentValue(labelField) ?? '');
      let sample = labelField?.sampleValue || '42';
      if (value('taskbar.hide_unit', false)) sample = sample.replace(/\s*(?:[KMGT]?[Bb](?:\/s)?|°C|℃|GHz|MHz)\s*$/i, '');
      if (value('taskbar.hide_percent', false)) sample = sample.replace(/%/g, '');
      if (value('taskbar.separate_value_unit_with_space', false)) sample = sample.replace(/(\d)([A-Za-z%°℃])/, '$1 $2');
      else sample = sample.replace(/(\d)\s+([A-Za-z%°℃])/, '$1$2');
      const number = element('span', 'native-preview-value', sample);
      label.style.color = independent ? currentValue(item.find(field => field.key.endsWith('.labelColor'))) : baseLabelColor;
      number.style.color = independent ? currentValue(item.find(field => field.key.endsWith('.valueColor'))) : baseLabelColor;
      itemNode.dataset.nativePreviewKey = labelField?.key ?? '';
      itemNode.classList.toggle('right-align', value('taskbar.value_right_align', false));
      itemNode.append(label, number);
      if (!horizontal) {
        const exclusive = labelField?.doubleLineExclusive === true;
        if (exclusive && row === 2) {
          preview.lastElementChild.style.gridRow = '1 / span 2';
          preview.lastElementChild.classList.add('double-line');
          column++; row = 1;
        }
        itemNode.style.gridColumn = String(column);
        itemNode.style.gridRow = exclusive || index === items.length - 1 && row === 1 ? '1 / span 2' : String(row);
        if (exclusive || index === items.length - 1 && row === 1) itemNode.classList.add('double-line');
        if (exclusive || row === 2) { column++; row = 1; } else row = 2;
      }
      preview.append(itemNode);
    }
    if (!items.length) preview.append(element('span', 'native-preview-empty', '没有已启用的显示项；主程序会保留上行速度。'));
    document.getElementById('nativePreviewSummary').textContent = `${horizontal ? '水平单行' : '上下双行'} · ${items.length} 个显示项 · ${value('taskbar.fontSize', 12)} pt · ${itemDrag ? '拖动排序预览' : pending.size ? '包含待应用更改' : '已读取主程序配置'}`;
  }

  function renderFonts() {
    let list = document.getElementById('native-fonts');
    if (!list) { list = element('datalist'); list.id = 'native-fonts'; document.body.append(list); }
    list.replaceChildren(...fonts.map(font => { const option = element('option'); option.value = font; return option; }));
  }

  function status(text, state = 'idle') {
    for (const node of document.querySelectorAll('[data-native-status]')) {
      node.textContent = text;
      node.dataset.state = state;
    }
    for (const button of document.querySelectorAll('[data-native-apply]')) button.disabled = !!activeRequest || !!itemDrag || !pending.size;
    for (const button of document.querySelectorAll('[data-native-reload]')) {
      button.disabled = !!activeRequest || !!itemDrag;
      button.textContent = pending.size ? '放弃并重新读取' : '重新读取';
    }
    for (const control of document.querySelectorAll('[data-native-key]')) control.disabled = !!activeRequest;
    for (const control of document.querySelectorAll('[data-native-move]')) control.disabled = activeRequest?.operation === 'load' || !!itemDrag || control.dataset.edge === 'true';
    for (const control of document.querySelectorAll('[data-native-drag]')) control.disabled = activeRequest?.operation === 'load';
  }

  function request(operation, values) {
    if (activeRequest || itemDrag) return;
    if (operation === 'apply' && Object.keys(values).some(key => /^items\.[^.]+\.order$/.test(key))) {
      // A permutation is one change: include unchanged ranks too, including hidden items.
      orderedItems().forEach((item, index) => { values[itemOrderKey(item)] = index + 1; });
    }
    const requestId = ++requestNumber;
    activeRequest = { requestId, operation, values, edits: new Map() };
    status(operation === 'apply' ? '正在保存并等待主程序确认…' : '正在读取主程序配置…', 'pending');
    timeout = window.setTimeout(() => {
      retainApplyDraft(activeRequest);
      activeRequest = null;
      status('主程序响应超时；未确认保存。请重新读取后重试。', 'error');
    }, 15000);
    post(operation === 'apply' ? 'applyNativeSettings' : 'getNativeSettings', { requestId, values });
  }

  function retainApplyDraft(request) {
    if (request?.operation === 'apply') pending = new Map([...Object.entries(request.values), ...request.edits]);
  }

  function draftStatus() {
    if (activeRequest) status('正在保存并等待主程序确认；新的排序会保留为待应用更改…', 'pending');
    else status(pending.size ? `${pending.size} 项配置待应用` : '主程序配置已同步', pending.size ? 'pending' : 'success');
  }

  function setDraft(field, value, refresh = true) {
    if (value === field.value) pending.delete(field.key);
    else pending.set(field.key, value);
    if (activeRequest?.operation === 'apply') activeRequest.edits.set(field.key, value);
    if (refresh) { draftStatus(); renderPreview(); }
  }

  function fieldRow(field) {
    const primaryToggle = field.type === 'boolean' && /^(?:taskbar.transparent|general.(?:cpu|gpu|disk|mainboard)Enabled)$/.test(field.key);
    const row = element('div', `setting-row native-setting-row${field.type === 'boolean' ? primaryToggle ? ' native-toggle-row' : ' native-checkbox-row' : ''}`);
    const label = element('label', 'setting-label');
    label.htmlFor = `native-field-${field.key}`;
    label.append(element('strong', '', field.label));
    const description = [field.description, field.restartRequired && '重启后生效'].filter(Boolean).join('；');
    if (description) label.append(element('small', '', description));
    const controls = element('div', 'native-field-controls');
    const value = currentValue(field);
    let input;
    if (field.type === 'select') {
      input = element('select', 'control native-select');
      (field.options ?? []).forEach((option, index) => {
        const node = element('option', '', option.label);
        node.value = String(index);
        node.selected = option.value === value;
        input.append(node);
      });
    } else {
      input = element('input', field.type === 'boolean' ? '' : 'control');
      input.type = field.type === 'boolean' ? 'checkbox' : field.type === 'number' ? 'number' : 'text';
      if (field.type === 'boolean') input.checked = value === true;
      else {
        input.value = value ?? '';
        if (field.type === 'number') {
          input.step = field.step ?? '1';
          if (field.min !== undefined) input.min = field.min;
          if (field.max !== undefined) input.max = field.max;
          input.required = true;
        }
        if (field.type === 'color') {
          input.pattern = '#[0-9A-Fa-f]{6}'; input.maxLength = 7; input.required = true;
          input.classList.add('native-color-text');
        }
        if (/fontName$/i.test(field.key)) input.setAttribute('list', 'native-fonts');
      }
    }
    input.id = label.htmlFor;
    input.dataset.nativeKey = field.key;
    input.setAttribute('aria-label', field.label);
    const update = event => {
      if (!input.checkValidity()) { if (event.type === 'change') input.reportValidity(); return; }
      const next = field.type === 'boolean' ? input.checked : field.type === 'number' ? Number(input.value) :
        field.type === 'select' ? field.options[Number(input.value)].value : input.value;
      setDraft(field, next);
      const picker = controls.querySelector('input[type="color"]');
      if (picker) picker.value = next;
      const itemState = row.closest('.native-item-card')?.querySelector('.native-item-state');
      if (itemState && field.key.endsWith('.show')) itemState.textContent = next ? '显示' : '隐藏';
    };
    input.addEventListener('change', update);
    if (field.type !== 'boolean' && field.type !== 'select') input.addEventListener('input', update);
    if (field.type === 'color') {
      const picker = element('input', 'native-color-picker');
      picker.type = 'color'; picker.value = value; picker.dataset.nativeKey = field.key;
      picker.setAttribute('aria-label', `${field.label}取色器`);
      picker.addEventListener('change', () => { input.value = picker.value; setDraft(field, picker.value); });
      picker.addEventListener('input', () => { input.value = picker.value; setDraft(field, picker.value); });
      controls.append(picker);
    }
    if (primaryToggle) {
      const toggle = element('label', 'switch');
      toggle.append(input, element('span', 'switch-track')); controls.append(toggle);
    } else controls.append(input);
    row.append(label, controls);
    return row;
  }

  const itemOrderKey = item => item.find(field => field.key.endsWith('.order')).key;

  function refreshItemOrder() {
    const container = document.querySelector('.native-item-grid');
    if (!container) return;
    const focused = document.activeElement;
    const cards = new Map([...container.children].map(card => [card.dataset.nativeItem, card]));
    const sorted = orderedItems();
    sorted.forEach((item, index) => {
      const card = cards.get(itemOrderKey(item));
      if (!card) return;
      card.querySelector('.native-order-badge').textContent = String(index + 1);
      for (const button of card.querySelectorAll('[data-native-move]'))
        button.dataset.edge = String(index + Number(button.dataset.nativeMove) < 0 || index + Number(button.dataset.nativeMove) >= sorted.length);
      container.append(card);
    });
    if (focused?.isConnected) focused.focus({ preventScroll: true });
  }

  function applyItemOrder(items, movedKey) {
    items.forEach((item, index) => setDraft(item.find(field => field.key.endsWith('.order')), index + 1, false));
    refreshItemOrder();
    draftStatus();
    renderPreview();
    const moved = items.find(item => itemOrderKey(item) === movedKey);
    const name = moved?.find(field => field.key.endsWith('.label'));
    const announcement = document.getElementById('nativeItemOrderAnnouncement');
    if (announcement) announcement.textContent = `${name?.itemName || name?.value || '显示项'}已移至第 ${items.indexOf(moved) + 1} 位。点击应用保存。`;
  }

  function moveItem(key, position) {
    const items = orderedItems();
    const from = items.findIndex(item => itemOrderKey(item) === key);
    const to = Math.max(0, Math.min(items.length - 1, position(from, items.length)));
    if (from < 0 || from === to) return;
    const [moved] = items.splice(from, 1);
    items.splice(to, 0, moved);
    applyItemOrder(items, key);
  }

  function endItemDrag() {
    itemDrag = null;
    document.querySelectorAll('.native-item-card').forEach(card => card.classList.remove('dragging', 'drag-target'));
    draftStatus();
    renderPreview();
  }

  function itemCards(fields, container) {
    container.classList.add('native-item-grid');
    const sorted = orderedItems();
    sorted.forEach((item, index) => {
      const key = itemOrderKey(item);
      const card = element('section', 'native-item-card');
      card.dataset.nativeItem = key;
      const summary = element('div', 'native-item-summary');
      const name = item.find(field => field.key.endsWith('.label'));
      const show = item.find(field => field.key.endsWith('.show'));
      const itemName = name?.itemName || show?.label || name?.label || '显示项';
      card.setAttribute('aria-label', itemName);
      const handle = element('button', 'native-item-drag-handle', '⠿');
      handle.type = 'button'; handle.draggable = true; handle.dataset.nativeDrag = key;
      handle.setAttribute('aria-label', `拖动排序：${itemName}`);
      handle.setAttribute('aria-describedby', 'nativeItemOrderInstructions');
      handle.title = '拖动排序；Alt + 方向键调整顺序';
      handle.addEventListener('keydown', event => {
        if (!event.altKey || !['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        moveItem(key, (index, count) => event.key === 'Home' ? 0 : event.key === 'End' ? count - 1 :
          index + (['ArrowUp', 'ArrowLeft'].includes(event.key) ? -1 : 1));
      });
      handle.addEventListener('dragstart', event => {
        if (activeRequest?.operation === 'load') { event.preventDefault(); return; }
        itemDrag = { key, original: orderedItems(), preview: orderedItems() };
        event.dataTransfer?.setData('text/plain', key);
        if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
        card.classList.add('dragging');
        draftStatus();
      });
      handle.addEventListener('dragend', () => { if (itemDrag) endItemDrag(); });
      card.addEventListener('dragover', event => {
        if (!itemDrag || itemDrag.key === key) return;
        event.preventDefault();
        if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
        document.querySelectorAll('.native-item-card.drag-target').forEach(node => node.classList.remove('drag-target'));
        card.classList.add('drag-target');
        const items = [...itemDrag.original];
        const from = items.findIndex(entry => itemOrderKey(entry) === itemDrag.key);
        const to = items.findIndex(entry => itemOrderKey(entry) === key);
        const [moved] = items.splice(from, 1); items.splice(to, 0, moved);
        itemDrag.preview = items;
        renderPreview(items);
      });
      card.addEventListener('drop', event => {
        if (!itemDrag) return;
        event.preventDefault();
        const { preview, key: movedKey } = itemDrag;
        endItemDrag();
        applyItemOrder(preview, movedKey);
      });
      summary.append(handle, element('span', 'native-order-badge', String(index + 1)), element('strong', '', itemName));
      summary.append(element('span', 'native-item-state', currentValue(show) ? '显示' : '隐藏'));
      if (show) summary.append(fieldRow({ ...show, label: `${itemName} · 显示` }));
      card.append(summary);
      const actions = element('div', 'native-item-actions');
      for (const [delta, text] of [[-1, '上移'], [1, '下移']]) {
        const button = element('button', 'btn secondary small', text);
        button.type = 'button'; button.dataset.nativeMove = String(delta);
        button.setAttribute('aria-label', `${text}：${itemName}`);
        button.dataset.edge = String(index + delta < 0 || index + delta >= sorted.length);
        button.disabled = button.dataset.edge === 'true';
        button.addEventListener('click', () => moveItem(key, index => index + delta));
        actions.append(button);
      }
      card.append(actions);
      const body = element('div', 'native-item-fields');
      for (const field of item.filter(field => !field.key.endsWith('.show') && !field.key.endsWith('.order'))) {
        const suffix = field.key.split('.').at(-1);
        body.append(fieldRow({ ...field, label: { label: '标签文本', labelColor: '标签颜色', valueColor: '数值颜色' }[suffix] ?? field.label }));
      }
      card.append(body);
      container.append(card);
    });
  }

  function render() {
    for (const container of document.querySelectorAll('[data-native-groups]')) {
      container.replaceChildren();
      const groups = [...(schema?.groups ?? [])].sort((a, b) => (a.id === 'appearance' ? -1 : b.id === 'appearance' ? 1 : 0));
      for (const group of groups) {
        if (group.page !== container.dataset.nativeGroups) continue;
        if (group.id === 'notifications') continue;
        if (group.page === 'plugins') continue;
        const fields = schema.fields.filter(field => field.group === group.id);
        if (!fields.length) continue;
        const panel = element(group.advanced ? 'details' : 'section', 'panel native-group');
        const head = element(group.advanced ? 'summary' : 'div', 'panel-head');
        head.append(element('h2', '', `${group.advanced ? '高级 · ' : ''}${group.label}`));
        if (group.id === 'items') {
          const instructions = element('p', '', '拖动卡片左上角调整顺序，点击应用保存；也可使用上移、下移，或聚焦拖动按钮后按 Alt + 方向键。');
          instructions.id = 'nativeItemOrderInstructions';
          const announcement = element('span', 'sr-only');
          announcement.id = 'nativeItemOrderAnnouncement'; announcement.setAttribute('aria-live', 'polite');
          head.append(instructions, announcement);
        }
        const body = element('div', 'settings native-fields-grid');
        if (group.id === 'items') itemCards(fields, body);
        else {
          fields.filter(field => !field.advanced).forEach(field => body.append(fieldRow(field)));
          const advanced = fields.filter(field => field.advanced);
          if (advanced.length) {
            const details = element('details', 'native-advanced'); details.append(element('summary', '', '高级选项'));
            const advancedGrid = element('div', 'native-fields-grid');
            advanced.forEach(field => advancedGrid.append(fieldRow(field))); details.append(advancedGrid); body.append(details);
          }
        }
        panel.append(head, body); container.append(panel);
      }
    }
    document.getElementById('nativeConfigPath').textContent = schema?.configPath ? `配置文件：${schema.configPath}` : '';
    renderFonts();
    renderPreview();
    if (activeRequest) status('正在等待主程序确认…', 'pending');
  }

  document.getElementById('openAudioPanelButton').addEventListener('click', () => post('openAudioPanel'));
  for (const button of document.querySelectorAll('[data-native-reload]')) button.addEventListener('click', () => request('load'));
  for (const button of document.querySelectorAll('[data-native-apply]')) button.addEventListener('click', () => {
    for (const input of document.querySelectorAll('[data-native-key]')) {
      if (!input.checkValidity()) { input.reportValidity(); status('请修正无效值后应用。', 'error'); return; }
    }
    if (pending.size) request('apply', Object.fromEntries(pending));
  });
  window.nativeSettingsApp = {
    receive(message) {
      if (message.type === 'settingsState') {
        fonts = (message.payload?.fonts ?? []).map(font => typeof font === 'string' ? font : font.value ?? font.Value).filter(Boolean);
        renderFonts();
      }
      if (message.type !== 'nativeSettingsResult') return;
      const result = message.payload;
      if (!activeRequest || result?.requestId !== activeRequest.requestId) return;
      window.clearTimeout(timeout);
      const completed = activeRequest;
      const operation = completed.operation;
      if (itemDrag) endItemDrag();
      activeRequest = null;
      if (result.success !== true || !Array.isArray(result.payload?.groups) || !Array.isArray(result.payload?.fields)) {
        retainApplyDraft(completed);
        if (Array.isArray(result.payload?.groups) && Array.isArray(result.payload?.fields)) {
          schema = result.payload;
          render(); // Keep the unconfirmed draft, but retain the actual native readback.
        }
        status(result.error || '主程序没有返回有效配置；更改尚未确认。', 'error'); return;
      }
      schema = result.payload;
      pending.clear();
      if (operation === 'apply') {
        for (const [key, value] of completed.edits) {
          if (value !== byKey(key)?.value) pending.set(key, value);
        }
      }
      render();
      if (pending.size) status('上一批更改已保存；新的排序仍待应用。', 'pending');
      else status(operation === 'apply' ? '已保存并应用，已读回主程序配置' : '主程序配置已同步', 'success');
    }
  };
  request('load');
})();
