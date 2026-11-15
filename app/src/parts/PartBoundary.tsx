import { Component } from "react";
import type { ReactNode } from "react";

// A part that breaks while drawing says so in its own cell, quietly; the rest of
// the grid goes on. Nothing saved changes: no layout, corner or card is touched.
// It draws again when `again` changes (a corner's card, at the next read of the
// dashboards) and, with `retry`, when someone asks.
interface Props {
  children: ReactNode;
  label?: string;
  again?: unknown;
  retry?: boolean;
}
export class PartBoundary extends Component<
  Props,
  { failed: boolean; again: unknown }
> {
  state = { failed: false, again: this.props.again };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  static getDerivedStateFromProps(
    props: Props,
    state: { failed: boolean; again: unknown },
  ) {
    return props.again === state.again
      ? null
      : { failed: false, again: props.again };
  }
  render() {
    return this.state.failed ? (
      <p className="part-quiet">
        {this.props.label ?? "This part can’t be drawn right now."}
        {this.props.retry && (
          <button
            type="button"
            className="part-again"
            onClick={() => this.setState({ failed: false })}
          >
            Try again
          </button>
        )}
      </p>
    ) : (
      this.props.children
    );
  }
}
