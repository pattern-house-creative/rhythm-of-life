import { useEffect, useRef, useState } from "react";
import { instance } from "./instance";
import { bookmarklet } from "./bookmarklet";
import "./PageBookmark.css";

// The bookmark that saves the page a person is on (SB-P7-03, bookmarklet.ts), offered in the Memories view, which shows
// only while the engine serves it (memory.tools on). It is made for the address the app is open at now, and holds that
// address and nothing else. Dragged to the bookmarks bar, or copied and pasted as a new bookmark's address where
// dragging isn't possible (a phone); clicked inside the app it does nothing.
export default function PageBookmark({
  origin = location.origin,
}: {
  origin?: string;
}) {
  const code = bookmarklet(origin);
  const link = useRef<HTMLAnchorElement>(null);
  const byHandBox = useRef<HTMLTextAreaElement>(null);
  const [said, setSaid] = useState<string | null>(null);
  const [byHand, setByHand] = useState(false);
  // React refuses a javascript: address in href, as it should for any address a page or a person supplies; this one is
  // the app's own, made from its origin alone, so it is set on the element itself.
  useEffect(() => {
    if (code) link.current?.setAttribute("href", code);
  }, [code]);
  // The way on when the browser won't copy it: the address, selected, for a person to copy.
  useEffect(() => {
    if (byHand) byHandBox.current?.focus();
  }, [byHand]);
  if (!code) return null;
  const name = instance().name;

  async function copy() {
    try {
      if (!navigator.clipboard) throw new Error("No clipboard");
      await navigator.clipboard.writeText(code!);
      setByHand(false);
      setSaid(
        "Copied. Make a new bookmark and paste this as its address (its URL).",
      );
    } catch {
      setByHand(true);
      setSaid(
        "This browser won't copy it from here: select it below and copy it.",
      );
    }
  }

  return (
    <details className="page-bookmark">
      <summary>Save pages from your browser</summary>
      <p className="page-bookmark-drag">
        Drag this to your bookmarks bar:{" "}
        <a
          ref={link}
          className="page-bookmark-link"
          onClick={(e) => {
            e.preventDefault();
            setSaid(
              "It works from the bookmarks bar, on another page: drag it there.",
            );
          }}
        >
          Save to {name}
        </a>
      </p>
      <p className="page-bookmark-hint">
        On any page, select some text if you like and click the bookmark: {name}{" "}
        opens in a new tab with the page's title, its address and what you
        selected, and saves nothing until you press Save there. The bookmark
        holds this app's address and nothing else, no key or token; what it
        brings travels after the # of that address, which browsers send to no
        server. Sign in to {name} first if it asks you to: a sign-in page would
        see what the bookmark brought, and it would not come across. A browser
        that syncs its history keeps the address it opened.
      </p>
      <div className="page-bookmark-row">
        <button type="button" onClick={() => void copy()}>
          Copy the bookmark
        </button>
        <span className="page-bookmark-said" aria-live="polite">
          {said && (
            <span role="status" className="page-bookmark-hint">
              {said}
            </span>
          )}
        </span>
      </div>
      {byHand && (
        <textarea
          ref={byHandBox}
          className="page-bookmark-code"
          aria-label="The bookmark's address, to copy by hand"
          readOnly
          rows={3}
          value={code}
          onFocus={(e) => e.currentTarget.select()}
        />
      )}
    </details>
  );
}
