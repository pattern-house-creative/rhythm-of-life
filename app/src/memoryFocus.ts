import { useEffect, useRef } from "react";
import type {
  FocusEvent as ReactFocusEvent,
  KeyboardEvent as ReactKeyboardEvent,
  RefObject,
} from "react";

// Where focus goes in the memory screens (SB-P6-02). Everything here happens in one flow, never in a popup: a question opens in its row
// and its trigger often makes way for it, a row leaves its list once it is dealt with, a memory opens over the list it came from. Each
// of those would leave a keyboard or a screen reader at the top of the page, so the whole view is watched from one place, and every
// section gets this by keeping to the view's classes rather than by code of its own:
//  - a question (.memory-confirm), a form (form.memory-edit) or a revision read (.memory-version with its region role) takes focus when
//    it opens: a form its first field, the others themselves, so what they ask is read and Enter can't answer before it is;
//  - when it closes, focus goes back to what opened it, or to where that was if it has gone;
//  - a control that goes away with focus on it hands on to the same control in the row that took its place, or the row before, or the
//    view's heading once its list is empty; a control that a row offers only once something in it is done (data-then) hands on to
//    the control it names there instead, when the row that took its place has none of its own;
//  - Show more goes to the first row it brought;
//  - back from an open memory goes to the row that opened it, once its list is there again.
// Nothing moves once a person has gone on somewhere else.

// A control a keyboard reaches. Disabled ones are left in, since a step on its way disables its buttons only until it answers.
const controls =
  'button, a[href], input, select, textarea, summary, [tabindex="0"]';
const reachable = (scope: Element) =>
  [...scope.querySelectorAll<HTMLElement>(controls)].filter(
    (el) => el.getAttribute("tabindex") !== "-1",
  );
const disabled = (el: HTMLElement) => el.matches(":disabled");
const nameOf = (el: Element) =>
  (el.getAttribute("aria-label") ?? el.textContent ?? "").trim();
const asks =
  '.memory-confirm, form.memory-edit, .memory-version[role="region"]';
const rowLists = ".memory-list, .memory-history";

// Where a control was, enough to find it or its neighbour once a list has been drawn again.
export interface Place {
  el: HTMLElement;
  // The list its row is in, by its name, and the row.
  list: string | null;
  row: number;
  // Among the row's controls, or the view's if it is in no row: which of that name, and which of all.
  nth: number;
  at: number;
  name: string;
  // The control of the row that took its place to go to when that row has none of this one's name (data-then).
  then: string | null;
  // A notice's: its words and buttons go with it, so nothing there is the same control again.
  notice: boolean;
}

function placeOf(el: HTMLElement, view: Element): Place {
  let row = el.closest("li");
  for (
    let outer = row;
    outer;
    outer = outer.parentElement?.closest("li") ?? null
  )
    row = outer;
  const list = row?.parentElement ?? null;
  const label = list?.getAttribute("aria-label") ?? null;
  const scope = label && row ? row : view;
  const all = reachable(scope);
  const name = nameOf(el);
  return {
    el,
    list: label,
    row: label && list && row ? [...list.children].indexOf(row) : 0,
    at: all.indexOf(el),
    nth: all.filter((c) => nameOf(c) === name).indexOf(el),
    name,
    then: el.getAttribute("data-then"),
    notice: Boolean(el.closest(".memory-notice")),
  };
}

type Found = HTMLElement | "heading" | "wait";

// The control a place stands for now: itself, or the same one in the row that took its place. Wait: there is one, but a step on its way
// has it disabled, or the list hasn't been read yet.
function find(place: Place, view: Element, patient: boolean): Found {
  const { el } = place;
  // React may have drawn something else in the same element, so its name says whether it is still that control.
  if (el.isConnected && view.contains(el) && nameOf(el) === place.name)
    return disabled(el) ? "wait" : el;
  if (place.notice) return "heading";
  let scope: Element = view;
  if (place.list !== null) {
    const list = [
      ...view.querySelectorAll("ul[aria-label], ol[aria-label]"),
    ].find((l) => l.getAttribute("aria-label") === place.list);
    const rows = list ? list.children : null;
    if (!rows?.length) return patient ? "wait" : "heading";
    scope = rows[Math.min(place.row, rows.length - 1)];
  }
  const all = reachable(scope);
  const same = all.filter((c) => nameOf(c) === place.name);
  const then = place.then
    ? all.find((c) => nameOf(c) === place.then)
    : undefined;
  const found =
    same[Math.min(place.nth, same.length - 1)] ??
    then ??
    (place.list !== null ? all[Math.min(place.at, all.length - 1)] : undefined);
  if (!found) return "heading";
  return disabled(found) ? "wait" : found;
}

// A row's first control, or the row itself when it has none, such as a line of a memory's history.
function focusRow(row: HTMLElement) {
  const first = reachable(row).find((c) => !disabled(c));
  if (first) return first.focus();
  row.setAttribute("tabindex", "-1");
  row.focus();
}

// What a question takes focus on: a form its first field, the others themselves.
function focusQuestion(question: HTMLElement) {
  if (question.matches("form")) {
    const field = question.querySelector<HTMLElement>(
      "input:not(:disabled), select:not(:disabled), textarea:not(:disabled)",
    );
    if (field) return field.focus();
  }
  question.setAttribute("tabindex", "-1");
  question.focus();
}

export function useMemoryFocus(
  view: RefObject<HTMLElement | null>,
  heading: RefObject<HTMLElement | null>,
) {
  // The control last used, and where to go when something asks for it. Both live in refs, since nothing here draws.
  const last = useRef<Place | null>(null);
  // patient: wait for a list being read. insist: its question has closed, and focus may be on what took a button's place in its element.
  const pending = useRef<{
    place: Place;
    patient?: boolean;
    insist?: boolean;
  } | null>(null);
  const settle = useRef<() => void>(() => {});

  useEffect(() => {
    const root = view.current;
    if (!root) return;
    // The questions open at the last look. A row's buttons and its question can be one element whose class changes, so this goes by what
    // matches now and not by which elements have been seen.
    let shown = new Set<Element>(root.querySelectorAll(asks));
    let asked: { node: HTMLElement; from: Place | null } | null = null;
    // How long each list was when Show more was pressed.
    let more: Map<Element, number> | null = null;

    const lost = () => {
      const active = document.activeElement;
      return !active || active === document.body || !active.isConnected;
    };
    const land = (target: HTMLElement | "heading") => {
      if (target === "heading") heading.current?.focus({ preventScroll: true });
      else target.focus();
    };

    const run = () => {
      // Show more's new rows.
      if (more) {
        const grew = [...more].find(
          ([list, had]) => list.isConnected && list.children.length > had,
        );
        if (grew) {
          const row = grew[0].children[grew[1]] as HTMLElement;
          more = null;
          pending.current = null;
          focusRow(row);
          return;
        }
      }
      // A question that has just opened comes first, then one that has closed.
      const open = [...root.querySelectorAll<HTMLElement>(asks)];
      const fresh = open.find((q) => !shown.has(q));
      shown = new Set(open);
      if (asked && !open.includes(asked.node)) {
        const { node, from } = asked;
        asked = null;
        const within =
          node.isConnected && node.contains(document.activeElement);
        node.removeAttribute("tabindex");
        if (from && (lost() || within))
          pending.current ??= { place: from, insist: true };
      }
      if (fresh) {
        asked = { node: fresh, from: last.current };
        pending.current = null;
        focusQuestion(fresh);
        return;
      }
      // The control in use has gone, or a step has it disabled for now.
      const used = last.current;
      if (
        !pending.current &&
        used &&
        lost() &&
        (!used.el.isConnected || disabled(used.el))
      )
        pending.current = { place: used };
      if (!pending.current) return;
      if (
        !pending.current.insist &&
        !lost() &&
        document.activeElement !== heading.current
      ) {
        pending.current = null;
        return;
      }
      const found = find(
        pending.current.place,
        root,
        Boolean(pending.current.patient),
      );
      if (found === "wait") return;
      pending.current = null;
      land(found);
    };
    settle.current = run;

    const onFocus = (event: Event) => {
      const el = (event.target as Element | null)?.closest?.<HTMLElement>(
        controls,
      );
      if (!el || !root.contains(el)) return;
      last.current = placeOf(el, root);
      // A control a person has gone on to is where they are now.
      if (pending.current?.place.el !== el) pending.current = null;
    };
    const press = (event: Event) => {
      onFocus(event);
      const el = (event.target as Element | null)?.closest?.("button");
      more =
        el && root.contains(el) && /^Show (more|earlier)/.test(nameOf(el))
          ? new Map(
              [...root.querySelectorAll(rowLists)].map((list) => [
                list,
                list.children.length,
              ]),
            )
          : null;
    };
    const watcher = new MutationObserver(run);
    watcher.observe(root, {
      subtree: true,
      childList: true,
      attributes: true,
      attributeFilter: ["disabled"],
    });
    root.addEventListener("focusin", onFocus);
    root.addEventListener("click", press, true);
    return () => {
      watcher.disconnect();
      root.removeEventListener("focusin", onFocus);
      root.removeEventListener("click", press, true);
    };
  }, [view, heading]);

  return {
    // Where the control last used is now: the row a memory is being opened from.
    place: () => last.current,
    // Back to a place, waiting for its list if it is on its way; a person going on somewhere else meanwhile ends the wait.
    returnTo: (place: Place) => {
      pending.current = { place, patient: true };
      settle.current();
    },
  };
}

// The strip of sections is one stop on the way through the page, however long it is: the arrow keys, Home and End walk along it.
export function walkStrip(event: ReactKeyboardEvent<HTMLElement>) {
  const buttons = [
    ...event.currentTarget.querySelectorAll<HTMLButtonElement>("button"),
  ];
  const at = buttons.indexOf(document.activeElement as HTMLButtonElement);
  if (at < 0) return;
  const last = buttons.length - 1;
  const to =
    event.key === "ArrowRight"
      ? at === last
        ? 0
        : at + 1
      : event.key === "ArrowLeft"
        ? at === 0
          ? last
          : at - 1
        : event.key === "Home"
          ? 0
          : event.key === "End"
            ? last
            : null;
  if (to === null) return;
  event.preventDefault();
  for (const [n, button] of buttons.entries())
    button.tabIndex = n === to ? 0 : -1;
  buttons[to].focus();
}
// Leaving the strip, its one stop is the section shown again.
export function leaveStrip(event: ReactFocusEvent<HTMLElement>) {
  if (event.currentTarget.contains(event.relatedTarget)) return;
  for (const button of event.currentTarget.querySelectorAll("button"))
    button.tabIndex = button.getAttribute("aria-pressed") === "true" ? 0 : -1;
}

// Once a card on Home has gone with focus on it: the next control the page has, or failing that the one before.
export function focusAfter(gone: Element) {
  const all = reachable(document.body).filter(
    (c) => !gone.contains(c) && !disabled(c) && !c.closest("[inert], [hidden]"),
  );
  const following = (c: Element) =>
    gone.compareDocumentPosition(c) & Node.DOCUMENT_POSITION_FOLLOWING;
  for (const c of [...all.filter(following), ...all.reverse()]) {
    c.focus({ preventScroll: true });
    if (document.activeElement === c) return;
  }
}
