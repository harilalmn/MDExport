// Direct editing of the rendered document.
//
// The Markdown source stays the single source of truth. Every top-level block carries its
// source range, and a change only rewrites the range of the blocks that changed, so everything
// else in the file keeps its exact bytes. Blocks this file cannot turn back into Markdown are
// locked instead of being approximated.
(function () {
  var COMMIT_DELAY_MS = 150;
  var INLINE = { STRONG: 1, B: 1, EM: 1, I: 1, DEL: 1, S: 1, STRIKE: 1, CODE: 1, A: 1, IMG: 1, BR: 1, MARK: 1, SUB: 1, SUP: 1, SPAN: 1, FONT: 1 };
  var HEADINGS = { H1: 1, H2: 2, H3: 3, H4: 4, H5: 5, H6: 6 };

  var article = null;
  var active = false;
  var entries = [];       // Top-level blocks as the source knows them.
  var timer = null;
  var inFlight = null;    // The commit waiting for the host.
  var serverHtml = null;  // Latest render, applied when the caret leaves a block.
  var caretBlock = null;
  var savedRange = null;
  var docLength = 0;

  // --------------- which blocks can be edited ---------------

  function inlineOk(node, allowCheckbox) {
    if (node.nodeType !== 1) return true;
    if (allowCheckbox && node.tagName === 'INPUT' && node.type === 'checkbox') return true;
    if (!INLINE[node.tagName]) return false;
    // A class means a Markdown extension made this (footnote references, math, attributes),
    // and its syntax cannot be recovered from the HTML.
    if (node.className) return false;
    for (var c = node.firstChild; c; c = c.nextSibling) if (!inlineOk(c, false)) return false;
    return true;
  }

  function allInline(el, allowCheckbox) {
    for (var c = el.firstChild; c; c = c.nextSibling) if (!inlineOk(c, allowCheckbox)) return false;
    return true;
  }

  function listOk(el) {
    return Array.prototype.every.call(el.children, function (li) {
      if (li.tagName !== 'LI') return false;
      for (var c = li.firstChild; c; c = c.nextSibling) {
        if (c.nodeType !== 1 || inlineOk(c, true)) continue;
        if (!blockOk(c)) return false;
      }
      return true;
    });
  }

  function tableOk(el) {
    var cells = el.querySelectorAll('th, td');
    // Column widths only exist on grid tables, which pipe syntax could not reproduce.
    if (!el.querySelector('thead') || el.querySelector('caption, table, col[style*="width"]')) return false;
    return Array.prototype.every.call(cells, function (cell) {
      return !cell.hasAttribute('colspan') && !cell.hasAttribute('rowspan') && allInline(cell, false);
    });
  }

  function blockOk(el) {
    var tag = el.tagName;
    if (tag === 'P' || HEADINGS[tag]) return allInline(el, false);
    if (tag === 'UL' || tag === 'OL') return listOk(el);
    if (tag === 'BLOCKQUOTE') return Array.prototype.every.call(el.children, blockOk);
    if (tag === 'PRE') return el.children.length <= 1;
    if (tag === 'TABLE') return tableOk(el);
    if (tag === 'HR') return true;
    if (tag === 'DIV') return !el.className && !el.hasAttribute('data-src-start') && allInline(el, false);
    return false;
  }

  // --------------- HTML to Markdown ---------------

  function escapeText(text) {
    return text
      .replace(/\\/g, '\\\\')
      .replace(/([*`\[\]<])/g, '\\$1')
      .replace(/(^|[^A-Za-z0-9])_/g, '$1\\_')
      .replace(/_(?=[^A-Za-z0-9]|$)/g, '\\_');
  }

  function wrap(content, marker, closing) {
    var m = /^(\s*)([\s\S]*?)(\s*)$/.exec(content);
    if (!m[2]) return content;
    return m[1] + marker + m[2] + (closing || marker) + m[3];
  }

  function inline(el) {
    var out = '';
    for (var n = el.firstChild; n; n = n.nextSibling) {
      if (n.nodeType === 3) {
        out += escapeText(n.nodeValue.replace(/\n/g, '').replace(/ /g, ' '));
        continue;
      }
      if (n.nodeType !== 1) continue;
      switch (n.tagName) {
        case 'STRONG': case 'B': out += wrap(inline(n), '**'); break;
        case 'EM': case 'I': out += wrap(inline(n), '*'); break;
        case 'DEL': case 'S': case 'STRIKE': out += wrap(inline(n), '~~'); break;
        case 'MARK': out += wrap(inline(n), '=='); break;
        case 'SUB': out += wrap(inline(n), '~'); break;
        case 'SUP': out += wrap(inline(n), '^'); break;
        case 'CODE': out += codeSpan(n.textContent); break;
        case 'BR': out += '\n'; break;
        case 'IMG': out += image(n); break;
        case 'A': out += link(n); break;
        case 'INPUT': break;
        default: out += inline(n);
      }
    }
    return out;
  }

  function codeSpan(text) {
    if (text.indexOf('`') < 0) return '`' + text + '`';
    return '`` ' + text + ' ``';
  }

  function image(img) {
    var src = img.getAttribute('data-md-src') || img.getAttribute('src') || '';
    return '![' + (img.getAttribute('alt') || '') + '](' + src + titleOf(img) + ')';
  }

  function link(a) {
    var href = a.getAttribute('href') || '';
    var text = inline(a);
    if (text === href) return href;
    return '[' + text + '](' + href + titleOf(a) + ')';
  }

  function titleOf(el) {
    var title = el.getAttribute('title');
    return title ? ' "' + title.replace(/"/g, '\\"') + '"' : '';
  }

  function indent(text, prefix) {
    return text.split('\n').map(function (line) { return line ? prefix + line : line; }).join('\n');
  }

  function listItem(li, marker) {
    var parts = [];
    var run = document.createElement('div');
    var task = '';
    function flush() {
      if (run.firstChild) parts.push({ md: inline(run).replace(/^\s+|\s+$/g, ''), list: false });
      run = document.createElement('div');
    }
    Array.prototype.slice.call(li.childNodes).forEach(function (c) {
      if (c.nodeType === 1 && c.tagName === 'INPUT') {
        task = c.hasAttribute('checked') ? '[x] ' : '[ ] ';
      } else if (c.nodeType === 1 && !inlineOk(c, false)) {
        flush();
        parts.push({ md: block(c), list: c.tagName === 'UL' || c.tagName === 'OL' });
      } else {
        run.appendChild(c.cloneNode(true));
      }
    });
    flush();

    var body = '';
    parts.forEach(function (p, i) {
      if (i > 0) body += p.list ? '\n' : '\n\n';
      body += p.md;
    });
    var pad = new Array(marker.length + 1).join(' ');
    var lines = indent(body, pad).slice(pad.length);
    return marker + task + lines;
  }

  function list(el) {
    var ordered = el.tagName === 'OL';
    var number = parseInt(el.getAttribute('start') || '1', 10);
    return Array.prototype.map.call(el.children, function (li, i) {
      return listItem(li, ordered ? (number + i) + '. ' : '- ');
    }).join('\n');
  }

  function table(el) {
    function row(tr) {
      return '| ' + Array.prototype.map.call(tr.children, function (cell) {
        return inline(cell).replace(/\n+$/, '').replace(/\n/g, '<br>').replace(/\|/g, '\\|').trim();
      }).join(' | ') + ' |';
    }
    var head = el.querySelector('thead tr');
    var rule = '|' + Array.prototype.map.call(head.children, function (th) {
      var align = th.style.textAlign;
      if (align === 'center') return ' :---: ';
      if (align === 'right') return ' ---: ';
      if (align === 'left') return ' :--- ';
      return ' --- ';
    }).join('|') + '|';
    var rows = Array.prototype.map.call(el.querySelectorAll('tbody tr'), row);
    return [row(head), rule].concat(rows).join('\n');
  }

  function fence(pre) {
    var code = pre.querySelector('code') || pre;
    var language = /language-(\S+)/.exec(code.className || '');
    var text = code.textContent.replace(/\n$/, '');
    var ticks = '```';
    while (text.indexOf(ticks) >= 0) ticks += '`';
    return ticks + (language ? language[1] : '') + '\n' + text + '\n' + ticks;
  }

  function block(el) {
    var tag = el.tagName;
    if (HEADINGS[tag]) {
      var title = inline(el).replace(/\n/g, ' ').trim();
      return title ? new Array(HEADINGS[tag] + 1).join('#') + ' ' + title : '';
    }
    if (tag === 'UL' || tag === 'OL') return list(el);
    if (tag === 'BLOCKQUOTE') {
      var inner = Array.prototype.map.call(el.children, block)
        .filter(function (b) { return b.length > 0; }).join('\n\n');
      return inner.split('\n').map(function (l) { return l ? '> ' + l : '>'; }).join('\n');
    }
    if (tag === 'PRE') return fence(el);
    if (tag === 'TABLE') return table(el);
    if (tag === 'HR') return '---';
    return inline(el).replace(/^\s+|\s+$/g, '');
  }

  // --------------- tracking what changed ---------------

  function clean(html) {
    return html
      .replace(/ (data-src-start|data-src-end|data-source-line|contenteditable)="[^"]*"/g, '')
      .replace(/ class="(md-sync-highlight|md-locked)?"/g, '');
  }

  function range(el) {
    var holder = el.hasAttribute('data-src-start') ? el : el.querySelector('[data-src-start]');
    if (!holder) return { start: -1, end: -1 };
    return {
      start: parseInt(holder.getAttribute('data-src-start'), 10),
      end: parseInt(holder.getAttribute('data-src-end'), 10)
    };
  }

  function prepare(el) {
    // No source range means nothing can be written back: raw HTML, footnotes, and anything else
    // the renderer could not place. Blocks made on this page have no range yet but are ours.
    var ok = blockOk(el) && (range(el).start >= 0 || el.mdSent !== undefined);
    el.classList.toggle('md-locked', !ok);
    if (!el.classList.length) el.removeAttribute('class');
    if (!ok || el.tagName === 'HR') el.setAttribute('contenteditable', 'false');
    else el.removeAttribute('contenteditable');
  }

  function snapshot(pin, pinRange) {
    entries = Array.prototype.map.call(article.children, function (el) {
      prepare(el);
      var r = el === pin ? pinRange : range(el);
      return { el: el, start: r.start, end: r.end, html: clean(el.outerHTML) };
    });
    if (pin) {
      var e = entries.filter(function (x) { return x.el === pin; })[0];
      if (e && pin.mdSent) e.html = pin.mdSent;
    }
  }

  // The first contiguous run of blocks that no longer match the source.
  function findRun() {
    var cur = Array.prototype.slice.call(article.children);
    var i = 0;
    while (i < entries.length && i < cur.length && cur[i] === entries[i].el &&
           clean(cur[i].outerHTML) === entries[i].html) i++;
    if (i === entries.length && i === cur.length) return null;

    var m = i, k = i, found = false;
    for (m = i; m < entries.length && !found; m++) {
      var at = cur.indexOf(entries[m].el);
      if (at >= i && clean(cur[at].outerHTML) === entries[m].html) { k = at; found = true; break; }
    }
    if (!found) { m = entries.length; k = cur.length; }
    return { first: i, oldEnd: m, els: cur.slice(i, k) };
  }

  function buildCommit() {
    var run = findRun();
    if (!run) return null;

    var old = entries.slice(run.first, run.oldEnd).filter(function (e) { return e.start >= 0; });
    var md = run.els.map(block).filter(function (s) { return s.length > 0; }).join('\n\n');
    var start, end;
    if (old.length) {
      start = old[0].start;
      end = old[old.length - 1].end;
    } else {
      var prev = run.first > 0 ? entries[run.first - 1] : null;
      var next = entries[run.oldEnd];
      if (prev && prev.end >= 0) {
        start = end = prev.end;
      } else if (next && next.start >= 0) {
        start = end = next.start;
      } else {
        // Nothing nearby has a known position. Never overwrite: add to the end of the file.
        start = end = docLength;
      }
    }

    run.els.forEach(function (el) { el.mdSent = clean(el.outerHTML); });
    return { run: run, start: start, end: end, md: md };
  }

  function commit() {
    timer = null;
    if (inFlight) return;
    inFlight = buildCommit();
    if (!inFlight) return;
    window.mdPost({ type: 'edit-commit', start: inFlight.start, end: inFlight.end, md: inFlight.md });
  }

  function schedule() {
    if (timer) clearTimeout(timer);
    timer = setTimeout(commit, COMMIT_DELAY_MS);
  }

  function acknowledge(m) {
    var sent = inFlight;
    inFlight = null;
    docLength = m.docLength;
    serverHtml = m.html;
    if (!sent) return;

    var replaced = sent.run.els.map(function (el) {
      return { el: el, start: m.start, end: m.end, html: el.mdSent };
    });
    var after = entries.slice(sent.run.oldEnd);
    after.forEach(function (e) { if (e.start >= 0) { e.start += m.delta; e.end += m.delta; } });
    entries = entries.slice(0, sent.run.first).concat(replaced, after);

    if (findRun()) commit();
    else reconcile(caretBlock);
  }

  // Brings the page in line with the latest render, leaving alone the block being typed in:
  // replacing it would throw the caret away.
  function reconcile(pin) {
    if (serverHtml === null) return;
    var template = document.createElement('template');
    template.innerHTML = serverHtml;
    var next = Array.prototype.slice.call(template.content.children);
    // Empty paragraphs just made on the page render to nothing, so the source has no counterpart.
    var cur = Array.prototype.filter.call(article.children, function (el) {
      return el === pin || !isEmptyParagraph(el) || range(el).start >= 0;
    });
    var p = pin ? cur.indexOf(pin) : -1;
    var pinRange = null;

    function pair(c, n) {
      if (clean(c.outerHTML) === clean(n.outerHTML)) {
        var from = n.hasAttribute('data-src-start') ? n : n.querySelector('[data-src-start]');
        var to = c.hasAttribute('data-src-start') ? c : c.querySelector('[data-src-start]');
        if (from && to) {
          to.setAttribute('data-src-start', from.getAttribute('data-src-start'));
          to.setAttribute('data-src-end', from.getAttribute('data-src-end'));
        }
      } else {
        article.replaceChild(n, c);
      }
    }

    if (p < 0 || next.length < cur.length - 1) {
      var count = Math.min(next.length, cur.length);
      for (var i = 0; i < count; i++) pair(cur[i], next[i]);
      for (var j = count; j < next.length; j++) article.appendChild(next[j]);
      for (var k = count; k < cur.length; k++) article.removeChild(cur[k]);
      snapshot(null, null);
      return;
    }

    var tail = cur.length - p - 1;
    var midEnd = next.length - tail;
    for (var a = 0; a < p; a++) pair(cur[a], next[a]);
    for (var b = 0; b < tail; b++) pair(cur[p + 1 + b], next[midEnd + b]);

    var mid = next.slice(p, midEnd);
    if (mid.length) {
      pinRange = { start: range(mid[0]).start, end: range(mid[mid.length - 1]).end };
    } else {
      var position = next[midEnd] ? range(next[midEnd]).start : docLength;
      pinRange = { start: position, end: position };
    }
    snapshot(pin, pinRange);
  }

  // --------------- editing behaviour ---------------

  function topBlock(node) {
    while (node && node.parentNode !== article) node = node.parentNode;
    return node && node.nodeType === 1 ? node : null;
  }

  function onSelectionChange() {
    if (!active) return;
    var sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !article.contains(sel.anchorNode)) return;
    savedRange = sel.getRangeAt(0).cloneRange();
    var b = topBlock(sel.anchorNode);
    if (b === caretBlock) return;
    caretBlock = b;
    if (!inFlight && !timer && !findRun()) reconcile(caretBlock);
  }

  function onInput() {
    var sel = window.getSelection();
    var n = sel && sel.anchorNode;
    // Text typed straight into the page, for example after deleting everything, needs a block.
    if (n && (n === article || (n.nodeType === 3 && n.parentNode === article))) {
      document.execCommand('formatBlock', false, 'p');
    }
    window.mdPost({ type: 'edit-dirty' });
    schedule();
  }

  function caretIn(tag) {
    var sel = window.getSelection();
    var n = sel && sel.anchorNode;
    while (n && n !== article) {
      if (n.nodeType === 1 && n.tagName === tag) return n;
      n = n.parentNode;
    }
    return null;
  }

  function onKeyDown(e) {
    if (e.ctrlKey && !e.altKey && (e.key === 'z' || e.key === 'y' || e.key === 'Z')) {
      e.preventDefault();
      if (timer) { clearTimeout(timer); timer = null; }
      commit();
      window.mdPost({ type: e.key === 'y' || e.shiftKey ? 'edit-redo' : 'edit-undo' });
      return;
    }
    if (e.key === 'Tab' && !e.ctrlKey && !e.altKey && moveInTable(e.shiftKey)) {
      e.preventDefault();
      return;
    }
    if (e.key === 'Enter' && !e.ctrlKey) {
      if (caretIn('PRE')) {
        e.preventDefault();
        insertNewline();
      } else if (caretIn('TABLE')) {
        e.preventDefault();
      } else if (leaveQuote()) {
        e.preventDefault();
      }
    }
  }

  // Tab moves through a table cell by cell; from the last cell it adds a row.
  function moveInTable(backwards) {
    var cell = caretIn('TD') || caretIn('TH');
    if (!cell) return false;
    var cells = Array.prototype.slice.call(caretIn('TABLE').querySelectorAll('th, td'));
    var target = cells[cells.indexOf(cell) + (backwards ? -1 : 1)];
    if (target) {
      selectCell(target);
    } else if (!backwards) {
      tableEdit('row-add');
      onInput();
    }
    return true;
  }

  // Selecting the contents means typing replaces them, as in a spreadsheet.
  function selectCell(cell) {
    var r = document.createRange();
    r.selectNodeContents(cell);
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(r);
    savedRange = r.cloneRange();
  }

  function insertNewline() {
    var sel = window.getSelection();
    var r = sel.getRangeAt(0);
    r.deleteContents();
    var text = document.createTextNode('\n');
    r.insertNode(text);
    // A final newline is not drawn, so the caret after it would sit on the line above.
    if (!text.nextSibling || !text.nextSibling.nodeValue) {
      text.parentNode.insertBefore(document.createTextNode('\n'), text.nextSibling);
    }
    r.setStartAfter(text);
    r.collapse(true);
    sel.removeAllRanges();
    sel.addRange(r);
    onInput();
  }

  // Enter on an empty last line of a quote steps out of it, as an empty list item does.
  function leaveQuote() {
    var quote = caretIn('BLOCKQUOTE');
    var p = caretIn('P');
    if (!quote || !p || p.parentNode !== quote || p !== quote.lastElementChild) return false;
    if (p.textContent.trim() || quote.children.length < 2) return false;
    quote.removeChild(p);
    var after = emptyParagraph();
    article.insertBefore(after, quote.nextSibling);
    placeCaret(after, false);
    onInput();
    return true;
  }

  function onPaste(e) {
    e.preventDefault();
    var data = e.clipboardData;
    var hasFile = Array.prototype.some.call(data.items, function (i) { return i.kind === 'file'; });
    var text = data.getData('text/plain');
    // Image data and files copied in Explorer are the host's to read from the clipboard.
    if (hasFile) {
      window.mdPost({ type: 'edit-paste-image' });
      return;
    }
    if (/^(https?:\/\/|mailto:)\S+$/i.test(text.trim()) && !window.getSelection().isCollapsed) {
      document.execCommand('createLink', false, text.trim());
      return;
    }
    document.execCommand('insertText', false, text);
  }

  function onClick(e) {
    var b = topBlock(e.target);
    if (e.target === article || e.target === document.body || e.target === document.documentElement) {
      // Only the space below the document starts a new paragraph, not the gaps between blocks.
      var last = article.lastElementChild;
      if (!last || e.clientY > last.getBoundingClientRect().bottom) appendParagraphIfNeeded();
      return;
    }
    if (b && b.classList.contains('md-locked')) {
      var at = b;
      while (at && range(at).start < 0) at = at.previousElementSibling;
      window.mdPost({ type: 'edit-locked', start: at ? range(at).start : -1 });
    }
  }

  function onPointerDown(e) {
    var t = e.target;
    if (t && t.tagName === 'INPUT' && t.type === 'checkbox' && article.contains(t)) {
      e.preventDefault();
      if (t.hasAttribute('checked')) t.removeAttribute('checked');
      else t.setAttribute('checked', 'checked');
      onInput();
    }
  }

  function appendParagraphIfNeeded() {
    var last = article.lastElementChild;
    if (!last || last.tagName !== 'P' || last.textContent.trim() || last.querySelector('img')) {
      last = document.createElement('p');
      last.appendChild(document.createElement('br'));
      article.appendChild(last);
    }
    placeCaret(last, true);
  }

  function placeCaret(el, atEnd) {
    var r = document.createRange();
    r.selectNodeContents(el);
    r.collapse(!atEnd);
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(r);
    article.focus();
    savedRange = r.cloneRange();
    caretBlock = topBlock(el);
  }

  function flushNow() {
    if (timer) { clearTimeout(timer); timer = null; }
    if (inFlight) return null;
    inFlight = buildCommit();
    return inFlight ? { start: inFlight.start, end: inFlight.end, md: inFlight.md } : null;
  }

  // --------------- toolbar commands ---------------

  function restore() {
    article.focus();
    if (!savedRange) { appendParagraphIfNeeded(); return; }
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(savedRange);
  }

  function currentBlock() {
    var b = topBlock(window.getSelection().anchorNode);
    if (b && !b.classList.contains('md-locked')) return b;
    appendParagraphIfNeeded();
    return topBlock(window.getSelection().anchorNode);
  }

  function retag(el, tag) {
    var n = document.createElement(tag);
    while (el.firstChild) n.appendChild(el.firstChild);
    article.replaceChild(n, el);
    placeCaret(n, true);
    return n;
  }

  function setStyle(tag) {
    var b = currentBlock();
    if (b.tagName === 'P' || HEADINGS[b.tagName] || b.tagName === 'DIV') retag(b, tag);
  }

  function setList(tag, checklist) {
    var b = currentBlock();
    if (b.tagName === 'UL' || b.tagName === 'OL') {
      var list = b.tagName === tag ? b : retag(b, tag);
      Array.prototype.forEach.call(list.children, function (li) {
        var box = li.querySelector(':scope > input');
        if (checklist && !box) li.insertBefore(makeCheckbox(), li.firstChild);
        if (!checklist && box) li.removeChild(box);
      });
      placeCaret(list.lastElementChild, true);
      return;
    }
    var host = document.createElement(tag);
    var li = document.createElement('li');
    if (checklist) li.appendChild(makeCheckbox());
    while (b.firstChild) li.appendChild(b.firstChild);
    host.appendChild(li);
    article.replaceChild(host, b);
    placeCaret(li, true);
  }

  function makeCheckbox() {
    var box = document.createElement('input');
    box.type = 'checkbox';
    box.setAttribute('disabled', 'disabled');
    return box;
  }

  function insertAfter(el, b) {
    article.insertBefore(el, b ? b.nextSibling : null);
  }

  function emptyParagraph() {
    var p = document.createElement('p');
    p.appendChild(document.createElement('br'));
    return p;
  }

  function isEmptyParagraph(b) {
    return b.tagName === 'P' && !b.textContent.trim() && !b.querySelector('img');
  }

  function insertBlock(el) {
    var b = currentBlock();
    if (isEmptyParagraph(b)) article.replaceChild(el, b);
    else insertAfter(el, b);
    var after = emptyParagraph();
    insertAfter(after, el);
    placeCaret(after, false);
  }

  function makeTable() {
    var t = document.createElement('table');
    var head = '<thead><tr><th>Column 1</th><th>Column 2</th><th>Column 3</th></tr></thead>';
    var cell = '<td><br></td>';
    var row = '<tr>' + cell + cell + cell + '</tr>';
    t.innerHTML = head + '<tbody>' + row + row + '</tbody>';
    return t;
  }

  function tableEdit(action) {
    var cell = caretIn('TD') || caretIn('TH');
    if (!cell) return false;
    var tr = cell.parentNode;
    var t = caretIn('TABLE');
    var column = Array.prototype.indexOf.call(tr.children, cell);
    var rows = Array.prototype.slice.call(t.querySelectorAll('tr'));
    if (action === 'row-add') {
      var fresh = document.createElement('tr');
      for (var i = 0; i < tr.children.length; i++) {
        var td = document.createElement('td');
        td.appendChild(document.createElement('br'));
        fresh.appendChild(td);
      }
      if (tr.parentNode.tagName === 'THEAD') t.querySelector('tbody').insertBefore(fresh, t.querySelector('tbody').firstChild);
      else tr.parentNode.insertBefore(fresh, tr.nextSibling);
      placeCaret(fresh.firstChild, false);
    } else if (action === 'row-delete' && tr.parentNode.tagName === 'TBODY') {
      tr.parentNode.removeChild(tr);
      placeCaret(t.querySelector('th'), true);
    } else if (action === 'column-add') {
      rows.forEach(function (r) {
        var head = r.parentNode.tagName === 'THEAD';
        var c = document.createElement(head ? 'th' : 'td');
        if (head) c.textContent = 'Column';
        else c.appendChild(document.createElement('br'));
        r.insertBefore(c, r.children[column + 1] || null);
      });
    } else if (action === 'column-delete' && tr.children.length > 1) {
      rows.forEach(function (r) { r.removeChild(r.children[column]); });
      placeCaret(t.querySelector('th'), true);
    }
    return true;
  }

  function toggleCode() {
    var existing = caretIn('CODE');
    if (existing && existing.parentNode.tagName !== 'PRE') {
      existing.parentNode.replaceChild(document.createTextNode(existing.textContent), existing);
      return;
    }
    var sel = window.getSelection();
    if (sel.isCollapsed) return;
    var r = sel.getRangeAt(0);
    var code = document.createElement('code');
    code.textContent = r.toString();
    r.deleteContents();
    r.insertNode(code);
  }

  function insertImage(url, alt, display) {
    var img = document.createElement('img');
    img.setAttribute('src', display || url);
    img.setAttribute('data-md-src', url);
    img.setAttribute('alt', alt || '');
    var sel = window.getSelection();
    var r = sel.rangeCount ? sel.getRangeAt(0) : null;
    if (r && article.contains(r.startContainer) && r.startContainer !== article) {
      r.deleteContents();
      r.insertNode(img);
      r.setStartAfter(img);
      r.collapse(true);
      sel.removeAllRanges();
      sel.addRange(r);
    } else {
      var p = document.createElement('p');
      p.appendChild(img);
      insertBlock(p);
    }
  }

  function run(name, arg, alt, display) {
    restore();
    switch (name) {
      case 'copy': case 'selectAll':
        document.execCommand(name);
        return; // Neither changes the document.
      case 'cut': document.execCommand('cut'); break;
      case 'insert-text': document.execCommand('insertText', false, arg); break;
      case 'bold': document.execCommand('bold'); break;
      case 'italic': document.execCommand('italic'); break;
      case 'strike': document.execCommand('strikeThrough'); break;
      case 'code': toggleCode(); break;
      case 'style': setStyle(arg); break;
      case 'bullets': setList('UL', false); break;
      case 'numbers': setList('OL', false); break;
      case 'checklist': setList('UL', true); break;
      case 'quote': {
        var b = currentBlock();
        if (b.tagName === 'BLOCKQUOTE') {
          var inner = b.firstElementChild;
          article.replaceChild(inner, b);
          placeCaret(inner, true);
        } else if (b.tagName !== 'TABLE' && b.tagName !== 'PRE') {
          var q = document.createElement('blockquote');
          article.replaceChild(q, b);
          q.appendChild(b.tagName === 'P' ? b : b);
          placeCaret(b, true);
        }
        break;
      }
      case 'codeblock': {
        var c = currentBlock();
        var pre = document.createElement('pre');
        var code = document.createElement('code');
        var isEmpty = isEmptyParagraph(c);
        code.textContent = isEmpty ? '\n' : c.textContent + '\n';
        pre.appendChild(code);
        if (c.tagName === 'P' && !isEmpty) {
          article.replaceChild(pre, c);
          insertAfter(emptyParagraph(), pre);
        } else {
          insertBlock(pre);
        }
        placeCaret(code, false);
        break;
      }
      case 'hr': insertBlock(document.createElement('hr')); break;
      case 'table': insertBlock(makeTable()); break;
      case 'link':
        if (window.getSelection().isCollapsed) document.execCommand('insertText', false, arg);
        else document.execCommand('createLink', false, arg);
        break;
      case 'image': insertImage(arg, alt, display); break;
      case 'row-add': case 'row-delete': case 'column-add': case 'column-delete':
        tableEdit(name);
        break;
    }
    onInput();
  }

  // --------------- host interface ---------------

  function onBlur() {
    if (timer) { clearTimeout(timer); commit(); }
  }

  // A drop must always be handled here: left alone, the browser would navigate to the file and
  // the document on screen would be replaced by it.
  document.addEventListener('dragover', function (e) {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  });

  document.addEventListener('drop', function (e) {
    e.preventDefault();
    if (!e.dataTransfer.files.length) return;
    if (active) {
      var at = document.caretRangeFromPoint(e.clientX, e.clientY);
      if (at && article.contains(at.startContainer) && at.startContainer !== article) {
        savedRange = at;
      }
    }
    window.chrome.webview.postMessageWithAdditionalObjects(
      JSON.stringify({ type: 'edit-drop' }), e.dataTransfer.files);
  });

  window.mdEdit = {
    setActive: function (on, length) {
      article = document.querySelector('article');
      if (on === active) { docLength = length; return; }
      active = on;
      docLength = length;
      document.documentElement.classList.toggle('md-editing', on);
      var method = on ? 'addEventListener' : 'removeEventListener';
      article[method]('input', onInput);
      article[method]('keydown', onKeyDown);
      article[method]('paste', onPaste);
      article[method]('blur', onBlur);
      document[method]('click', onClick);
      document[method]('pointerdown', onPointerDown, true);
      document[method]('selectionchange', onSelectionChange);
      if (on) {
        article.setAttribute('contenteditable', 'true');
        document.execCommand('defaultParagraphSeparator', false, 'p');
        snapshot(null, null);
      } else {
        article.removeAttribute('contenteditable');
        caretBlock = savedRange = serverHtml = null;
      }
    },
    isActive: function () { return active; },
    ack: acknowledge,
    // Replaces the page after a change that came from elsewhere, such as undo.
    reset: function (html, length) {
      if (timer) { clearTimeout(timer); timer = null; }
      inFlight = null;
      docLength = length;
      serverHtml = html;
      reconcile(null);
    },
    flush: flushNow,
    command: run
  };

  // The page says when it can be switched on, so that does not depend on navigation timing.
  window.mdPost({ type: 'edit-page-ready' });
})();
