(function (root) {
  const key = s => (s.remote ? 'r:' : 'l:') + s.id;
  const all = state => [...state.local.map(s => ({ ...s, remote: false })), ...state.remote.map(s => ({ ...s, remote: true }))];
  function rectangles(state, positions) {
    return all(state).map(s => { const p = positions.find(p => key(p) === key(s)); return p && !p.hidden ? { ...s, x: p.x, y: p.y } : null; }).filter(Boolean);
  }
  function bounds(rects) {
    if (!rects.length) return { x: 0, y: 0, width: 1000, height: 1000 };
    const x = Math.min(...rects.map(s => s.x)), y = Math.min(...rects.map(s => s.y));
    return { x, y, width: Math.max(...rects.map(s => s.x + s.width)) - x, height: Math.max(...rects.map(s => s.y + s.height)) - y };
  }
  function defaults(state) {
    const positions = state.local.map(s => ({ id: s.id, remote: false, x: s.left || 0, y: s.top || 0 }));
    if (!state.remote.length) return positions;
    const main = state.local.find(s => s.primary) || state.local[0];
    const peer = state.remote[0];
    const exit = main?.route?.exit || 'left';
    const x = exit === 'left' ? main.left - peer.width - peer.left : exit === 'right' ? main.left + main.width - peer.left : main.left - peer.left;
    const y = exit === 'top' ? main.top - peer.height - peer.top : exit === 'bottom' ? main.top + main.height - peer.top : main.top + Math.round((main.height - peer.height) / 2) - peer.top;
    return positions.concat(state.remote.map(s => ({ id: s.id, remote: true, x: x + (s.left || 0), y: y + (s.top || 0) })));
  }
  function overlaps(rects) {
    for (let i = 0; i < rects.length; i++) for (let j = i + 1; j < rects.length; j++) {
      const a = rects[i], b = rects[j];
      if (a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y) return true;
    }
    return false;
  }
  function contacts(rects) {
    const result = [];
    for (let i = 0; i < rects.length; i++) for (let j = i + 1; j < rects.length; j++) {
      const a = rects[i], b = rects[j]; let side, from, to, fixed;
      if (a.x + a.width === b.x || b.x + b.width === a.x) {
        side = a.x + a.width === b.x ? 'right' : 'left';
        from = Math.max(a.y, b.y); to = Math.min(a.y + a.height, b.y + b.height); fixed = side === 'right' ? b.x : a.x;
      } else if (a.y + a.height === b.y || b.y + b.height === a.y) {
        side = a.y + a.height === b.y ? 'bottom' : 'top';
        from = Math.max(a.x, b.x); to = Math.min(a.x + a.width, b.x + b.width); fixed = side === 'bottom' ? b.y : a.y;
      }
      if (side && to > from) result.push({ a, b, side, from, to, fixed });
    }
    return result;
  }
  function snap(state, positions, moving, x, y, tolerance) {
    const rects = rectangles(state, positions), node = rects.find(s => key(s) === moving);
    if (!node) return { x, y };
    let bestX = tolerance + 1, bestY = tolerance + 1;
    let sx = x, sy = y;
    for (const other of rects.filter(s => key(s) !== moving)) {
      if (y < other.y + other.height && y + node.height > other.y) {
        for (const candidate of [other.x - node.width, other.x + other.width]) {
          const distance = Math.abs(candidate - x);
          if (distance <= tolerance && distance < bestX) { bestX = distance; sx = candidate; }
        }
      }
      if (x < other.x + other.width && x + node.width > other.x) {
        for (const candidate of [other.y - node.height, other.y + other.height]) {
          const distance = Math.abs(candidate - y);
          if (distance <= tolerance && distance < bestY) { bestY = distance; sy = candidate; }
        }
      }
      if (bestX <= tolerance) for (const candidate of [other.y, other.y + other.height - node.height, other.y + (other.height - node.height) / 2]) {
        const distance = Math.abs(candidate - y);
        if (distance <= tolerance && distance < bestY) { bestY = distance; sy = candidate; }
      }
      if (bestY <= tolerance) for (const candidate of [other.x, other.x + other.width - node.width, other.x + (other.width - node.width) / 2]) {
        const distance = Math.abs(candidate - x);
        if (distance <= tolerance && distance < bestX) { bestX = distance; sx = candidate; }
      }
    }
    return { x: Math.round(sx), y: Math.round(sy) };
  }
  const api = { key, all, rectangles, bounds, defaults, overlaps, contacts, snap };
  if (typeof module !== 'undefined') module.exports = api; else root.TecMap = api;
})(globalThis);
