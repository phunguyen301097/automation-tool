// Locate a day in whatever calendar popup is open, using only visible text and positions
// (no library-specific class names). Called with [year, month (1-12), day].
//
// Returns one of:
//   {x, y, disabled}             centre of the day cell to click
//   {nav: 'prev'|'next', x, y}   the target month is not shown; click this arrow first
//   {error, shown}               nothing usable found
([year, month, day]) => {
  const FULL = ['january', 'february', 'march', 'april', 'may', 'june', 'july',
                'august', 'september', 'october', 'november', 'december'];
  const monthIndex = w => {
    w = w.toLowerCase();
    if (w.length < 3) return -1;
    return FULL.findIndex(f => f === w || f.startsWith(w));
  };
  const visible = el => {
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) return false;
    if (r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) return false;
    const s = getComputedStyle(el);
    return s.visibility !== 'hidden' && s.display !== 'none' && parseFloat(s.opacity || '1') > 0.05;
  };
  const text = el => (el.textContent || '').trim();
  const box = el => {
    const r = el.getBoundingClientRect();
    return {left: r.left, right: r.right, top: r.top, bottom: r.bottom,
            cx: (r.left + r.right) / 2, cy: (r.top + r.bottom) / 2, w: r.width, h: r.height};
  };
  const skip = el => el.closest('select, option, script, style, noscript, aside');
  // "Leaves": elements whose text is their own (no child element carries text).
  const leaves = Array.from(document.querySelectorAll('body *')).filter(el =>
    !skip(el) && text(el) && Array.from(el.children).every(c => !text(c)) && visible(el));

  // 1. Month headers: "Sep" + "2026" in separate elements, or "September 2026" in one.
  let headers = [];
  for (const el of leaves) {
    const t = text(el);
    if (t.length > 20) continue;
    const m = t.match(/^([A-Za-z]+)\.?(?:,?\s+(\d{4}))?$/);
    if (!m) continue;
    const mi = monthIndex(m[1]);
    if (mi < 0) continue;
    const b = box(el);
    let y = m[2] ? +m[2] : null, right = b.right;
    if (!y) {
      let best = null;
      for (const e2 of leaves) {
        if (!/^\d{4}$/.test(text(e2))) continue;
        const b2 = box(e2);
        if (Math.abs(b2.cy - b.cy) < 12 && b2.left >= b.right - 2 && b2.left - b.right < 250
            && (!best || b2.left < best.b.left)) best = {b: b2, y: +text(e2)};
      }
      if (!best) continue;
      y = best.y;
      right = best.b.right;
    }
    headers.push({el, m: mi + 1, y, left: b.left, right, top: b.top, bottom: b.bottom, cy: b.cy});
  }
  if (!headers.length) return {error: 'no-calendar', shown: []};
  // Keep the row with the most month headers (the calendars sit side by side).
  const rows = [];
  for (const h of headers) {
    const row = rows.find(r => Math.abs(r[0].cy - h.cy) < 12);
    row ? row.push(h) : rows.push([h]);
  }
  rows.sort((a, b) => b.length - a.length || a[0].top - b[0].top);
  headers = rows[0].sort((a, b) => a.left - b.left);
  const shown = headers.map(h => `${h.y}-${String(h.m).padStart(2, '0')}`);
  const rowCy = headers[0].cy;

  const target = headers.findIndex(h => h.y === year && h.m === month);
  if (target < 0) {
    // 2. Target month not shown: find the arrow left of the first / right of the last header.
    const first = headers[0], last = headers[headers.length - 1];
    const dir = year * 12 + month < first.y * 12 + first.m ? 'prev' : 'next';
    const arrows = Array.from(document.querySelectorAll('body *')).filter(el => {
      if (skip(el) || !visible(el)) return false;
      const b = box(el);
      if (Math.abs(b.cy - rowCy) > 16 || b.w > 60 || b.h > 60) return false;
      return dir === 'prev' ? b.right <= first.left - 2 && first.left - b.right < 200
                            : b.left >= last.right + 2 && b.left - last.right < 250;
    }).map(box);
    if (!arrows.length) return {error: 'no-nav', dir, shown};
    arrows.sort((a, b) => dir === 'prev' ? b.right - a.right : a.left - b.left);
    return {nav: dir, x: arrows[0].cx, y: arrows[0].cy, shown};
  }

  // 3. Day cells of the target month. Prefer the month's own container: the smallest
  //    ancestor of its header holding a day grid (>= 28 numbers) and no other month header.
  const h = headers[target];
  const isDayNum = el => /^\d{1,2}$/.test(text(el)) && +text(el) >= 1 && +text(el) <= 31;
  let box3 = null;
  for (let a = h.el.parentElement; a && a !== document.body; a = a.parentElement) {
    if (headers.some((o, i) => i !== target && a.contains(o.el))) break;
    if (leaves.filter(el => a.contains(el) && isDayNum(el)).length >= 28) { box3 = a; break; }
  }
  let inColumn;
  if (box3) {
    inColumn = o => box3.contains(o.el);
  } else {
    // Fallback: split the calendars halfway between their headers.
    const centers = headers.map(q => (q.left + q.right) / 2);
    const c = centers[target];
    const lo = target > 0 ? (centers[target - 1] + c) / 2 : c - 260;
    const hi = target < headers.length - 1 ? (centers[target + 1] + c) / 2 : c + 260;
    inColumn = o => o.b.cx > lo && o.b.cx < hi;
  }
  const cells = leaves.filter(el => text(el) === String(day)).map(el => ({el, b: box(el)}))
    .filter(o => inColumn(o) && o.b.cy > h.bottom && o.b.cy < h.bottom + 420);
  if (!cells.length) return {error: 'no-day', shown};
  cells.sort((a, b) => a.b.top - b.b.top || a.b.left - b.b.left);
  // Grids pad with the previous month's last days (>= 22) at the top and the next month's
  // first days (<= 14) at the bottom, so: small numbers -> first match, large -> last match.
  const pick = day <= 15 ? cells[0] : cells[cells.length - 1];
  let disabled = false;
  for (let e = pick.el, i = 0; e && i < 4; e = e.parentElement, i++) {
    const s = getComputedStyle(e);
    const cls = String(e.className && e.className.baseVal !== undefined ? e.className.baseVal : e.className);
    if (/disabled/i.test(cls) || e.getAttribute('aria-disabled') === 'true' || e.hasAttribute('disabled')
        || s.textDecorationLine.includes('line-through')) disabled = true;
  }
  return {x: pick.b.cx, y: pick.b.cy, disabled, shown};
}
