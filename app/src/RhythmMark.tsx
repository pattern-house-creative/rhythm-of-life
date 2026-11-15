import { useId } from "react";

// The Rhythm of Life wave, drawn as a fine line for the night sky: the main
// rhythm, with a faint echo flowing beneath it inside the mark's circle.
export function RhythmMark({ size = 22 }: { size?: number }) {
  const clip = useId();
  return (
    <svg
      className="rhythm-mark"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <clipPath id={clip}>
        <circle cx="12" cy="12" r="10.5" />
      </clipPath>
      <g clipPath={`url(#${clip})`}>
        <path
          d="M0 17.5h4c.5-1.5 1.5-2.5 3-2.5s2.5 1 3 2.5 1.5 2.5 3 2.5 2.5-1 3-2.5 1.5-2.5 3-2.5 2.5 1 3 2.5h2"
          strokeWidth="1"
          opacity=".35"
        />
        <path
          d="M2 12h2c.5-3 1.5-5 3-5s2.5 2 3 5 1.5 5 3 5 2.5-2 3-5 1.5-5 3-5 2.5 2 3 5h2"
          strokeWidth="1.5"
        />
      </g>
    </svg>
  );
}
