// Ring and gauge geometry for the body panels.

// A point on a circle, degrees clockwise from twelve o'clock.
export function polar(cx: number, cy: number, r: number, deg: number) {
  const a = ((deg - 90) * Math.PI) / 180;
  return [cx + r * Math.cos(a), cy + r * Math.sin(a)] as const;
}
export function arcPath(
  cx: number,
  cy: number,
  r: number,
  from: number,
  to: number,
) {
  const end = Math.min(to, from + 359.99);
  const [x0, y0] = polar(cx, cy, r, from),
    [x1, y1] = polar(cx, cy, r, end);
  return `M ${x0.toFixed(2)} ${y0.toFixed(2)} A ${r} ${r} 0 ${end - from > 180 ? 1 : 0} 1 ${x1.toFixed(2)} ${y1.toFixed(2)}`;
}
