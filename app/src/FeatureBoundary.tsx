import { Component } from "react";
import type { ReactNode } from "react";
import { reloadFromNetwork } from "./appUpdates";
// A part of the app that can't open says so in place. One given `failed` gives way by itself
// instead (a full screen, to what is under it), so an unattended screen never stays on it.
export class FeatureBoundary extends Component<
  { children: ReactNode; close: () => void; failed?: () => void },
  { failed: boolean }
> {
  state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  componentDidCatch() {
    this.props.failed?.();
  }
  render() {
    if (this.state.failed && this.props.failed) return null;
    return this.state.failed ? (
      <section className="detail-backdrop">
        <div className="detail-panel" role="alert">
          <h2>This view could not be opened</h2>
          {/* Any render error lands here, live or demo: say only what helps. */}
          <p className="detail-prose">
            Reload to try again, or return to Home.
          </p>
          <div className="settings-actions">
            <button onClick={this.props.close}>Back to Home</button>
            <button onClick={() => void reloadFromNetwork()}>
              Reload and try again
            </button>
          </div>
        </div>
      </section>
    ) : (
      this.props.children
    );
  }
}
