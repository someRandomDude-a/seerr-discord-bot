export function node(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = String(text);
  return element;
}

export function formatBytes(value = 0) {
  let n = Math.max(0, Number(value) || 0);
  for (const unit of ['B', 'KiB', 'MiB', 'GiB', 'TiB']) {
    if (n < 1024 || unit === 'TiB') return `${n.toFixed(1)} ${unit}`;
    n /= 1024;
  }
}

export function reference(item) {
  return { kind: item.kind, external_id: item.external_id, title: item.title.slice(0, 100), source: item.source };
}

export function identity(item) {
  return [item.kind, item.external_id, item.id ?? '', item.source ?? ''].join(':');
}
